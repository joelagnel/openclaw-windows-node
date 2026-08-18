namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiHardwareEligibilityTests
{
    private static readonly ulong Minimum = LocalAiHardwareEligibilityPolicy.MinimumGpuMemoryBytes;

    [Fact]
    public void Evaluate_QualifiesLargestNvidiaGpuAtThreshold()
    {
        var result = LocalAiHardwareEligibilityPolicy.Evaluate(new LocalAiHardwareSnapshot(
        [
            new("Small GPU", Minimum - 1, Minimum - 1),
            new("Qualified GPU", Minimum, Minimum / 2),
            new("Largest GPU", Minimum + 1024, Minimum / 2),
        ]));

        Assert.True(result.IsEligible);
        Assert.Equal("Largest GPU", result.SelectedGpu?.Name);
        Assert.Contains("24,000 MiB", result.Message);
    }

    [Fact]
    public void Evaluate_RejectsGpuBelowQualifiedThreshold()
    {
        var result = LocalAiHardwareEligibilityPolicy.Evaluate(new LocalAiHardwareSnapshot(
            [new("Almost large enough", Minimum - 1, Minimum - 1)]));

        Assert.False(result.IsEligible);
        Assert.Equal("Almost large enough", result.SelectedGpu?.Name);
        Assert.Contains("requires at least 24,000 MiB", result.Message);
    }

    [Fact]
    public void Evaluate_RejectsMissingGpuAndProbeFailure()
    {
        var missing = LocalAiHardwareEligibilityPolicy.Evaluate(new LocalAiHardwareSnapshot([]));
        var failed = LocalAiHardwareEligibilityPolicy.Evaluate(
            new LocalAiHardwareSnapshot([], "NVML unavailable"));

        Assert.False(missing.IsEligible);
        Assert.Contains("No compatible NVIDIA GPU", missing.Message);
        Assert.False(failed.IsEligible);
        Assert.Contains("could not be verified", failed.Message);
        Assert.DoesNotContain("NVML unavailable", failed.Message);
    }

    [Fact]
    public void NvmlProbe_LoadsOnlyFromExplicitDriverLocations()
    {
        var candidates = NvmlLocalAiHardwareProbe.GetNvmlLibraryCandidates();

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, path => Assert.True(Path.IsPathFullyQualified(path), path));
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            candidates[0],
            ignoreCase: true);
        Assert.EndsWith(
            Path.Combine("NVIDIA Corporation", "NVSMI", "nvml.dll"),
            candidates[1],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preflight_SkipsWhenLocalAiIsDisabled()
    {
        var probe = new FakeProbe(new LocalAiHardwareSnapshot([], "must not be called"));
        var step = new PreflightLocalAiHardwareStep(probe);
        var context = CreateContext(localAiEnabled: false);

        Assert.True(step.CanSkip(context));
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Preflight_RejectsUnqualifiedHardware()
    {
        var step = new PreflightLocalAiHardwareStep(new FakeProbe(
            new LocalAiHardwareSnapshot([new("Small GPU", Minimum - 1, 0)])));

        var result = await step.ExecuteAsync(CreateContext(localAiEnabled: true), CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("requires at least 24,000 MiB", result.Message);
    }

    [Fact]
    public async Task Preflight_AcceptsQualifiedHardware()
    {
        var step = new PreflightLocalAiHardwareStep(new FakeProbe(
            new LocalAiHardwareSnapshot([new("Qualified GPU", Minimum, Minimum)])));

        var result = await step.ExecuteAsync(CreateContext(localAiEnabled: true), CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Contains("Qualified GPU", result.Message);
    }

    private static SetupContext CreateContext(bool localAiEnabled)
    {
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            new SetupConfig { LocalAi = new LocalAiConfig { Enabled = localAiEnabled } },
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None);
    }

    private sealed class FakeProbe(LocalAiHardwareSnapshot snapshot) : ILocalAiHardwareProbe
    {
        public int CallCount { get; private set; }

        public LocalAiHardwareSnapshot Probe()
        {
            CallCount++;
            return snapshot;
        }
    }
}
