using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiSetupStepsTests : IDisposable
{
    private readonly TempDirectory _temp = new("openclaw-local-ai-steps-");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Acquire_AdoptsHealthyExternalWithoutInstallingOrStoppingIt()
    {
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.External));
        var acquireCalled = false;
        var step = new AcquireLocalAiEngineStep(
            _ => runtime,
            (_, _, _, _) =>
            {
                acquireCalled = true;
                throw new InvalidOperationException("Artifact acquisition must not run.");
            },
            () => Architecture.X64);
        var context = CreateContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(LocalAiOwnership.External, context.LocalAiOwnership);
        Assert.False(acquireCalled);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(0, runtime.StopCount);
        Assert.False(File.Exists(new LocalAiPaths(context.LocalDataDir).ManifestPath));
    }

    [Fact]
    public async Task Acquire_FailsClosedOnUnknownOrUnhealthyPort()
    {
        var runtime = new FakeRuntime(Snapshot(
            LocalAiRuntimeState.Conflict,
            LocalAiOwnership.None,
            "The configured Ollama port is occupied by an unhealthy or unknown service."));
        var acquireCalled = false;
        var step = new AcquireLocalAiEngineStep(
            _ => runtime,
            (_, _, _, _) =>
            {
                acquireCalled = true;
                throw new InvalidOperationException();
            },
            () => Architecture.X64);

        var result = await step.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("occupied", result.Message);
        Assert.False(acquireCalled);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Acquire_InstallsPinnedArtifactAndSavesExactContainedManifest()
    {
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None));
        var context = CreateContext();
        var details = new List<SetupDetailProgressEvent>();
        context.DetailProgress += (_, value) => details.Add(value);
        var step = new AcquireLocalAiEngineStep(
            _ => runtime,
            CreateFakeArtifact,
            () => Architecture.X64);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(LocalAiOwnership.Managed, context.LocalAiOwnership);
        var paths = new LocalAiPaths(context.LocalDataDir);
        var install = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.NotNull(install);
        Assert.Equal(OllamaReleasePolicy.RecommendedVersion, install!.Manifest.EngineVersion);
        Assert.Equal("x64", install.Manifest.Architecture);
        Assert.Equal(LocalAiConfig.DefaultModel, install.Manifest.ModelTag);
        Assert.Equal(262_144, install.Manifest.ContextLength);
        Assert.Equal(paths.ModelsDirectory, install.ModelsPath);
        Assert.StartsWith(paths.RootDirectory + Path.DirectorySeparatorChar, install.ExecutablePath);
        Assert.False(Path.IsPathRooted(install.Manifest.ExecutablePath));
        Assert.False(Path.IsPathRooted(install.Manifest.ModelsPath));
        var artifactProgress = Assert.Single(details, value =>
            value.Phase == "artifact" && value.Status == "downloading");
        Assert.Equal(1024, artifactProgress.Completed);
        Assert.Equal(2048, artifactProgress.Total);
        Assert.Equal(SetupDetailProgressUnit.Bytes, artifactProgress.Unit);
    }

    [Fact]
    public async Task Download_PullsExactModelIntoManagedStoreAndReportsBytes()
    {
        var context = CreateContext();
        await CreateManagedManifestAsync(context);
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed));
        var api = new FakeOllamaApiClient();
        api.ListResponses.Enqueue([]);
        api.ListResponses.Enqueue([Model(LocalAiConfig.DefaultModel)]);
        api.PullHandler = (model, expected, progress, _) =>
        {
            if (expected is not { } expectedValue)
                throw new InvalidOperationException("Expected model bytes are required.");
            progress?.Report(new(
                "pulling layer",
                "sha256:test",
                1 * 1024 * 1024,
                expectedValue,
                1 * 1024 * 1024,
                expected,
                0.01,
                false));
            progress?.Report(new(
                "pulling layer",
                "sha256:test",
                4 * 1024 * 1024,
                expectedValue,
                4 * 1024 * 1024,
                expected,
                0.02,
                false));
            progress?.Report(new(
                "pulling layer",
                "sha256:test",
                5 * 1024 * 1024,
                expectedValue,
                5 * 1024 * 1024,
                expected,
                0.03,
                false));
            progress?.Report(new(
                "success",
                null,
                null,
                null,
                expectedValue,
                expected,
                1,
                true));
            return Task.FromResult(new OllamaPullResult(model, expectedValue, expected));
        };
        var details = new List<SetupDetailProgressEvent>();
        context.DetailProgress += (_, value) => details.Add(value);
        var step = new DownloadLocalAiModelStep(_ => runtime, _ => new(api));

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(LocalAiConfig.DefaultModel, api.PulledModel);
        Assert.Equal(LocalAiConfig.DefaultModelDownloadSizeBytes, api.ExpectedBytes);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Contains(details, value =>
            value.Phase == "model" &&
            value.Status == "pulling layer" &&
            value.Completed == 4 * 1024 * 1024 &&
            value.Unit == SetupDetailProgressUnit.Bytes);
        Assert.DoesNotContain(details, value => value.Completed == 1 * 1024 * 1024);
        Assert.DoesNotContain(details, value => value.Completed == 5 * 1024 * 1024);
        Assert.Contains(details, value => value.Status == "success" && value.Completed == api.ExpectedBytes);

        await step.RollbackAsync(context, CancellationToken.None);
        Assert.True(api.DeleteCalled);
        Assert.False(context.DownloadedManagedLocalAiModelThisRun);
        Assert.Equal(2, runtime.DisposeCount);
    }

    [Fact]
    public async Task Download_SkipsOnlyWhenExactTagAlreadyExists()
    {
        var context = CreateContext();
        await CreateManagedManifestAsync(context);
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed));
        var api = new FakeOllamaApiClient();
        api.ListResponses.Enqueue([
            Model("qwen3.6:latest"),
            Model(LocalAiConfig.DefaultModel),
        ]);
        var step = new DownloadLocalAiModelStep(_ => runtime, _ => new(api));

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Skipped, result.Outcome);
        Assert.False(api.PullCalled);
        await step.RollbackAsync(context, CancellationToken.None);
        Assert.False(api.DeleteCalled);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Download_CancellationCleansManagedPartialDataAfterRuntimeDisposal()
    {
        var context = CreateContext();
        var install = await CreateManagedManifestAsync(context);
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed));
        var api = new FakeOllamaApiClient();
        api.ListResponses.Enqueue([]);
        using var cancellation = new CancellationTokenSource();
        var partial = Path.Combine(install.ModelsPath, "blobs", "sha256-test-partial");
        api.PullHandler = (_, _, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            File.WriteAllText(partial, "partial");
            cancellation.Cancel();
            return Task.FromCanceled<OllamaPullResult>(cancellation.Token);
        };
        var step = new DownloadLocalAiModelStep(_ => runtime, _ => new(api));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(context, cancellation.Token));

        Assert.Equal(1, runtime.DisposeCount);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task Download_InternalTimeoutReturnsClearFailureAndDisposesRuntime()
    {
        var context = CreateContext();
        context.Config.LocalAi.PullTimeoutSeconds = 1;
        await CreateManagedManifestAsync(context);
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed));
        var api = new FakeOllamaApiClient();
        api.ListResponses.Enqueue([]);
        api.PullHandler = async (model, expected, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new OllamaPullResult(model, 0, expected);
        };
        var step = new DownloadLocalAiModelStep(_ => runtime, _ => new(api));

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("timed out after 1 seconds", result.Message);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Download_ExternalPullIsNeverMarkedForManagedRollback()
    {
        var context = CreateContext();
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.External));
        var api = new FakeOllamaApiClient();
        api.ListResponses.Enqueue([]);
        api.ListResponses.Enqueue([Model(LocalAiConfig.DefaultModel)]);
        var step = new DownloadLocalAiModelStep(_ => runtime, _ => new(api));

        var result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(context.DownloadedManagedLocalAiModelThisRun);
        Assert.False(api.DeleteCalled);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task VerifyWsl_UsesStdinSafeScriptAndRequiresVersionAndExactTag()
    {
        var commands = new RecordingCommandRunner
        {
            WslResult = BuildWslVerificationResult(LocalAiConfig.DefaultModel),
        };
        var context = CreateContext(commands);
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed));
        var details = new List<SetupDetailProgressEvent>();
        context.DetailProgress += (_, value) => details.Add(value);
        var step = new VerifyLocalAiWslStep(_ => runtime);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(commands.WslCalls);
        Assert.True(call.InputViaStdin);
        Assert.Equal(context.Config.Wsl.User, call.User);
        Assert.Contains("/api/version", call.Command);
        Assert.Contains("/api/tags", call.Command);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Contains(details, value => value.Phase == "verification" && value.Status == "complete");
    }

    [Fact]
    public async Task VerifyWsl_FailsWhenOnlyNonExactModelIsVisible()
    {
        var commands = new RecordingCommandRunner
        {
            WslResult = BuildWslVerificationResult("qwen3.6:latest"),
        };
        var runtime = new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.External));
        var step = new VerifyLocalAiWslStep(_ => runtime);

        var result = await step.ExecuteAsync(CreateContext(commands), CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("exact qualified model", result.Message);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task AcquireRollback_ConfirmedUninstallUsesManifestMarkerAndIsIdempotent()
    {
        var context = CreateContext();
        context.Config.ConfirmDestructive = true;
        var install = await CreateManagedManifestAsync(context);
        var engineDirectory = Path.GetDirectoryName(install.ExecutablePath)!;
        File.WriteAllText(Path.Combine(install.ModelsPath, "model.bin"), "model");
        var runtimes = new List<FakeRuntime>();
        var step = new AcquireLocalAiEngineStep(
            _ =>
            {
                var runtime = new FakeRuntime(
                    Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None),
                    Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None));
                runtimes.Add(runtime);
                return runtime;
            },
            CreateFakeArtifact,
            () => Architecture.X64);

        await step.RollbackAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.False(Directory.Exists(engineDirectory));
        Assert.False(Directory.Exists(install.ModelsPath));
        Assert.False(File.Exists(new LocalAiPaths(context.LocalDataDir).ManifestPath));
        Assert.Equal(2, runtimes.Count);
    }

    [Fact]
    public async Task AcquireRollback_RemovesOnlyInstallCreatedByThisRun()
    {
        var context = CreateContext();
        var runtimeQueue = new Queue<FakeRuntime>([
            new FakeRuntime(Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None)),
            new FakeRuntime(
                Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None),
                Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None)),
        ]);
        var step = new AcquireLocalAiEngineStep(
            _ => runtimeQueue.Dequeue(),
            CreateFakeArtifact,
            () => Architecture.X64);

        var result = await step.ExecuteAsync(context, CancellationToken.None);
        var paths = new LocalAiPaths(context.LocalDataDir);
        Assert.True(result.IsSuccess);
        Assert.True(context.CreatedManagedLocalAiInstallThisRun);
        Assert.True(File.Exists(paths.ManifestPath));

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.False(context.CreatedManagedLocalAiInstallThisRun);
        Assert.False(File.Exists(paths.ManifestPath));
        Assert.False(Directory.Exists(paths.ModelsDirectory));
    }

    [Fact]
    public async Task AcquireRollback_PreservesPreExistingManagedInstallOnTransactionalFailure()
    {
        var context = CreateContext();
        var install = await CreateManagedManifestAsync(context);
        var sentinel = Path.Combine(install.ModelsPath, "keep.bin");
        File.WriteAllText(sentinel, "pre-existing");
        context.LocalAiOwnership = LocalAiOwnership.Managed;
        var factoryCalled = false;
        var step = new AcquireLocalAiEngineStep(
            _ =>
            {
                factoryCalled = true;
                return new FakeRuntime(Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None));
            },
            CreateFakeArtifact,
            () => Architecture.X64);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.False(factoryCalled);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(new LocalAiPaths(context.LocalDataDir).ManifestPath));
    }

    [Fact]
    public async Task AcquireRollback_PreservesNonOwnedDataWithoutManifest()
    {
        var context = CreateContext();
        var nonOwned = Path.Combine(context.LocalDataDir, "LocalAI", "models", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nonOwned)!);
        File.WriteAllText(nonOwned, "keep");
        var runtime = new FakeRuntime(
            Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None),
            Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None));
        var step = new AcquireLocalAiEngineStep(_ => runtime, CreateFakeArtifact, () => Architecture.X64);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.True(File.Exists(nonOwned));
    }

    [Fact]
    public async Task AcquireRollback_NeverDeletesExternalOwnedPaths()
    {
        var context = CreateContext();
        var install = await CreateManagedManifestAsync(context);
        var sentinel = Path.Combine(install.ModelsPath, "keep.bin");
        File.WriteAllText(sentinel, "external");
        context.LocalAiOwnership = LocalAiOwnership.External;
        var factoryCalled = false;
        var step = new AcquireLocalAiEngineStep(
            _ =>
            {
                factoryCalled = true;
                return new FakeRuntime(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.External));
            },
            CreateFakeArtifact,
            () => Architecture.X64);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.False(factoryCalled);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(new LocalAiPaths(context.LocalDataDir).ManifestPath));
    }

    [Fact]
    public async Task BestEffortCleanup_ContainsUnsafePathFailuresAndPreservesOutsideData()
    {
        var context = CreateContext();
        var paths = new LocalAiPaths(context.LocalDataDir);
        paths.EnsureDirectories();
        var outsideDirectory = _temp.Combine("outside");
        Directory.CreateDirectory(outsideDirectory);
        var sentinel = Path.Combine(outsideDirectory, "keep.bin");
        File.WriteAllText(sentinel, "keep");
        var unsafeManifest = new
        {
            schemaVersion = LocalAiInstallManifest.CurrentSchemaVersion,
            engine = "ollama",
            engineVersion = OllamaReleasePolicy.RecommendedVersion,
            architecture = "x64",
            executablePath = @"engines\ollama\0.32.14\win-x64\ollama.exe",
            modelsPath = @"..\..\outside",
            modelTag = LocalAiConfig.DefaultModel,
            endpoint = LocalAiConfig.DefaultEndpoint,
            installedAtUtc = DateTimeOffset.UtcNow,
            contextLength = 262_144,
        };
        File.WriteAllText(paths.ManifestPath, JsonSerializer.Serialize(unsafeManifest));

        await LocalAiManagedStorage.TryDeletePartialModelDataAsync(context);
        LocalAiManagedStorage.TryDeleteOwnedDirectory(context, outsideDirectory);

        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(paths.ManifestPath));
    }

    [Fact]
    public async Task Acquire_RejectsUnqualifiedModelBeforeProbingOrInstalling()
    {
        var context = CreateContext();
        context.Config.LocalAi.Model = "qwen3.6:latest";
        var factoryCalled = false;
        var step = new AcquireLocalAiEngineStep(
            _ =>
            {
                factoryCalled = true;
                return new FakeRuntime(Snapshot(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None));
            },
            CreateFakeArtifact,
            () => Architecture.X64);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(factoryCalled);
    }

    private SetupContext CreateContext(ICommandRunner? commands = null)
        => new(
            new SetupConfig
            {
                LocalAi = new LocalAiConfig { Enabled = true },
            },
            new SetupLogger(null, LogLevel.Trace),
            new TransactionJournal(null),
            commands ?? new RecordingCommandRunner(),
            CancellationToken.None,
            dataDir: _temp.Combine("data"),
            localDataDir: _temp.Combine("local"));

    private async Task<LocalAiResolvedInstall> CreateManagedManifestAsync(SetupContext context)
    {
        var paths = new LocalAiPaths(context.LocalDataDir);
        var engineDirectory = Path.Combine(
            paths.EnginesDirectory,
            "ollama",
            OllamaReleasePolicy.RecommendedVersion,
            "win-x64");
        Directory.CreateDirectory(engineDirectory);
        Directory.CreateDirectory(paths.ModelsDirectory);
        var executable = Path.Combine(engineDirectory, "ollama.exe");
        File.WriteAllText(executable, "executable");
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = OllamaReleasePolicy.RecommendedVersion,
            Architecture = "x64",
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory, executable),
            ModelsPath = Path.GetRelativePath(paths.RootDirectory, paths.ModelsDirectory),
            ModelTag = LocalAiConfig.DefaultModel,
            ContextLength = 262_144,
        };
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(manifest);
        var install = await store.LoadAsync();
        Assert.NotNull(install);
        return install!;
    }

    private static Task<LocalAiArtifactInstallResult> CreateFakeArtifact(
        SetupContext context,
        Architecture architecture,
        IProgress<LocalAiArtifactInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(Architecture.X64, architecture);
        var paths = new LocalAiPaths(context.LocalDataDir);
        var engineDirectory = Path.Combine(
            paths.EnginesDirectory,
            "ollama",
            OllamaReleasePolicy.RecommendedVersion,
            "win-x64");
        Directory.CreateDirectory(engineDirectory);
        var executable = Path.Combine(engineDirectory, "ollama.exe");
        File.WriteAllText(executable, "executable");
        progress.Report(new(
            LocalAiArtifactInstallPhase.Downloading,
            1024,
            2048,
            LocalAiArtifactProgressUnit.Bytes));
        return Task.FromResult(new LocalAiArtifactInstallResult(
            OllamaReleasePolicy.RecommendedVersion,
            "win-x64",
            engineDirectory,
            executable,
            paths.ModelsDirectory,
            2048,
            new string('0', 64),
            CreatedEngineDirectory: true));
    }

    private static LocalAiRuntimeSnapshot Snapshot(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? detail = null)
        => new(
            state,
            ownership,
            new Uri(LocalAiConfig.DefaultEndpoint),
            OllamaReleasePolicy.RecommendedVersion,
            LocalAiConfig.DefaultModel,
            ownership == LocalAiOwnership.None ? null : 123,
            ownership == LocalAiOwnership.None ? null : DateTimeOffset.UtcNow,
            detail,
            DateTimeOffset.UtcNow);

    private static OllamaModelInfo Model(string tag)
        => new(tag, tag, DateTimeOffset.UtcNow, LocalAiConfig.DefaultModelDownloadSizeBytes, "sha256:test");

    private static CommandResult BuildWslVerificationResult(string model)
    {
        var version = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"version":"{{OllamaReleasePolicy.RecommendedVersion}}"}"""));
        var tags = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"models":[{"name":"{{model}}","model":"{{model}}"}]}"""));
        return new(
            0,
            $"OPENCLAW_OLLAMA_VERSION_B64={version}\nOPENCLAW_OLLAMA_TAGS_B64={tags}\n",
            "",
            TimeSpan.Zero,
            false);
    }

    private sealed class FakeRuntime : ILocalAiRuntime
    {
        private readonly LocalAiRuntimeSnapshot _ensureSnapshot;
        private readonly LocalAiRuntimeSnapshot _refreshSnapshot;

        public FakeRuntime(LocalAiRuntimeSnapshot ensureSnapshot, LocalAiRuntimeSnapshot? refreshSnapshot = null)
        {
            _ensureSnapshot = ensureSnapshot;
            _refreshSnapshot = refreshSnapshot ?? ensureSnapshot;
            Snapshot = ensureSnapshot;
        }

        public LocalAiRuntimeSnapshot Snapshot { get; private set; }
        public int EnsureCount { get; private set; }
        public int RefreshCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCount++;
            Snapshot = _ensureSnapshot;
            StateChanged?.Invoke(this, new(Snapshot));
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
            => EnsureStartedAsync(cancellationToken);

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            Snapshot = _refreshSnapshot;
            return Task.FromResult(Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOllamaApiClient : IOllamaApiClient
    {
        public Queue<IReadOnlyList<OllamaModelInfo>> ListResponses { get; } = new();
        public Func<string, long?, IProgress<OllamaPullProgress>?, CancellationToken, Task<OllamaPullResult>>?
            PullHandler { get; set; }
        public string? PulledModel { get; private set; }
        public long? ExpectedBytes { get; private set; }
        public bool PullCalled => PulledModel is not null;
        public bool DeleteCalled { get; private set; }

        public Task<OllamaVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
            => Task.FromResult(new OllamaVersionInfo(OllamaReleasePolicy.RecommendedVersion));

        public Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ListResponses.Count > 0
                ? ListResponses.Dequeue()
                : (IReadOnlyList<OllamaModelInfo>)[]);
        }

        public Task DeleteModelAsync(string model, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(LocalAiConfig.DefaultModel, model);
            DeleteCalled = true;
            return Task.CompletedTask;
        }

        public Task<OllamaPullResult> PullModelAsync(
            string model,
            long? expectedBytes,
            IProgress<OllamaPullProgress>? progress,
            CancellationToken cancellationToken)
        {
            PulledModel = model;
            ExpectedBytes = expectedBytes;
            return PullHandler?.Invoke(model, expectedBytes, progress, cancellationToken)
                ?? Task.FromResult(new OllamaPullResult(model, expectedBytes ?? 0, expectedBytes));
        }
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public CommandResult WslResult { get; init; } = new(0, "", "", TimeSpan.Zero, false);
        public List<WslCall> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) => throw new NotSupportedException();

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            ct.ThrowIfCancellationRequested();
            WslCalls.Add(new(command, user, inputViaStdin));
            return Task.FromResult(WslResult);
        }
    }

    private sealed record WslCall(string Command, string? User, bool InputViaStdin);
}
