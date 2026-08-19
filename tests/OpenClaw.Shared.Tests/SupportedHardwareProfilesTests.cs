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
