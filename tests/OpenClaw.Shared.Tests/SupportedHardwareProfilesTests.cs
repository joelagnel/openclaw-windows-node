using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests;

public class SupportedHardwareProfilesTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("NVIDIA RTX Spark N1X (5120-core Blackwell RTX GPU)")]
    [InlineData("NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)")]
    public void Find_MatchesSparkN1XCoreCountVariants(string reportedGpuName)
    {
        SupportedHardwareProfile? profile = SupportedHardwareProfiles.Find(
            System.Runtime.InteropServices.Architecture.Arm64,
            reportedGpuName);

        Assert.NotNull(profile);
        Assert.Equal(SupportedHardwareProfiles.RtxSparkN1XProfileId, profile.Id);
    }

    [Fact]
    public void Find_DoesNotMatchOtherSparkModels()
    {
        SupportedHardwareProfile? profile = SupportedHardwareProfiles.Find(
            System.Runtime.InteropServices.Architecture.Arm64,
            "NVIDIA RTX Spark N2X (5120-core Blackwell RTX GPU)");

        Assert.Null(profile);
    }

    [Fact]
    public void Evaluate_UsesSharedGpuMemoryForSparkN1X()
    {
        var hardware = new HostHardwareInfo(
            System.Runtime.InteropServices.Architecture.Arm64,
            TotalPhysicalMemoryBytes: 64 * Gibibyte,
            AvailablePhysicalMemoryBytes: 48 * Gibibyte,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA RTX Spark N1X (5120-core Blackwell RTX GPU)",
                    GpuVisibleMemoryBytes: 8 * Gibibyte,
                    FreeGpuVisibleMemoryBytes: 8 * Gibibyte,
                    SharedGpuMemoryBytes: 38 * Gibibyte,
                    FreeSharedGpuMemoryBytes: null,
                    DriverVersion: "616.30",
                    CudaMajorVersion: 13,
                    StableId: "GPU-123"),
            ],
            VulkanAvailable: false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void Evaluate_RequiresEnoughGpuMemoryWhenSharedGpuMemoryIsUnavailable()
    {
        var hardware = new HostHardwareInfo(
            System.Runtime.InteropServices.Architecture.Arm64,
            TotalPhysicalMemoryBytes: 64 * Gibibyte,
            AvailablePhysicalMemoryBytes: null,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA RTX Spark N1X (5120-core Blackwell RTX GPU)",
                    GpuVisibleMemoryBytes: 8 * Gibibyte,
                    FreeGpuVisibleMemoryBytes: 8 * Gibibyte,
                    DriverVersion: "616.30",
                    CudaMajorVersion: 13,
                    StableId: "GPU-123"),
            ],
            VulkanAvailable: false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
    }

    [Theory]
    [InlineData(
        System.Runtime.InteropServices.Architecture.X64,
        SupportedHardwareProfiles.MemoryQualifiedX64ProfileId)]
    [InlineData(
        System.Runtime.InteropServices.Architecture.Arm64,
        SupportedHardwareProfiles.MemoryQualifiedArm64ProfileId)]
    public void Evaluate_UsesMemoryQualifiedFallbackForUnknownNvidiaGpu(
        System.Runtime.InteropServices.Architecture architecture,
        string expectedProfileId)
    {
        var hardware = new HostHardwareInfo(
            architecture,
            TotalPhysicalMemoryBytes: 64 * Gibibyte,
            AvailablePhysicalMemoryBytes: 48 * Gibibyte,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "JMJWOA-Generic-GPU",
                    GpuVisibleMemoryBytes: LocalInferenceEligibility.MinimumQualifiedGpuMemoryBytes,
                    FreeGpuVisibleMemoryBytes: LocalInferenceEligibility.MinimumQualifiedGpuMemoryBytes,
                    DriverVersion: "616.30",
                    CudaMajorVersion: 13,
                    StableId: "GPU-123"),
            ],
            VulkanAvailable: false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(expectedProfileId, result.Plan?.HardwareProfile.Id);
        Assert.Equal("GPU-123", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_RejectsUnknownNvidiaGpuBelowMemoryFallbackThreshold()
    {
        var hardware = new HostHardwareInfo(
            System.Runtime.InteropServices.Architecture.Arm64,
            TotalPhysicalMemoryBytes: 64 * Gibibyte,
            AvailablePhysicalMemoryBytes: 48 * Gibibyte,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "JMJWOA-Generic-GPU",
                    GpuVisibleMemoryBytes: LocalInferenceEligibility.MinimumQualifiedGpuMemoryBytes - 1,
                    FreeGpuVisibleMemoryBytes: LocalInferenceEligibility.MinimumQualifiedGpuMemoryBytes - 1,
                    DriverVersion: "616.30",
                    CudaMajorVersion: 13,
                    StableId: "GPU-123"),
            ],
            VulkanAvailable: false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnsupportedGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void Evaluate_AppliesDriverRequirementAfterMemoryQualifiedFallback()
    {
        var hardware = new HostHardwareInfo(
            System.Runtime.InteropServices.Architecture.Arm64,
            TotalPhysicalMemoryBytes: 64 * Gibibyte,
            AvailablePhysicalMemoryBytes: 48 * Gibibyte,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "JMJWOA-Generic-GPU",
                    GpuVisibleMemoryBytes: 24_512L * 1024 * 1024,
                    FreeGpuVisibleMemoryBytes: 23_279L * 1024 * 1024,
                    DriverVersion: "592.22",
                    CudaMajorVersion: 13,
                    StableId: "GPU-123"),
            ],
            VulkanAvailable: false);

        LocalInferenceSelectionResult selection = LocalInferenceSelector.Select(hardware);
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityFailureCode.DriverTooOld, result.FailureCode);
        Assert.Equal(
            SupportedHardwareProfiles.MemoryQualifiedArm64ProfileId,
            selection.Plan?.HardwareProfile.Id);
        Assert.Equal("GPU-123", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Probe_JoinsSparkN1XNvmlAndDxgiNamesWithDifferentCoreCountSuffixes()
    {
        const long dedicatedMemoryBytes = 8 * Gibibyte;
        const long sharedMemoryBytes = 38 * Gibibyte;
        var probe = new NvmlHostHardwareProbe(
            () => new NvmlProbeResult(
                [
                    new NvmlGpuSnapshot(
                        "NVIDIA RTX Spark N1X (5120-core Blackwell RTX GPU)",
                        "GPU-123",
                        (ulong)dedicatedMemoryBytes,
                        (ulong)dedicatedMemoryBytes),
                ],
                "616.30",
                13),
            () => null,
            () => new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["NVIDIA RTX Spark N1X"] = new DxgiGpuMemoryInfo(sharedMemoryBytes, null),
            },
            System.Runtime.InteropServices.Architecture.Arm64);

        GpuInfo gpu = Assert.Single(probe.Probe().NvidiaGpus);

        Assert.Equal(sharedMemoryBytes, gpu.SharedGpuMemoryBytes);
    }
}
