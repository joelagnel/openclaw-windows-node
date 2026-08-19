using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using System.Net;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerRuntimeServiceTests
{
    [Fact]
    public async Task EnsureStarted_StartsOwnedRouterWithoutLoadingModel()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        var client = new FakeClient(platform);
        await using var runtime = Runtime(paths, processHost, platform, client);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, snapshot.Ownership);
        Assert.Equal(LocalAiModelAvailabilityState.Verified, snapshot.ModelEvidence.State);
        Assert.Equal(1, processHost.StartCount);
        Assert.NotNull(processHost.LastSpec);
        Assert.DoesNotContain("--model", processHost.LastSpec.Arguments);
        Assert.DoesNotContain(paths.ResolveContainedPath(
            LocalAiManifestStoreTests.ValidManifest().ModelPath,
            nameof(LocalAiInstallManifest.ModelPath)), processHost.LastSpec.Arguments);
        Assert.True(File.Exists(paths.RouterPresetPath));
        Assert.Contains("load-on-startup = false", await File.ReadAllTextAsync(paths.RouterPresetPath), StringComparison.Ordinal);
        Assert.Equal(2, client.ProbeCountBeforeFirstStart);
    }

    [Fact]
    public async Task Stop_RemovesOwnedListenerAndProcessTree()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        await using var runtime = Runtime(paths, processHost, platform, new FakeClient(platform));
        await runtime.EnsureStartedAsync();

        LocalAiRuntimeSnapshot stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(LocalAiModelAvailabilityState.Verified, stopped.ModelEvidence.State);
        Assert.Empty(platform.Listeners);
        Assert.Equal(1, processHost.Processes.Single().StopCount);
    }

    [Fact]
    public async Task EnsureStarted_IsIdempotentAndDoesNotRestartHealthyRouter()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        await using var runtime = Runtime(paths, processHost, platform, new FakeClient(platform));

        await runtime.EnsureStartedAsync();
        LocalAiRuntimeSnapshot second = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, second.State);
        Assert.Equal(1, processHost.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_FailsClosedWhenPortHasAnotherOwner()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            18803,
            7007,
            "other-server",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var processHost = new FakeProcessHost(platform);
        await using var runtime = Runtime(paths, processHost, platform, new FakeClient(platform, healthyWhenListening: false));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(LocalAiOwnership.None, snapshot.Ownership);
        Assert.Equal(0, processHost.StartCount);

        LocalAiRuntimeSnapshot afterStop = await runtime.StopAsync();
        Assert.Equal(LocalAiRuntimeState.Conflict, afterStop.State);
        Assert.Equal(0, processHost.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_WithoutManifestReportsNotInstalled()
    {
        using var temp = new TempDirectory("llama-runtime-");
        var paths = new LocalAiPaths(temp.Path);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        await using var runtime = Runtime(paths, processHost, platform, new FakeClient(platform));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.NotInstalled, snapshot.State);
        Assert.Equal(LocalAiModelAvailabilityState.NotInstalled, snapshot.ModelEvidence.State);
        Assert.Equal(0, processHost.StartCount);
    }

    [Fact]
    public async Task Refresh_MapsLoadedModelToDigestBackedEvidence()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        var client = new FakeClient(platform);
        await using var runtime = Runtime(paths, processHost, platform, client);
        await runtime.EnsureStartedAsync();
        client.ModelState = LocalAiModelAvailabilityState.Loaded;

        LocalAiRuntimeSnapshot snapshot = await runtime.RefreshAsync();

        Assert.Equal(LocalAiModelAvailabilityState.Loaded, snapshot.ModelEvidence.State);
        Assert.Equal(LocalAiManifestStoreTests.ValidManifest().ModelAsset.Sha256, snapshot.ModelEvidence.Sha256);
        Assert.Equal(LocalAiManifestStoreTests.ValidManifest().ModelAlias, snapshot.ModelEvidence.ServerModelId);
    }

    [Fact]
    public async Task UnexpectedExit_RestartsRouterWithinBudget()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        await using var runtime = Runtime(paths, processHost, platform, new FakeClient(platform));
        await runtime.EnsureStartedAsync();

        processHost.Processes.Single().Exit(17);
        await WaitUntilAsync(() => processHost.StartCount == 2);

        Assert.Equal(LocalAiRuntimeState.Healthy, runtime.Snapshot.State);
        Assert.Equal(2, processHost.StartCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var processHost = new FakeProcessHost(platform);
        var runtime = Runtime(paths, processHost, platform, new FakeClient(platform));
        await runtime.EnsureStartedAsync();

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();

        Assert.Equal(1, processHost.Processes.Single().StopCount);
    }

    private static LlamaServerRuntimeService Runtime(
        LocalAiPaths paths,
        FakeProcessHost processHost,
        FakePlatform platform,
        FakeClient client) =>
        new(
            new LlamaServerRuntimeOptions
            {
                Paths = paths,
                HealthPollInterval = TimeSpan.FromMilliseconds(1),
                StartupTimeout = TimeSpan.FromSeconds(2),
                RestartDelay = TimeSpan.Zero,
            },
            NullLogger.Instance,
            processHost,
            platform,
            client);

    private static async Task<LocalAiPaths> PrepareInstallAsync(TempDirectory temp)
    {
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = LocalAiManifestStoreTests.ValidManifest();
        string executable = paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
        string model = paths.ResolveContainedPath(manifest.ModelPath, nameof(manifest.ModelPath));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllTextAsync(executable, "test executable");
        await using (var stream = new FileStream(model, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(manifest.ModelAsset.SizeBytes);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        return paths;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakePlatform : ILlamaServerRuntimePlatform
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        public List<WindowsTcpListenerInfo> Listeners { get; } = [];

        public WindowsTcpListenerSnapshotResult CaptureListeners() =>
            new([.. Listeners], Ipv4Complete: true, Ipv6Complete: true);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }

        public void SetOwnedListener(FakeProcess process)
        {
            Listeners.Clear();
            Listeners.Add(new WindowsTcpListenerInfo(
                IPAddress.Loopback,
                18803,
                process.ProcessId,
                "llama-server",
                @"C:\managed\llama-server.exe",
                process.StartedAtUtc.UtcDateTime));
        }
    }

    private sealed class FakeProcessHost(FakePlatform platform) : ILocalAiManagedProcessHost
    {
        public int StartCount { get; private set; }
        public LocalAiProcessStartSpec? LastSpec { get; private set; }
        public List<FakeProcess> Processes { get; } = [];

        public Task<ILocalAiManagedProcess> StartProcessAsync(
            LocalAiProcessStartSpec spec,
            Action<LocalAiManagedProcessExit> exited,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            LastSpec = spec;
            var process = new FakeProcess(4200 + StartCount, platform.UtcNow, platform, exited);
            Processes.Add(process);
            platform.SetOwnedListener(process);
            return Task.FromResult<ILocalAiManagedProcess>(process);
        }
    }

    private sealed class FakeProcess(
        int processId,
        DateTimeOffset startedAtUtc,
        FakePlatform platform,
        Action<LocalAiManagedProcessExit> exited) : ILocalAiManagedProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public bool HasExited { get; private set; }
        public int StopCount { get; private set; }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            HasExited = true;
            platform.Listeners.Clear();
            return Task.CompletedTask;
        }

        public void Exit(int exitCode)
        {
            HasExited = true;
            platform.Listeners.Clear();
            exited(new LocalAiManagedProcessExit(ProcessId, StartedAtUtc, exitCode));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient(FakePlatform platform, bool healthyWhenListening = true) : ILlamaServerClient
    {
        public int ProbeCount { get; private set; }
        public int ProbeCountBeforeFirstStart { get; private set; }
        public LocalAiModelAvailabilityState ModelState { get; set; } = LocalAiModelAvailabilityState.Verified;

        public Task<LlamaServerRouterProbeResult> ProbeRouterAsync(
            Uri endpoint,
            string modelAlias,
            string expectedModelPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            bool healthy = healthyWhenListening && platform.Listeners.Any(listener => listener.Port == endpoint.Port);
            if (!healthy)
                ProbeCountBeforeFirstStart++;
            return Task.FromResult(new LlamaServerRouterProbeResult(
                healthy,
                healthy ? ModelState : LocalAiModelAvailabilityState.Unknown,
                healthy ? expectedModelPath : null,
                null));
        }

        public void Dispose() { }
    }
}
