using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class LocalInferenceEligibilityTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void SparkDefaultIsEligibleAtObservedConfiguredAllocation()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(
                RuntimeArchitecture.Arm64,
                "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                24_512 * MiB,
                23_336 * MiB));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, result.Plan?.ModelSelectionOrigin);
        Assert.Equal("GPU-test", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void LowFreeMemoryKeepsSameDefaultAndReportsBusy()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(
                RuntimeArchitecture.X64,
                "NVIDIA GeForce RTX 5090",
                32_768 * MiB,
                8_000 * MiB));

        Assert.Equal(LocalInferenceEligibilityStatus.EligibleButBusy, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.Default.Weights.SizeBytes, result.RequiredFreeMemoryBytes);
        Assert.Equal(8_000 * MiB, result.AvailableFreeMemoryBytes);
    }

    [Fact]
    public void ExplicitAlternativeIsNeverSelectedImplicitly()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            "NVIDIA GeForce RTX 5090",
            32_768 * MiB,
            7_000 * MiB);

        LocalInferenceEligibilityResult defaultResult = LocalInferenceEligibility.Evaluate(hardware);
        LocalInferenceEligibilityResult explicitResult = LocalInferenceEligibility.Evaluate(
            hardware,
            LocalModelCatalog.Qwen9BModelId);

        Assert.Equal(LocalModelCatalog.Qwen35BModelId, defaultResult.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.Qwen9BModelId, explicitResult.Plan?.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Explicit, explicitResult.Plan?.ModelSelectionOrigin);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, explicitResult.Status);
    }

    [Theory]
    [InlineData(23_999, "616.00", 13, LocalInferenceEligibilityFailureCode.InsufficientGpuMemory)]
    [InlineData(32_768, "614.99", 13, LocalInferenceEligibilityFailureCode.DriverTooOld)]
    [InlineData(32_768, "616.00", 12, LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow)]
    public void QualifiedSkuStillRequiresCapacityDriverAndCuda(
        long totalMiB,
        string driver,
        int cudaMajor,
        LocalInferenceEligibilityFailureCode expectedFailure)
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            "NVIDIA GeForce RTX 5090",
            totalMiB * MiB,
            totalMiB * MiB,
            driver,
            cudaMajor);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void MissingStableIdentityFailsClosed()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            "NVIDIA GeForce RTX 5090",
            32_768 * MiB,
            30_000 * MiB,
            stableId: null);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void UnknownModelPreservesCatalogFailureWithoutFallback()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(
                RuntimeArchitecture.Arm64,
                "NVIDIA RTX Spark N1X",
                24_512 * MiB,
                23_000 * MiB),
            "unknown-model");

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnknownModel, result.SelectionFailureCode);
        Assert.Null(result.Plan);
    }

    private static HostHardwareInfo Hardware(
        RuntimeArchitecture architecture,
        string name,
        long totalBytes,
        long freeBytes,
        string driver = "616.00",
        int cudaMajor = 13,
        string? stableId = "GPU-test") =>
        new(
            architecture,
            128L * 1024 * 1024 * 1024,
            96L * 1024 * 1024 * 1024,
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    name,
                    totalBytes,
                    freeBytes,
                    driver,
                    cudaMajor,
                    stableId),
            ],
            VulkanAvailable: false);
}
