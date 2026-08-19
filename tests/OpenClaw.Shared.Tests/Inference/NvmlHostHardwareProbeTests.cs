using OpenClaw.Shared.Inference;
using System.Runtime.InteropServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference;

public sealed class NvmlHostHardwareProbeTests
{
    [Fact]
    public void ProbeComposesDriverGpuAndPhysicalMemoryFacts()
    {
        var probe = new NvmlHostHardwareProbe(
            () => new NvmlProbeResult(
                [
                    new NvmlGpuSnapshot(
                        "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                        "GPU-4bd513e9",
                        25_702_694_912,
                        24_469_569_536),
                    new NvmlGpuSnapshot("NVIDIA NPU", "NPU-test", 0, 0),
                    new NvmlGpuSnapshot("Malformed free memory", "GPU-bad", 1024, 2048),
                    new NvmlGpuSnapshot("Overflow memory", "GPU-overflow", ulong.MaxValue, 0),
                ],
                "616.00",
                13),
            () => new PhysicalMemorySnapshot(137_438_953_472, 99_000_000_000),
            RuntimeArchitecture.Arm64);

        HostHardwareInfo result = probe.Probe();

        GpuInfo gpu = Assert.Single(result.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Equal("NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)", gpu.Name);
        Assert.Equal(25_702_694_912, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(24_469_569_536, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Equal("616.00", gpu.DriverVersion);
        Assert.Equal(13, gpu.CudaMajorVersion);
        Assert.Equal("GPU-4bd513e9", gpu.StableId);
        Assert.Equal(137_438_953_472, result.TotalPhysicalMemoryBytes);
        Assert.Equal(99_000_000_000, result.AvailablePhysicalMemoryBytes);
        Assert.Equal(RuntimeArchitecture.Arm64, result.CpuArchitecture);
        Assert.False(result.VulkanAvailable);
    }

    [Fact]
    public void ProbeFailsClosedWhenSourcesThrow()
    {
        var probe = new NvmlHostHardwareProbe(
            () => throw new InvalidOperationException("driver failure"),
            () => throw new InvalidOperationException("memory failure"),
            RuntimeArchitecture.X64);

        HostHardwareInfo result = probe.Probe();

        Assert.Empty(result.Gpus);
        Assert.Null(result.TotalPhysicalMemoryBytes);
        Assert.Null(result.AvailablePhysicalMemoryBytes);
        Assert.Equal(RuntimeArchitecture.X64, result.CpuArchitecture);
    }

    [Fact]
    public void NvmlLoadsOnlyFromExplicitDriverOwnedPaths()
    {
        IReadOnlyList<string> candidates = NvmlHostHardwareProbe.GetNvmlLibraryCandidates();

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.True(Path.IsPathFullyQualified(candidate), candidate));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "nvml.dll"), candidates[0], ignoreCase: true);
        Assert.EndsWith(
            Path.Combine("NVIDIA Corporation", "NVSMI", "nvml.dll"),
            candidates[1],
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(candidates, candidate => string.Equals(candidate, "nvml.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LiveProbeRecognizesThisSparkWhenNvmlIsAvailable()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("OPENCLAW_RUN_HARDWARE_PROBE_TESTS"),
                "1",
                StringComparison.Ordinal) ||
            !OperatingSystem.IsWindows() ||
            RuntimeInformation.OSArchitecture != RuntimeArchitecture.Arm64)
        {
            return;
        }

        HostHardwareInfo result = new NvmlHostHardwareProbe().Probe();
        GpuInfo? spark = result.Gpus.SingleOrDefault(
            gpu => gpu.Name.Contains("RTX Spark N1X", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(spark);
        Assert.True(spark.GpuVisibleMemoryBytes >= 24_000L * 1024 * 1024);
        Assert.True(spark.FreeGpuVisibleMemoryBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(spark.StableId));
        Assert.False(string.IsNullOrWhiteSpace(spark.DriverVersion));
        Assert.True(spark.CudaMajorVersion >= 13);
    }
}
