using System.Runtime.InteropServices;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

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

    private static SetupContext CreateContext(LocalAiConfig localAi)
    {
        var config = new SetupConfig { LocalAi = localAi };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None);
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
}
