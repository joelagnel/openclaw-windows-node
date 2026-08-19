using OpenClaw.Shared;
using System.Net;
using System.Text;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerRuntimeOptions
{
    public required LocalAiPaths Paths { get; init; }
    public Uri InitialEndpoint { get; init; } = new("http://127.0.0.1:18803/v1");
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxRestartAttempts { get; init; } = 2;
    public long MaxLogBytes { get; init; } = 8 * 1024 * 1024;
    public int LogBackupCount { get; init; } = 2;
    public int MaxLogLineCharacters { get; init; } = 16 * 1024;
}

internal interface ILlamaServerRuntimePlatform
{
    DateTimeOffset UtcNow { get; }
    WindowsTcpListenerSnapshotResult CaptureListeners();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemLlamaServerRuntimePlatform : ILlamaServerRuntimePlatform
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public WindowsTcpListenerSnapshotResult CaptureListeners() => WindowsTcpListenerSnapshot.Capture();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Owns the native llama-server router for the lifetime of the Windows companion.
/// The router starts without a model; the first inference request triggers the
/// model load defined by the verified preset.
/// </summary>
public sealed class LlamaServerRuntimeService : ILocalAiRuntime
{
    private readonly LlamaServerRuntimeOptions _options;
    private readonly LocalAiManifestStore _manifestStore;
    private readonly IOpenClawLogger _logger;
    private readonly ILocalAiManagedProcessHost _processHost;
    private readonly ILlamaServerRuntimePlatform _platform;
    private readonly ILlamaServerClient _client;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _exitTasksGate = new();
    private readonly HashSet<Task> _exitTasks = [];
    private readonly object _snapshotGate = new();
    private LocalAiRuntimeSnapshot _snapshot;
    private ILocalAiManagedProcess? _managedProcess;
    private LocalAiResolvedInstall? _install;
    private long _generation;
    private int _restartAttempts;
    private bool _stopping;
    private bool _disposed;
    private bool _acceptExitTasks = true;
    private int _disposeStarted;

    public LlamaServerRuntimeService(LlamaServerRuntimeOptions options, IOpenClawLogger? logger = null)
        : this(
            options,
            logger ?? NullLogger.Instance,
            new WindowsLocalAiManagedProcessHost(logger ?? NullLogger.Instance),
            new SystemLlamaServerRuntimePlatform(),
            new LlamaServerClient())
    {
    }

    internal LlamaServerRuntimeService(
        LlamaServerRuntimeOptions options,
        IOpenClawLogger logger,
        ILocalAiManagedProcessHost processHost,
        ILlamaServerRuntimePlatform platform,
        ILlamaServerClient client)
    {
        _options = ValidateOptions(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _manifestStore = new LocalAiManifestStore(options.Paths);
        _snapshot = LocalAiRuntimeSnapshot.Initial(options.InitialEndpoint, platform.UtcNow);
    }

    public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

    public LocalAiRuntimeSnapshot Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    public async Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<LocalAiRuntimeSnapshot> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        LocalAiResolvedInstall install = _install!;
        LlamaServerRouterLaunchPlan launchPlan;
        try
        {
            ValidateInstalledFiles(install);
            launchPlan = LlamaServerRouterConfiguration.Build(_options.Paths, install);
            await WritePresetAtomicallyAsync(launchPlan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not prepare the managed llama-server router.", ex);
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }

        EndpointObservation observed = await ObserveEndpointAsync(install, cancellationToken).ConfigureAwait(false);
        if (!observed.IsComplete)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
        if (observed.HasListeners)
        {
            if (_managedProcess is not null &&
                IsManagedOwnership(observed.Listeners, _managedProcess) &&
                observed.Probe.IsHealthy)
            {
                return PublishHealthy(observed.Probe);
            }
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "The configured llama-server port is already in use.");
        }
        if (observed.Probe.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "llama-server responded without a verifiable TCP listener.");

        observed = await ObserveEndpointAsync(install, cancellationToken).ConfigureAwait(false);
        if (!observed.IsComplete || observed.HasListeners || observed.Probe.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "The llama-server endpoint changed while startup was being prepared.");

        long generation = ++_generation;
        Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.CompanionManaged, "Starting the local AI router.");
        var spec = new LocalAiProcessStartSpec(
            install.ExecutablePath,
            Path.GetDirectoryName(install.ExecutablePath)!,
            launchPlan.Arguments,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            _options.Paths.StandardOutputLogPath,
            _options.Paths.StandardErrorLogPath,
            _options.MaxLogBytes,
            _options.LogBackupCount,
            _options.MaxLogLineCharacters);

        bool sawHealthyWithoutListener = false;
        try
        {
            _managedProcess = await _processHost.StartProcessAsync(
                    spec,
                    exit => OnManagedProcessExited(generation, exit),
                    cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset deadline = _platform.UtcNow + _options.StartupTimeout;
            while (_platform.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_managedProcess.HasExited)
                    throw new InvalidOperationException("Managed llama-server exited during startup.");

                observed = await ObserveEndpointAsync(install, cancellationToken).ConfigureAwait(false);
                if (!observed.IsComplete)
                    return await FailStartupAsync(LocalAiRuntimeState.Conflict, "TCP listener ownership could not be determined.").ConfigureAwait(false);
                if (observed.HasListeners)
                {
                    if (!IsManagedOwnership(observed.Listeners, _managedProcess))
                        return await FailStartupAsync(LocalAiRuntimeState.Conflict, "Another process owns the configured llama-server endpoint.").ConfigureAwait(false);
                    if (observed.Probe.IsHealthy)
                        return PublishHealthy(observed.Probe);
                }
                else if (observed.Probe.IsHealthy)
                {
                    sawHealthyWithoutListener = true;
                }

                await _platform.DelayAsync(_options.HealthPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return await FailStartupAsync(
                    sawHealthyWithoutListener ? LocalAiRuntimeState.Conflict : LocalAiRuntimeState.Failed,
                    sawHealthyWithoutListener
                        ? "llama-server responded without a verifiable TCP listener."
                        : "The local AI router did not become healthy before the startup timeout.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, "Local AI startup was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.Error("Managed llama-server startup failed.", ex);
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }
    }

    private async Task<LocalAiRuntimeSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        try
        {
            ValidateInstalledFiles(_install!);
        }
        catch (InvalidDataException ex)
        {
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }

        EndpointObservation observed = await ObserveEndpointAsync(_install!, cancellationToken).ConfigureAwait(false);
        if (!observed.IsComplete)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
        if (observed.HasListeners)
        {
            if (_managedProcess is null || !IsManagedOwnership(observed.Listeners, _managedProcess))
                return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "Another process owns the configured llama-server endpoint.");
            return observed.Probe.IsHealthy
                ? PublishHealthy(observed.Probe)
                : Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.CompanionManaged, "The local AI router is not healthy yet.", _managedProcess.ProcessId, _managedProcess.StartedAtUtc);
        }
        if (observed.Probe.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "llama-server responded without a verifiable TCP listener.");
        if (_managedProcess is { HasExited: false })
            return Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.CompanionManaged, "The local AI router has not opened its endpoint yet.", _managedProcess.ProcessId, _managedProcess.StartedAtUtc);
        return Publish(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            null,
            modelState: LocalAiModelAvailabilityState.Verified);
    }

    private async Task<bool> TryLoadInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            _install = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not load the local AI installation manifest.", ex);
            Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
            return false;
        }

        if (_install is not null)
            return true;
        Publish(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None, "Local AI is not installed.");
        return false;
    }

    private static void ValidateInstalledFiles(LocalAiResolvedInstall install)
    {
        if (!File.Exists(install.ExecutablePath))
            throw new InvalidDataException("The managed llama-server executable is missing.");
        var model = new FileInfo(install.ModelPath);
        if (!model.Exists || model.Length != install.Manifest.ModelAsset.SizeBytes)
            throw new InvalidDataException("The managed GGUF model is missing or has an unexpected size.");
    }

    private async Task WritePresetAtomicallyAsync(
        LlamaServerRouterLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        _options.Paths.EnsureDirectories();
        string temporaryPath = Path.Combine(
            _options.Paths.RootDirectory,
            $".{Path.GetFileName(plan.PresetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _ = _options.Paths.ResolveContainedPath(Path.GetFileName(temporaryPath), nameof(temporaryPath));
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(plan.PresetContent);
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            _ = _options.Paths.ResolveContainedPath(
                Path.GetRelativePath(_options.Paths.RootDirectory, plan.PresetPath),
                nameof(plan.PresetPath));
            File.Move(temporaryPath, plan.PresetPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { }
        }
    }

    private async Task<LocalAiRuntimeSnapshot> StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_managedProcess is null)
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);

        _stopping = true;
        ++_generation;
        Publish(LocalAiRuntimeState.Stopping, LocalAiOwnership.CompanionManaged, "Stopping the local AI router.", _managedProcess.ProcessId, _managedProcess.StartedAtUtc);
        try
        {
            await DisposeManagedProcessAsync(cancellationToken).ConfigureAwait(false);
            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }
        finally
        {
            _stopping = false;
        }
    }

    private async Task<LocalAiRuntimeSnapshot> FailStartupAsync(LocalAiRuntimeState state, string detail)
    {
        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        return Publish(state, LocalAiOwnership.None, detail);
    }

    private async Task DisposeManagedProcessAsync(CancellationToken cancellationToken)
    {
        ILocalAiManagedProcess? process = _managedProcess;
        _managedProcess = null;
        if (process is null)
            return;
        try
        {
            await process.StopAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnManagedProcessExited(long generation, LocalAiManagedProcessExit exit)
    {
        Task exitTask;
        lock (_exitTasksGate)
        {
            if (!_acceptExitTasks)
                return;
            exitTask = Task.Run(() => HandleManagedProcessExitedAsync(generation, exit));
            _exitTasks.Add(exitTask);
        }
        _ = RemoveCompletedExitTaskAsync(exitTask);
    }

    private async Task HandleManagedProcessExitedAsync(long generation, LocalAiManagedProcessExit exit)
    {
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _stopping || generation != _generation)
                    return;
                ILocalAiManagedProcess? exited = _managedProcess;
                _managedProcess = null;
                if (exited is not null)
                    await exited.DisposeAsync().ConfigureAwait(false);
                Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    $"Managed llama-server exited unexpectedly{(exit.ExitCode.HasValue ? $" with code {exit.ExitCode.Value}" : string.Empty)}.");
                if (_restartAttempts >= _options.MaxRestartAttempts)
                    return;
                _restartAttempts++;
            }
            finally
            {
                _operationGate.Release();
            }

            await _platform.DelayAsync(_options.RestartDelay, CancellationToken.None).ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed && !_stopping && generation == _generation)
                    await EnsureStartedCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Managed llama-server automatic restart failed.", ex);
        }
    }

    private async Task RemoveCompletedExitTaskAsync(Task exitTask)
    {
        await exitTask.ConfigureAwait(false);
        lock (_exitTasksGate)
            _exitTasks.Remove(exitTask);
    }

    private async Task<EndpointObservation> ObserveEndpointAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken)
    {
        WindowsTcpListenerSnapshotResult snapshot = _platform.CaptureListeners();
        WindowsTcpListenerInfo[] listeners = snapshot.Listeners.Where(
            listener => listener.Port == install.Endpoint.Port &&
                (listener.Address.Equals(IPAddress.Loopback) || listener.Address.Equals(IPAddress.Any)))
            .ToArray();
        LlamaServerRouterProbeResult probe = await _client.ProbeRouterAsync(
                install.Endpoint,
                install.Manifest.ModelAlias,
                install.ModelPath,
                cancellationToken)
            .ConfigureAwait(false);
        return new EndpointObservation(snapshot.Ipv4Complete, listeners, probe);
    }

    private LocalAiRuntimeSnapshot PublishHealthy(LlamaServerRouterProbeResult probe) =>
        Publish(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            probe.Detail,
            _managedProcess?.ProcessId,
            _managedProcess?.StartedAtUtc,
            probe.ModelState);

    private static bool IsManagedOwnership(
        IReadOnlyList<WindowsTcpListenerInfo> listeners,
        ILocalAiManagedProcess process) =>
        listeners.Count > 0 && listeners.All(listener =>
            listener.ProcessId == process.ProcessId &&
            listener.ProcessStartTimeUtc is { } started &&
            Math.Abs((started - process.StartedAtUtc.UtcDateTime).TotalSeconds) < 1);

    private LocalAiRuntimeSnapshot Publish(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? detail,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null,
        LocalAiModelAvailabilityState modelState = LocalAiModelAvailabilityState.Unknown)
    {
        DateTimeOffset now = _platform.UtcNow;
        if (state == LocalAiRuntimeState.NotInstalled)
            modelState = LocalAiModelAvailabilityState.NotInstalled;
        LocalAiModelEvidence evidence = BuildModelEvidence(modelState, now);
        var value = new LocalAiRuntimeSnapshot(
            state,
            ownership,
            _install?.Endpoint ?? _options.InitialEndpoint,
            _install?.Manifest.EngineVersion,
            _install?.Manifest.ModelCatalogId,
            evidence,
            processId,
            processStartedAtUtc,
            detail,
            now);
        lock (_snapshotGate)
            _snapshot = value;

        EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? handler = StateChanged;
        if (handler is not null)
        {
            foreach (EventHandler<LocalAiRuntimeSnapshotChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, new(value)); }
                catch (Exception ex) { _logger.Warn($"A local AI state observer failed: {Sanitize(ex.Message)}"); }
            }
        }
        return value;
    }

    private LocalAiModelEvidence BuildModelEvidence(
        LocalAiModelAvailabilityState state,
        DateTimeOffset now) => state switch
        {
            LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
            LocalAiModelAvailabilityState.Verified when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes),
            LocalAiModelAvailabilityState.Loaded when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes,
                _install.Manifest.ModelAlias),
            _ => LocalAiModelEvidence.Unknown(now),
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Task[] exitTasks;
        lock (_exitTasksGate)
        {
            _acceptExitTasks = false;
            exitTasks = [.. _exitTasks];
        }

        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;
                _stopping = true;
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                _disposed = true;
                _client.Dispose();
            }
            finally
            {
                _stopping = false;
                _operationGate.Release();
            }
        }
        finally
        {
            await Task.WhenAll(exitTasks).ConfigureAwait(false);
            _operationGate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static LlamaServerRuntimeOptions ValidateOptions(LlamaServerRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        if (!options.InitialEndpoint.IsAbsoluteUri ||
            options.InitialEndpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(options.InitialEndpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            options.InitialEndpoint.Port is <= 0 or > 65535 ||
            !string.Equals(options.InitialEndpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Query) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Fragment) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.UserInfo))
        {
            throw new ArgumentException("The initial local AI endpoint must use an explicit IPv4 loopback port.", nameof(options));
        }
        if (options.StartupTimeout <= TimeSpan.Zero ||
            options.HealthPollInterval <= TimeSpan.Zero ||
            options.ShutdownTimeout <= TimeSpan.Zero ||
            options.RestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentException("Runtime timeouts must be positive.", nameof(options));
        }
        if (options.MaxRestartAttempts < 0 ||
            options.MaxLogBytes <= 0 ||
            options.LogBackupCount < 0 ||
            options.MaxLogLineCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Runtime limits are invalid.");
        }
        return options;
    }

    private static string Sanitize(string value) => TokenSanitizer.SanitizeLogMessage(value);

    private sealed record EndpointObservation(
        bool IsComplete,
        IReadOnlyList<WindowsTcpListenerInfo> Listeners,
        LlamaServerRouterProbeResult Probe)
    {
        public bool HasListeners => Listeners.Count > 0;
    }
}
