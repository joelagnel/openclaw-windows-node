using OpenClaw.Shared;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace OpenClaw.Connection.LocalAi;

internal sealed record LocalAiHealthResult(bool IsHealthy, string? Version = null);

internal interface ILocalAiHealthClient : IDisposable
{
    Task<LocalAiHealthResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken);
}

internal sealed class OllamaHealthClient : ILocalAiHealthClient
{
    private readonly HttpClient _client;

    public OllamaHealthClient()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public async Task<LocalAiHealthResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(endpoint, "/api/version");
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false);
            var body = await response.Content.ReadFromJsonAsync<OllamaVersionResponse>(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Version) ? new(false) : new(true, body.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(false);
        }
    }

    public void Dispose() => _client.Dispose();
    private sealed record OllamaVersionResponse(string? Version);
}

/// <summary>
/// Supervises a companion-owned native Ollama process while preserving healthy external Ollama daemons.
/// </summary>
public sealed class OllamaRuntimeService : ILocalAiRuntime
{
    private readonly OllamaRuntimeOptions _options;
    private readonly LocalAiManifestStore _manifestStore;
    private readonly IOpenClawLogger _logger;
    private readonly ILocalAiRuntimePlatform _platform;
    private readonly ILocalAiHealthClient _health;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private LocalAiRuntimeSnapshot _snapshot;
    private ILocalAiManagedProcess? _managedProcess;
    private LocalAiResolvedInstall? _install;
    private long _generation;
    private int _restartAttempts;
    private bool _stopping;
    private bool _disposed;

    public OllamaRuntimeService(OllamaRuntimeOptions options, IOpenClawLogger? logger = null)
        : this(options, logger ?? NullLogger.Instance, new WindowsLocalAiRuntimePlatform(logger ?? NullLogger.Instance), new OllamaHealthClient())
    {
    }

    internal OllamaRuntimeService(
        OllamaRuntimeOptions options,
        IOpenClawLogger logger,
        ILocalAiRuntimePlatform platform,
        ILocalAiHealthClient health)
    {
        _options = ValidateOptions(options);
        _logger = logger;
        _platform = platform;
        _health = health;
        _manifestStore = new LocalAiManifestStore(options.Paths);
        _snapshot = LocalAiRuntimeSnapshot.Initial(options.Endpoint, platform.UtcNow);
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
        finally { _operationGate.Release(); }
    }

    public async Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Snapshot.Ownership == LocalAiOwnership.External)
                return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<LocalAiRuntimeSnapshot> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        var observed = await ObserveEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (observed.HasListeners)
            return PublishObserved(observed);
        if (!observed.IsComplete)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
        if (observed.Health.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "Ollama responded without a verifiable TCP listener.", observed.Health.Version);

        try
        {
            _install = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Could not load the local AI installation manifest.", ex);
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }

        if (_install is null)
            return Publish(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None, "Managed Ollama is not installed.");
        if (!SameEndpoint(_install.Endpoint, _options.Endpoint))
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, "The installed Ollama endpoint does not match the configured endpoint.");
        if (!File.Exists(_install.ExecutablePath))
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, "The managed Ollama executable is missing.");

        Directory.CreateDirectory(_install.ModelsPath);
        _options.Paths.EnsureDirectories();

        // Close the check/start race as far as the OS API allows. A later ownership check remains authoritative.
        observed = await ObserveEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (observed.HasListeners || !observed.IsComplete || observed.Health.IsHealthy)
            return observed.HasListeners ? PublishObserved(observed) :
                Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "The Ollama endpoint changed while startup was being prepared.");

        var generation = ++_generation;
        Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.Managed, "Starting managed Ollama.");
        var spec = new LocalAiProcessStartSpec(
            _install.ExecutablePath,
            Path.GetDirectoryName(_install.ExecutablePath)!,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OLLAMA_HOST"] = _options.Endpoint.GetComponents(UriComponents.HostAndPort, UriFormat.Unescaped),
                ["OLLAMA_MODELS"] = _install.ModelsPath,
                ["OLLAMA_CONTEXT_LENGTH"] = _options.ContextLength.ToString(CultureInfo.InvariantCulture),
                ["OLLAMA_FLASH_ATTENTION"] = _options.FlashAttention ? "1" : "0",
                ["OLLAMA_KV_CACHE_TYPE"] = _options.KvCacheType,
                ["OLLAMA_NUM_PARALLEL"] = _options.NumParallel.ToString(CultureInfo.InvariantCulture),
                ["OLLAMA_MAX_LOADED_MODELS"] = _options.MaxLoadedModels.ToString(CultureInfo.InvariantCulture),
                ["OLLAMA_KEEP_ALIVE"] = FormatOllamaDuration(_options.KeepAlive),
                ["OLLAMA_LLM_LIBRARY"] = _options.LlmLibrary,
            },
            _options.Paths.StandardOutputLogPath,
            _options.Paths.StandardErrorLogPath,
            _options.MaxLogBytes,
            _options.LogBackupCount,
            _options.MaxLogLineCharacters);

        try
        {
            _managedProcess = await _platform.StartProcessAsync(
                spec,
                exitCode => OnManagedProcessExited(generation, exitCode),
                cancellationToken).ConfigureAwait(false);

            var deadline = _platform.UtcNow + _options.StartupTimeout;
            while (_platform.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_managedProcess.HasExited)
                    throw new InvalidOperationException("Managed Ollama exited during startup.");

                observed = await ObserveEndpointAsync(cancellationToken).ConfigureAwait(false);
                if (!observed.IsComplete)
                    return await FailStartupAsync(LocalAiRuntimeState.Conflict, "TCP listener ownership could not be determined.").ConfigureAwait(false);
                if (observed.HasListeners)
                {
                    if (!IsManagedOwnership(observed.Listeners, _managedProcess))
                        return await FailStartupAsync(LocalAiRuntimeState.Conflict, "Another process owns the configured Ollama endpoint.").ConfigureAwait(false);
                    if (observed.Health.IsHealthy)
                    {
                        return Publish(
                            LocalAiRuntimeState.Healthy,
                            LocalAiOwnership.Managed,
                            null,
                            observed.Health.Version ?? _install.Manifest.EngineVersion,
                            _managedProcess.ProcessId,
                            _managedProcess.StartedAtUtc);
                    }
                }
                else if (observed.Health.IsHealthy)
                {
                    return await FailStartupAsync(LocalAiRuntimeState.Conflict, "Ollama responded without a verifiable TCP listener.").ConfigureAwait(false);
                }

                await _platform.DelayAsync(_options.HealthPollInterval, cancellationToken).ConfigureAwait(false);
            }
            return await FailStartupAsync(LocalAiRuntimeState.Failed, "Managed Ollama did not become healthy before the startup timeout.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, "Managed Ollama startup was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.Error("Managed Ollama startup failed.", ex);
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }
    }

    private async Task<LocalAiRuntimeSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var observed = await ObserveEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (!observed.IsComplete)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
        if (observed.HasListeners)
            return PublishObserved(observed);
        if (observed.Health.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "Ollama responded without a verifiable TCP listener.", observed.Health.Version);

        if (_managedProcess is { HasExited: false })
            return Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.Managed, "Managed Ollama has not opened its endpoint yet.", processId: _managedProcess.ProcessId, processStartedAtUtc: _managedProcess.StartedAtUtc);

        try { _install = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }
        return _install is null
            ? Publish(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None, "Managed Ollama is not installed.")
            : Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, null, _install.Manifest.EngineVersion);
    }

    private async Task<LocalAiRuntimeSnapshot> StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_managedProcess is null)
        {
            if (Snapshot.Ownership == LocalAiOwnership.External)
                return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            return Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, null);
        }

        _stopping = true;
        ++_generation;
        Publish(LocalAiRuntimeState.Stopping, LocalAiOwnership.Managed, "Stopping managed Ollama.", processId: _managedProcess.ProcessId, processStartedAtUtc: _managedProcess.StartedAtUtc);
        try
        {
            await DisposeManagedProcessAsync(cancellationToken).ConfigureAwait(false);
            return Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, null);
        }
        finally { _stopping = false; }
    }

    private async Task<LocalAiRuntimeSnapshot> FailStartupAsync(LocalAiRuntimeState state, string detail)
    {
        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        return Publish(state, LocalAiOwnership.None, detail);
    }

    private async Task DisposeManagedProcessAsync(CancellationToken cancellationToken)
    {
        var process = _managedProcess;
        _managedProcess = null;
        if (process is null) return;
        try { await process.StopAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false); }
        finally { await process.DisposeAsync().ConfigureAwait(false); }
    }

    private void OnManagedProcessExited(long generation, int? exitCode)
    {
        _ = Task.Run(async () =>
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _stopping || generation != _generation)
                    return;
                var exited = _managedProcess;
                _managedProcess = null;
                if (exited is not null) await exited.DisposeAsync().ConfigureAwait(false);
                Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, $"Managed Ollama exited unexpectedly{(exitCode.HasValue ? $" with code {exitCode.Value}" : string.Empty)}.");
                if (_restartAttempts >= _options.MaxRestartAttempts)
                    return;
                _restartAttempts++;
            }
            finally { _operationGate.Release(); }

            try
            {
                await _platform.DelayAsync(_options.RestartDelay, CancellationToken.None).ConfigureAwait(false);
                await _operationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_disposed && !_stopping && generation == _generation)
                        await EnsureStartedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally { _operationGate.Release(); }
            }
            catch (Exception ex) { _logger.Error("Managed Ollama automatic restart failed.", ex); }
        });
    }

    private async Task<EndpointObservation> ObserveEndpointAsync(CancellationToken cancellationToken)
    {
        var snapshot = _platform.CaptureListeners();
        var isIpv6 = _options.Endpoint.HostNameType == UriHostNameType.IPv6;
        var complete = isIpv6 ? snapshot.Ipv6Complete : snapshot.Ipv4Complete;
        var listeners = snapshot.Listeners.Where(IsRelevantListener).ToArray();
        var health = await _health.ProbeAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);
        return new EndpointObservation(complete, listeners, health);
    }

    private bool IsRelevantListener(WindowsTcpListenerInfo listener)
    {
        if (listener.Port != _options.Endpoint.Port)
            return false;
        if (!IPAddress.TryParse(_options.Endpoint.Host, out var address))
            return IPAddress.IsLoopback(listener.Address) || listener.Address.Equals(IPAddress.Any) || listener.Address.Equals(IPAddress.IPv6Any);
        if (address.AddressFamily != listener.Address.AddressFamily)
            return false;
        return listener.Address.Equals(address) || listener.Address.Equals(IPAddress.Any) || listener.Address.Equals(IPAddress.IPv6Any);
    }

    private LocalAiRuntimeSnapshot PublishObserved(EndpointObservation observed)
    {
        if (!observed.IsComplete)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
        if (!observed.Health.IsHealthy)
            return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "The configured Ollama port is occupied by an unhealthy or unknown service.");
        if (_managedProcess is not null && IsManagedOwnership(observed.Listeners, _managedProcess))
            return Publish(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed, null, observed.Health.Version, _managedProcess.ProcessId, _managedProcess.StartedAtUtc);

        var first = observed.Listeners.First();
        return Publish(LocalAiRuntimeState.Healthy, LocalAiOwnership.External, "Using an existing healthy Ollama service.", observed.Health.Version, first.ProcessId, ToOffset(first.ProcessStartTimeUtc));
    }

    private static bool IsManagedOwnership(IReadOnlyList<WindowsTcpListenerInfo> listeners, ILocalAiManagedProcess process) =>
        listeners.Count > 0 && listeners.All(listener =>
            listener.ProcessId == process.ProcessId &&
            listener.ProcessStartTimeUtc is { } started &&
            Math.Abs((started - process.StartedAtUtc.UtcDateTime).TotalSeconds) < 1);

    private LocalAiRuntimeSnapshot Publish(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? detail,
        string? engineVersion = null,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null)
    {
        var value = new LocalAiRuntimeSnapshot(
            state,
            ownership,
            _options.Endpoint,
            engineVersion ?? _install?.Manifest.EngineVersion,
            _install?.Manifest.ModelTag,
            processId,
            processStartedAtUtc,
            detail,
            _platform.UtcNow);
        lock (_snapshotGate) _snapshot = value;
        var handler = StateChanged;
        if (handler is not null)
        {
            foreach (EventHandler<LocalAiRuntimeSnapshotChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, new(value)); }
                catch (Exception ex) { _logger.Warn($"A local AI state observer failed: {ex.Message}"); }
            }
        }
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _stopping = true;
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            _health.Dispose();
        }
        finally { _stopping = false; _operationGate.Release(); }
        _operationGate.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static OllamaRuntimeOptions ValidateOptions(OllamaRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        if (options.Endpoint.Scheme != Uri.UriSchemeHttp || !options.Endpoint.IsLoopback)
            throw new ArgumentException("The Ollama endpoint must be an HTTP loopback address.", nameof(options));
        if (options.StartupTimeout <= TimeSpan.Zero || options.HealthPollInterval <= TimeSpan.Zero || options.ShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Runtime timeouts must be positive.", nameof(options));
        if (options.MaxRestartAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Restart attempts cannot be negative.");
        if (options.ContextLength <= 0 || options.NumParallel <= 0 || options.MaxLoadedModels <= 0 || options.KeepAlive <= TimeSpan.Zero)
            throw new ArgumentException("Ollama runtime sizing values must be positive.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.KvCacheType) || string.IsNullOrWhiteSpace(options.LlmLibrary))
            throw new ArgumentException("Ollama runtime library and KV cache type are required.", nameof(options));
        return options;
    }

    private static bool SameEndpoint(Uri left, Uri right) =>
        left.Scheme == right.Scheme &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
    private static DateTimeOffset? ToOffset(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    private static string FormatOllamaDuration(TimeSpan value) =>
        value.TotalMinutes == Math.Truncate(value.TotalMinutes)
            ? $"{value.TotalMinutes.ToString(CultureInfo.InvariantCulture)}m"
            : $"{value.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s";
    private static string Sanitize(string value) => TokenSanitizer.SanitizeLogMessage(value);
    private sealed record EndpointObservation(bool IsComplete, IReadOnlyList<WindowsTcpListenerInfo> Listeners, LocalAiHealthResult Health)
    {
        public bool HasListeners => Listeners.Count > 0;
    }
}
