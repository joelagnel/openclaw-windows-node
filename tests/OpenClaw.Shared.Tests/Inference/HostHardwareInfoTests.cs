using OpenClaw.Shared.Inference;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Tests.Inference;

public sealed class HostHardwareInfoTests
{
    [Fact]
    public void GpuFactsKeepCapacityReadinessAndIdentitySeparate()
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "NVIDIA RTX Spark N1X",
            GpuVisibleMemoryBytes: 25_702_694_912,
            FreeGpuVisibleMemoryBytes: 24_469_569_536,
            DriverVersion: "616.00",
            CudaMajorVersion: 13,
            StableId: "GPU-4bd513e9");

        Assert.Equal(25_702_694_912, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(24_469_569_536, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Equal("GPU-4bd513e9", gpu.StableId);
    }

    [Fact]
    public void UnknownSnapshotDoesNotInventFallbackHardware()
    {
        HostHardwareInfo snapshot = HostHardwareInfo.Unknown;

        Assert.Equal(RuntimeInformation.OSArchitecture, snapshot.CpuArchitecture);
        Assert.False(snapshot.HasNvidiaGpu);
        Assert.Empty(snapshot.Gpus);
        Assert.Null(snapshot.TotalPhysicalMemoryBytes);
        Assert.Null(snapshot.AvailablePhysicalMemoryBytes);
    }
}
