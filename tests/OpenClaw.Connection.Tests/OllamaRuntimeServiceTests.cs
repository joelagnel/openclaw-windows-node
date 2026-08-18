using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using System.Net;

namespace OpenClaw.Connection.Tests;

public sealed class OllamaRuntimeServiceTests
{
    [Fact]
    public async Task EnsureStarted_NoManifestReportsNotInstalledWithoutSpawning()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var platform = new FakePlatform();
        using var health = new FakeHealth(_ => new(false));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.NotInstalled, snapshot.State);
        Assert.Equal(LocalAiModelAvailabilityState.NotInstalled, snapshot.ModelAvailability);
        Assert.Equal(0, health.ModelProbeCount);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_HealthyExistingDaemonIsExternalAndIsNeverStopped()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var platform = new FakePlatform
        {
            ListenerFactory = () => Complete(new WindowsTcpListenerInfo(IPAddress.Loopback, 11434, 900, "ollama", @"C:\\Program Files\\Ollama\\ollama.exe", DateTime.UtcNow)),
        };
        using var health = new FakeHealth(_ => new(true, "0.11.7"));
        await using var runtime = CreateRuntime(temp, platform, health);

        var started = await runtime.EnsureStartedAsync();
        var stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiOwnership.External, started.Ownership);
        Assert.Null(started.ModelTag);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, started.ModelAvailability);
        Assert.Equal(LocalAiOwnership.External, stopped.Ownership);
        Assert.Equal(LocalAiRuntimeState.Healthy, stopped.State);
        Assert.Equal(0, health.ModelProbeCount);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_OccupiedUnhealthyPortFailsClosedWithoutSpawning()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var platform = new FakePlatform
        {
            ListenerFactory = () => Complete(new WindowsTcpListenerInfo(IPAddress.Loopback, 11434, 901, "unknown", null, null)),
        };
        using var health = new FakeHealth(_ => new(false));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_UnknownListenerSnapshotFailsClosedWithoutSpawning()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var platform = new FakePlatform
        {
            ListenerFactory = () => new([], Ipv4Complete: false, Ipv6Complete: true),
        };
        using var health = new FakeHealth(_ => new(false));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public async Task EnsureStarted_ManagedInstallUsesPrivateEnvironmentAndExactListenerOwnership()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var paths = await InstallAsync(temp);
        var platform = new FakePlatform();
        platform.ListenerFactory = () => platform.Process is null
            ? Complete()
            : Complete(new WindowsTcpListenerInfo(
                IPAddress.Loopback,
                11434,
                platform.Process.ProcessId,
                "ollama",
                Path.Combine(paths.RootDirectory, "engines", "ollama", "ollama.exe"),
                platform.Process.StartedAtUtc.UtcDateTime));
        using var health = new FakeHealth(_ => new(platform.Process is not null, platform.Process is null ? null : "0.11.7"));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(LocalAiOwnership.Managed, snapshot.Ownership);
        Assert.Equal("qwen3:8b", snapshot.ModelTag);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, snapshot.ModelAvailability);
        Assert.Equal(1, health.ModelProbeCount);
        Assert.Equal(1, platform.StartCount);
        Assert.Equal("127.0.0.1:11434", platform.LastSpec!.Environment["OLLAMA_HOST"]);
        Assert.Equal(paths.ModelsDirectory, platform.LastSpec.Environment["OLLAMA_MODELS"]);
        Assert.Equal("262144", platform.LastSpec.Environment["OLLAMA_CONTEXT_LENGTH"]);
        Assert.Equal("1", platform.LastSpec.Environment["OLLAMA_FLASH_ATTENTION"]);
        Assert.Equal("f16", platform.LastSpec.Environment["OLLAMA_KV_CACHE_TYPE"]);
        Assert.Equal("1", platform.LastSpec.Environment["OLLAMA_NUM_PARALLEL"]);
        Assert.Equal("1", platform.LastSpec.Environment["OLLAMA_MAX_LOADED_MODELS"]);
        Assert.Equal("10m", platform.LastSpec.Environment["OLLAMA_KEEP_ALIVE"]);
        Assert.Equal("cuda_v13", platform.LastSpec.Environment["OLLAMA_LLM_LIBRARY"]);
    }

    [Fact]
    public async Task EnsureStarted_HealthyProbeBeforeListenerSnapshotRetriesOwnershipProof()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var paths = await InstallAsync(temp);
        var platform = new FakePlatform();
        var postStartCaptures = 0;
        platform.ListenerFactory = () =>
        {
            if (platform.Process is null)
                return Complete();

            postStartCaptures++;
            return postStartCaptures == 1
                ? Complete()
                : Complete(new WindowsTcpListenerInfo(
                    IPAddress.Loopback,
                    11434,
                    platform.Process.ProcessId,
                    "ollama",
                    Path.Combine(paths.RootDirectory, "engines", "ollama", "ollama.exe"),
                    platform.Process.StartedAtUtc.UtcDateTime));
        };
        using var health = new FakeHealth(_ => new(
            platform.Process is not null,
            platform.Process is null ? null : "0.11.7"));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(LocalAiOwnership.Managed, snapshot.Ownership);
        Assert.Equal(2, postStartCaptures);
        Assert.Equal(0, platform.Process!.StopCount);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.NotInstalled)]
    [InlineData(LocalAiModelAvailabilityState.Downloaded)]
    [InlineData(LocalAiModelAvailabilityState.Loaded)]
    public async Task EnsureStarted_HealthyManagedPublishesExactModelEvidence(
        LocalAiModelAvailabilityState expectedAvailability)
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var paths = await InstallAsync(temp);
        var platform = new FakePlatform();
        platform.ListenerFactory = () => platform.Process is null
            ? Complete()
            : Complete(new WindowsTcpListenerInfo(
                IPAddress.Loopback,
                11434,
                platform.Process.ProcessId,
                "ollama",
                Path.Combine(paths.RootDirectory, "engines", "ollama", "ollama.exe"),
                platform.Process.StartedAtUtc.UtcDateTime));
        using var health = new FakeHealth(
            _ => new(platform.Process is not null, platform.Process is null ? null : "0.11.7"),
            (_, exactModelTag) =>
            {
                Assert.Equal("qwen3:8b", exactModelTag);
                return expectedAvailability;
            });
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(expectedAvailability, snapshot.ModelAvailability);
        Assert.Equal(1, health.ModelProbeCount);
    }

    [Fact]
    public async Task Refresh_ManifestAloneNeverInfersModelAvailability()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        await InstallAsync(temp);
        var platform = new FakePlatform();
        using var health = new FakeHealth(_ => new(false));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, snapshot.State);
        Assert.Equal("qwen3:8b", snapshot.ModelTag);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, snapshot.ModelAvailability);
        Assert.Equal(0, health.ModelProbeCount);
    }

    [Fact]
    public async Task EnsureStarted_ModelEvidenceFailureDoesNotDowngradeHealthyEngine()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var paths = await InstallAsync(temp);
        var platform = new FakePlatform();
        platform.ListenerFactory = () => platform.Process is null
            ? Complete()
            : Complete(new WindowsTcpListenerInfo(
                IPAddress.Loopback,
                11434,
                platform.Process.ProcessId,
                "ollama",
                Path.Combine(paths.RootDirectory, "engines", "ollama", "ollama.exe"),
                platform.Process.StartedAtUtc.UtcDateTime));
        using var health = new FakeHealth(
            _ => new(platform.Process is not null, "0.11.7"),
            (_, _) => LocalAiModelAvailabilityState.Unknown);
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(LocalAiOwnership.Managed, snapshot.Ownership);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, snapshot.ModelAvailability);
    }

    [Fact]
    public async Task EnsureStarted_PortTakenDuringLaunchStopsManagedProcessAndReportsConflict()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        await InstallAsync(temp);
        var platform = new FakePlatform();
        platform.ListenerFactory = () => platform.Process is null
            ? Complete()
            : Complete(new WindowsTcpListenerInfo(IPAddress.Loopback, 11434, 9999, "other", null, DateTime.UtcNow));
        using var health = new FakeHealth(_ => new(platform.Process is not null, "0.11.7"));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(1, platform.Process!.StopCount);
    }

    [Fact]
    public async Task EnsureStarted_MissingManagedExecutableReportsFailure()
    {
        using var temp = new TempDirectory("local-ai-runtime-");
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(LocalAiManifestStoreTests.ValidManifest());
        var platform = new FakePlatform();
        using var health = new FakeHealth(_ => new(false));
        await using var runtime = CreateRuntime(temp, platform, health);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Contains("missing", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, platform.StartCount);
    }

    [Fact]
    public void BoundedLogWriter_RotatesAndLimitsBackups()
    {
        using var temp = new TempDirectory("local-ai-log-");
        var path = temp.Combine("ollama.log");
        using var writer = new BoundedRotatingLogWriter(path, 1024, 2, 2048, NullLogger.Instance);

        for (var i = 0; i < 8; i++) writer.WriteLine(new string((char)('a' + i), 700));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.True(File.Exists(path + ".2"));
        Assert.False(File.Exists(path + ".3"));
        Assert.True(new FileInfo(path).Length <= 1024);
    }

    private static OllamaRuntimeService CreateRuntime(TempDirectory temp, FakePlatform platform, FakeHealth health) =>
        new(
            new OllamaRuntimeOptions
            {
                Paths = new LocalAiPaths(temp.Path),
                StartupTimeout = TimeSpan.FromSeconds(2),
                HealthPollInterval = TimeSpan.FromMilliseconds(10),
                RestartDelay = TimeSpan.Zero,
            },
            NullLogger.Instance,
            platform,
            health);

    private static async Task<LocalAiPaths> InstallAsync(TempDirectory temp)
    {
        var paths = new LocalAiPaths(temp.Path);
        var executable = paths.ResolveContainedPath(LocalAiManifestStoreTests.ValidManifest().ExecutablePath, "executable");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllBytesAsync(executable, [0]);
        await new LocalAiManifestStore(paths).SaveAsync(LocalAiManifestStoreTests.ValidManifest());
        return paths;
    }

    private static WindowsTcpListenerSnapshotResult Complete(params WindowsTcpListenerInfo[] listeners) => new(listeners, true, true);

    private sealed class FakeHealth : ILocalAiHealthClient
    {
        private readonly Func<Uri, LocalAiHealthResult> _probe;
        private readonly Func<Uri, string, LocalAiModelAvailabilityState>? _modelProbe;

        public FakeHealth(
            Func<Uri, LocalAiHealthResult> probe,
            Func<Uri, string, LocalAiModelAvailabilityState>? modelProbe = null)
        {
            _probe = probe;
            _modelProbe = modelProbe;
        }

        public int ModelProbeCount { get; private set; }

        public Task<LocalAiHealthResult> ProbeAsync(Uri endpoint, CancellationToken cancellationToken) =>
            Task.FromResult(_probe(endpoint));

        public Task<LocalAiModelAvailabilityState> ProbeModelAvailabilityAsync(
            Uri endpoint,
            string exactModelTag,
            CancellationToken cancellationToken)
        {
            ModelProbeCount++;
            return Task.FromResult(
                _modelProbe?.Invoke(endpoint, exactModelTag) ?? LocalAiModelAvailabilityState.Unknown);
        }

        public void Dispose() { }
    }

    private sealed class FakePlatform : ILocalAiRuntimePlatform
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        public Func<WindowsTcpListenerSnapshotResult> ListenerFactory { get; set; } = () => Complete();
        public FakeProcess? Process { get; private set; }
        public LocalAiProcessStartSpec? LastSpec { get; private set; }
        public int StartCount { get; private set; }
        public DateTimeOffset UtcNow => _now;
        public WindowsTcpListenerSnapshotResult CaptureListeners() => ListenerFactory();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { _now += delay; return Task.CompletedTask; }
        public Task<ILocalAiManagedProcess> StartProcessAsync(LocalAiProcessStartSpec spec, Action<int?> exited, CancellationToken cancellationToken)
        {
            LastSpec = spec;
            StartCount++;
            Process = new FakeProcess(4242, _now, exited);
            return Task.FromResult<ILocalAiManagedProcess>(Process);
        }
    }

    private sealed class FakeProcess(int id, DateTimeOffset started, Action<int?> exited) : ILocalAiManagedProcess
    {
        public int ProcessId { get; } = id;
        public DateTimeOffset StartedAtUtc { get; } = started;
        public bool HasExited { get; private set; }
        public int StopCount { get; private set; }
        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken) { StopCount++; HasExited = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Exit(int code) { HasExited = true; exited(code); }
    }
}
