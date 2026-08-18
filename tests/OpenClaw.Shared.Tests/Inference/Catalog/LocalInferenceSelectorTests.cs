using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class LocalInferenceSelectorTests
{
    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA GeForce RTX 5090", "geforce-rtx-5090", LlamaRuntimeCatalog.X64RuntimeId)]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "rtx-pro-6000-blackwell-workstation", LlamaRuntimeCatalog.X64RuntimeId)]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA RTX Spark N1X", "rtx-spark-n1x", LlamaRuntimeCatalog.Arm64RuntimeId)]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)", "rtx-spark-n1x", LlamaRuntimeCatalog.Arm64RuntimeId)]
    public void SupportedHardwareSelectsPinnedRuntimeAndDefaultModel(
        RuntimeArchitecture architecture,
        string gpuName,
        string expectedProfileId,
        string expectedRuntimeId)
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(Hardware(architecture, Nvidia(gpuName)));

        Assert.True(result.IsSelected);
        Assert.Equal(LocalInferenceSelectionStatus.Selected, result.Status);
        Assert.Equal(LocalInferenceSelectionFailureCode.None, result.FailureCode);
        LocalInferencePlan plan = Assert.IsType<LocalInferencePlan>(result.Plan);
        Assert.Equal(expectedProfileId, plan.HardwareProfile.Id);
        Assert.Equal(expectedRuntimeId, plan.Runtime.Id);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, plan.Model.Id);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Default, plan.ModelSelectionOrigin);
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen27BModelId)]
    [InlineData(LocalModelCatalog.Qwen9BModelId)]
    [InlineData("QWEN3.6-35B-A3B-MTP-Q4-K-M")]
    public void ExplicitModelSelectionNeverSilentlyChangesTheChoice(string requestedModelId)
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, Nvidia("NVIDIA GeForce RTX 5090")),
            requestedModelId);

        LocalInferencePlan plan = Assert.IsType<LocalInferencePlan>(result.Plan);
        Assert.Equal(requestedModelId, plan.Model.Id, ignoreCase: true);
        Assert.Equal(LocalInferenceModelSelectionOrigin.Explicit, plan.ModelSelectionOrigin);
    }

    [Fact]
    public void UnknownExplicitModelFailsWithoutFallback()
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, Nvidia("NVIDIA GeForce RTX 5090")),
            "qwen3.8-placeholder");

        AssertUnsupported(result, LocalInferenceSelectionFailureCode.UnknownModel);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X86)]
    [InlineData(RuntimeArchitecture.Arm)]
    [InlineData(RuntimeArchitecture.Wasm)]
    public void UnsupportedOperatingSystemArchitectureFailsDeterministically(RuntimeArchitecture architecture)
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(
            Hardware(architecture, Nvidia("NVIDIA GeForce RTX 5090")));

        AssertUnsupported(result, LocalInferenceSelectionFailureCode.UnsupportedArchitecture);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA GeForce RTX 5090")]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX Spark N1X")]
    public void QualifiedSkuOnWrongNativeArchitectureIsRejected(
        RuntimeArchitecture architecture,
        string gpuName)
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(Hardware(architecture, Nvidia(gpuName)));

        AssertUnsupported(result, LocalInferenceSelectionFailureCode.UnsupportedArchitecture);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090")]
    [InlineData("NVIDIA GeForce RTX 5090 Laptop GPU")]
    [InlineData("GeForce RTX 5090")]
    public void UnqualifiedNvidiaNameIsRejectedWithoutFamilyMatching(string gpuName)
    {
        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, Nvidia(gpuName)));

        AssertUnsupported(result, LocalInferenceSelectionFailureCode.UnsupportedGpu);
    }

    [Fact]
    public void VendorClassificationMustAlsoBeNvidia()
    {
        var spoofedName = new GpuInfo(GpuVendor.Unknown, "NVIDIA GeForce RTX 5090");

        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, spoofedName));

        AssertUnsupported(result, LocalInferenceSelectionFailureCode.UnsupportedGpu);
    }

    [Fact]
    public void CatalogPreferenceMakesMultiGpuSelectionIndependentOfProbeOrder()
    {
        GpuInfo rtx5090 = Nvidia("NVIDIA GeForce RTX 5090");
        GpuInfo rtxPro6000 = Nvidia("NVIDIA RTX PRO 6000 Blackwell Workstation Edition");

        LocalInferenceSelectionResult forward = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, rtx5090, rtxPro6000));
        LocalInferenceSelectionResult reverse = LocalInferenceSelector.Select(
            Hardware(RuntimeArchitecture.X64, rtxPro6000, rtx5090));

        Assert.Equal("rtx-pro-6000-blackwell-workstation", forward.Plan?.HardwareProfile.Id);
        Assert.Equal(forward.Plan, reverse.Plan);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1L, 1L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void CatalogSelectionDoesNotInventAMemoryEligibilityThreshold(
        long? totalPhysicalMemoryBytes,
        long? dedicatedMemoryBytes)
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "NVIDIA GeForce RTX 5090",
            GpuVisibleMemoryBytes: dedicatedMemoryBytes);
        var hardware = new HostHardwareInfo(
            RuntimeArchitecture.X64,
            totalPhysicalMemoryBytes,
            null,
            new[] { gpu },
            VulkanAvailable: false);

        LocalInferenceSelectionResult result = LocalInferenceSelector.Select(hardware);

        Assert.True(result.IsSelected);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, result.Plan?.Model.Id);
    }

    private static GpuInfo Nvidia(string name) => new(GpuVendor.Nvidia, name);

    private static HostHardwareInfo Hardware(RuntimeArchitecture architecture, params GpuInfo[] gpus) =>
        new(architecture, null, null, gpus, VulkanAvailable: false);

    private static void AssertUnsupported(
        LocalInferenceSelectionResult result,
        LocalInferenceSelectionFailureCode expectedFailureCode)
    {
        Assert.False(result.IsSelected);
        Assert.Equal(LocalInferenceSelectionStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailureCode, result.FailureCode);
        Assert.Null(result.Plan);
    }
}
