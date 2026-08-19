using System.Runtime.InteropServices;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiSetupStepsTests
{
    [Fact]
    public async Task Preflight_Disabled_SkipsWithoutProbing()
    {
        var probe = new FakeHardwareProbe(CreateSparkHardware(), throwOnProbe: true);
        var step = new PreflightLocalAiHardwareStep(probe, new FakePortSelector(8080));
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = false });

        Assert.True(step.CanSkip(context));
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Preflight_DefaultSelection_StoresQualifiedSparkPlanAndDynamicPort()
    {
        var probe = new FakeHardwareProbe(CreateSparkHardware());
        var step = new PreflightLocalAiHardwareStep(probe, new FakePortSelector(49_151));
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true, Port = 0 });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(49_151, context.LocalAiPort);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, context.LocalAiEligibility?.Status);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, context.LocalAiEligibility?.Plan?.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, context.LocalAiEligibility?.Plan?.ModelSelectionOrigin);
        Assert.Equal("GPU-SPARK", context.LocalAiEligibility?.SelectedGpu?.StableId);
    }

    [Fact]
    public async Task Preflight_ExplicitAlternative_PreservesUserChoiceAndPort()
    {
        var ports = new FakePortSelector(42_000);
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(CreateSparkHardware()), ports);
        SetupContext context = CreateContext(new LocalAiConfig
        {
            Enabled = true,
            Port = 42_000,
            SelectedModelId = LocalModelCatalog.Qwen9BModelId,
        });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(42_000, ports.RequestedPort);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, context.LocalAiEligibility?.Plan?.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Explicit, context.LocalAiEligibility?.Plan?.ModelSelectionOrigin);
    }

    [Fact]
    public async Task Preflight_UnsupportedSku_FailsBeforePortSelection()
    {
        HostHardwareInfo hardware = CreateSparkHardware() with
        {
            Gpus =
            [
                CreateSparkHardware().Gpus[0] with { Name = "NVIDIA Future GPU" },
            ],
        };
        var ports = new FakePortSelector(49_151);
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(hardware), ports);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains(nameof(LocalInferenceSelectionFailureCode.UnsupportedGpu), result.Message);
        Assert.Null(context.LocalAiPort);
        Assert.Equal(0, ports.CallCount);
    }

    [Fact]
    public async Task Preflight_BusyGpu_RequiresRetryWithoutChangingModel()
    {
        HostHardwareInfo hardware = CreateSparkHardware() with
        {
            Gpus =
            [
                CreateSparkHardware().Gpus[0] with { FreeGpuVisibleMemoryBytes = 1_000_000_000 },
            ],
        };
        var ports = new FakePortSelector(49_151);
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(hardware), ports);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("currently busy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, context.LocalAiEligibility?.Plan?.Model.Id);
        Assert.Null(context.LocalAiPort);
        Assert.Equal(0, ports.CallCount);
    }

    [Fact]
    public async Task Preflight_UnavailableRequestedPort_FailsWithoutSubstitution()
    {
        var ports = new FakePortSelector(0, succeeds: false);
        var step = new PreflightLocalAiHardwareStep(new FakeHardwareProbe(CreateSparkHardware()), ports);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true, Port = 40_000 });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(40_000, ports.RequestedPort);
        Assert.Null(context.LocalAiPort);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WslNetworking_AlreadyMirrored_SkipsWithoutConsentOrShutdown()
    {
        var manager = new FakeWslGlobalConfigManager(new WslGlobalConfigStatus(true, true));
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true, WslMirroredNetworkingConsent = false },
            commands);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Skipped, result.Outcome);
        Assert.Equal(0, manager.ApplyCount);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WslNetworking_ConsentDeclined_FailsBeforeWriteOrShutdown()
    {
        var manager = new FakeWslGlobalConfigManager(new WslGlobalConfigStatus(false, false));
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true, WslMirroredNetworkingConsent = false },
            commands);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("Consent is required", result.Message);
        Assert.Equal(0, manager.ApplyCount);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WslNetworking_ConsentAccepted_AppliesThenShutsDownExactlyOnce()
    {
        var manager = new FakeWslGlobalConfigManager(new WslGlobalConfigStatus(false, false));
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true, WslMirroredNetworkingConsent = true },
            commands);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(1, manager.ApplyCount);
        Assert.Collection(
            commands.Calls,
            call =>
            {
                Assert.Equal(WslConstants.WslExePath, call.Executable);
                Assert.Equal(["--shutdown"], call.Arguments);
            });
    }

    [Fact]
    public async Task WslNetworking_ShutdownFailure_RestoresImmediately()
    {
        var manager = new FakeWslGlobalConfigManager(
            new WslGlobalConfigStatus(false, false),
            restoreResult: WslGlobalConfigRestoreResult.Restored);
        var commands = new RecordingCommandRunner(exitCode: 1);
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true, WslMirroredNetworkingConsent = true },
            commands);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Equal(1, manager.RestoreCount);
    }

    [Fact]
    public async Task WslNetworking_Rollback_RestoresAndAppliesWithOneShutdown()
    {
        var manager = new FakeWslGlobalConfigManager(
            new WslGlobalConfigStatus(false, false),
            restoreResult: WslGlobalConfigRestoreResult.Restored);
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true }, commands);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, manager.RestoreCount);
        Assert.Single(commands.Calls);
        Assert.Equal(["--shutdown"], commands.Calls[0].Arguments);
    }

    [Fact]
    public async Task WslNetworking_Rollback_PreservesNewerUserEditWithoutShutdown()
    {
        var manager = new FakeWslGlobalConfigManager(
            new WslGlobalConfigStatus(false, false),
            restoreResult: WslGlobalConfigRestoreResult.UserModified);
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true }, commands);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, manager.RestoreCount);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task RuntimeAcquisition_UsesSelectedRuntimeAndStoresOwnedInstall()
    {
        var acquirer = new FakeRuntimeAcquirer();
        var step = new AcquireLocalAiRuntimeStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(LlamaRuntimeCatalog.Arm64RuntimeId, acquirer.Runtime?.Id);
        Assert.NotNull(context.LocalAiRuntimeInstall);
    }

    [Fact]
    public async Task RuntimeAcquisition_RequiresQualifiedPlanBeforeNetwork()
    {
        var acquirer = new FakeRuntimeAcquirer();
        var step = new AcquireLocalAiRuntimeStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(0, acquirer.InstallCount);
    }

    [Fact]
    public async Task RuntimeAcquisition_RollbackRemovesOnlyInstallCreatedThisRun()
    {
        var acquirer = new FakeRuntimeAcquirer();
        var step = new AcquireLocalAiRuntimeStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        await step.ExecuteAsync(context, CancellationToken.None);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, acquirer.RemoveCount);
        Assert.Null(context.LocalAiRuntimeInstall);
    }

    [Fact]
    public async Task ModelAcquisition_UsesExplicitSelectedModelAndStoresResult()
    {
        var acquirer = new FakeModelAcquirer();
        var step = new AcquireLocalAiModelStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig
        {
            Enabled = true,
            SelectedModelId = LocalModelCatalog.Qwen9BModelId,
        });
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(
            CreateSparkHardware(),
            LocalModelCatalog.Qwen9BModelId);
        context.LocalAiRuntimeInstall = RuntimeInstall(context.LocalDataDir);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, acquirer.Model?.Id);
        Assert.NotNull(context.LocalAiModelInstall);
    }

    [Fact]
    public async Task ModelAcquisition_RequiresRuntimeBeforeNetwork()
    {
        var acquirer = new FakeModelAcquirer();
        var step = new AcquireLocalAiModelStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal(0, acquirer.InstallCount);
    }

    [Fact]
    public async Task ModelAcquisition_RollbackRemovesOnlyNewlyDownloadedFile()
    {
        var acquirer = new FakeModelAcquirer();
        var step = new AcquireLocalAiModelStep(acquirer);
        SetupContext context = CreateContext(new LocalAiConfig { Enabled = true });
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiRuntimeInstall = RuntimeInstall(context.LocalDataDir);
        await step.ExecuteAsync(context, CancellationToken.None);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, acquirer.RemoveCount);
        Assert.Null(context.LocalAiModelInstall);
    }

    [Fact]
    public async Task Manifest_PersistsExactQualifiedPlanAndOwnedPaths()
    {
        using var temp = new TempDirectory("local-ai-setup-");
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true },
            localDataDirectory: temp.Path);
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiPort = 49_151;
        context.LocalAiRuntimeInstall = RuntimeInstall(temp.Path);
        context.LocalAiModelInstall = ModelInstall(temp.Path, LocalModelCatalog.Default);
        var step = new PersistLocalAiManifestStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        LocalAiResolvedInstall? loaded = await new LocalAiManifestStore(new LocalAiPaths(temp.Path)).LoadAsync();

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.NotNull(loaded);
        Assert.Equal(LlamaRuntimeCatalog.Arm64RuntimeId, loaded.Manifest.RuntimeId);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, loaded.Manifest.ModelCatalogId);
        Assert.Equal("GPU-SPARK", loaded.Manifest.SelectedGpuId);
        Assert.Equal(49_151, loaded.Endpoint.Port);
        Assert.Equal(2, loaded.Manifest.RuntimeAssets.Length);
        Assert.Equal(LocalModelCatalog.NativeContextTokens, loaded.Manifest.ContextLength);
        Assert.True(context.LocalAiManifestCreatedThisRun);
    }

    [Fact]
    public async Task Manifest_RollbackDeletesOnlyReceiptCreatedThisRun()
    {
        using var temp = new TempDirectory("local-ai-setup-");
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true },
            localDataDirectory: temp.Path);
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiPort = 49_151;
        context.LocalAiRuntimeInstall = RuntimeInstall(temp.Path);
        context.LocalAiModelInstall = ModelInstall(temp.Path, LocalModelCatalog.Default);
        var step = new PersistLocalAiManifestStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.False(File.Exists(new LocalAiPaths(temp.Path).ManifestPath));
        Assert.False(context.LocalAiManifestCreatedThisRun);
    }

    [Fact]
    public async Task Manifest_RefusesToOverwriteExistingReceipt()
    {
        using var temp = new TempDirectory("local-ai-setup-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.ManifestPath, "existing receipt");
        SetupContext context = CreateContext(
            new LocalAiConfig { Enabled = true },
            localDataDirectory: temp.Path);
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiPort = 49_151;
        context.LocalAiRuntimeInstall = RuntimeInstall(temp.Path);
        context.LocalAiModelInstall = ModelInstall(temp.Path, LocalModelCatalog.Default);

        StepResult result = await new PersistLocalAiManifestStep().ExecuteAsync(
            context,
            CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Equal("existing receipt", await File.ReadAllTextAsync(paths.ManifestPath));
        Assert.False(context.LocalAiManifestCreatedThisRun);
    }

    private static SetupContext CreateContext(
        LocalAiConfig localAi,
        ICommandRunner? commands = null,
        string? localDataDirectory = null)
    {
        var config = new SetupConfig { LocalAi = localAi };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            commands ?? new CommandRunner(logger),
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private static HostHardwareInfo CreateSparkHardware() =>
        new(
            Architecture.Arm64,
            128L * 1024 * 1024 * 1024,
            100L * 1024 * 1024 * 1024,
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                    GpuVisibleMemoryBytes: 25_702_694_912,
                    FreeGpuVisibleMemoryBytes: 25_000_000_000,
                    DriverVersion: "616.00",
                    CudaMajorVersion: 13,
                    StableId: "GPU-SPARK"),
            ],
            VulkanAvailable: false);

    private static LlamaRuntimeInstallResult RuntimeInstall(string localDataDirectory)
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(Architecture.Arm64)!;
        return new(
            Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server"),
            Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server", "llama-server.exe"),
            LlamaRuntimeInstallDisposition.Installed,
            CreatedThisRun: true,
            VerifiedArchives: runtime.Artifacts.Select(artifact => new LocalAiVerifiedArchive(
                Path.GetFileName(artifact.RelativePath),
                artifact.SizeBytes,
                artifact.Sha256.Value)).ToArray(),
            Rollback: new LocalAiArtifactRollbackMetadata(
                Path.Combine(localDataDirectory, "LocalAI", "engines", "llama-server")));
    }

    private static HuggingFaceModelInstallResult ModelInstall(
        string localDataDirectory,
        LocalModelInfo model) =>
        new(
            Path.Combine(localDataDirectory, "LocalAI", "models", model.Weights.RelativePath),
            HuggingFaceModelInstallDisposition.Downloaded,
            CreatedThisRun: true);

    private sealed class FakeHardwareProbe(HostHardwareInfo hardware, bool throwOnProbe = false) : IHostHardwareProbe
    {
        public int CallCount { get; private set; }

        public HostHardwareInfo Probe()
        {
            CallCount++;
            if (throwOnProbe)
                throw new InvalidOperationException("probe must not run");
            return hardware;
        }
    }

    private sealed class FakePortSelector(int selectedPort, bool succeeds = true) : ILocalAiPortSelector
    {
        public int CallCount { get; private set; }
        public int? RequestedPort { get; private set; }

        public bool TrySelect(int requestedPort, out int port, out string? error)
        {
            CallCount++;
            RequestedPort = requestedPort;
            port = selectedPort;
            error = succeeds ? null : "Requested port is unavailable.";
            return succeeds;
        }
    }

    private sealed class FakeWslGlobalConfigManager(
        WslGlobalConfigStatus status,
        WslGlobalConfigRestoreResult restoreResult = WslGlobalConfigRestoreResult.NoBackup) : IWslGlobalConfigManager
    {
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }

        public WslGlobalConfigStatus Inspect() => status;

        public WslGlobalConfigApplyResult ApplyMirroredNetworking()
        {
            ApplyCount++;
            return new WslGlobalConfigApplyResult(true, null);
        }

        public WslGlobalConfigRestoreResult RestoreIfUnchanged()
        {
            RestoreCount++;
            return restoreResult;
        }
    }

    private sealed class RecordingCommandRunner(int exitCode = 0) : ICommandRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(new CommandResult(exitCode, string.Empty, string.Empty, TimeSpan.Zero, false));
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRuntimeAcquirer : ILlamaRuntimeAcquirer
    {
        public int InstallCount { get; private set; }
        public int RemoveCount { get; private set; }
        public LlamaRuntimeVariant? Runtime { get; private set; }

        public Task<LlamaRuntimeInstallResult> InstallAsync(
            string localDataDirectory,
            LlamaRuntimeVariant runtime,
            IProgress<LocalAiArtifactInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            Runtime = runtime;
            return Task.FromResult(RuntimeInstall(localDataDirectory));
        }

        public void RemoveInstalledRuntime(string localDataDirectory, LlamaRuntimeInstallResult install) =>
            RemoveCount++;
    }

    private sealed class FakeModelAcquirer : IHuggingFaceModelAcquirer
    {
        public int InstallCount { get; private set; }
        public int RemoveCount { get; private set; }
        public LocalModelInfo? Model { get; private set; }

        public Task<HuggingFaceModelInstallResult> InstallAsync(
            string localDataDirectory,
            LocalAiComponentIdentity component,
            LocalModelInfo model,
            IProgress<HuggingFaceModelInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            Model = model;
            return Task.FromResult(new HuggingFaceModelInstallResult(
                Path.Combine(localDataDirectory, "LocalAI", "models", model.Weights.RelativePath),
                HuggingFaceModelInstallDisposition.Downloaded,
                CreatedThisRun: true));
        }

        public void RemoveInstalledModel(string localDataDirectory, HuggingFaceModelInstallResult install) =>
            RemoveCount++;
    }
}
