using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference;

public interface IHostHardwareProbe
{
    HostHardwareInfo Probe();
}

/// <summary>
/// Reads the CUDA driver's own device and allocatable-memory view. This is the
/// sole GPU-memory source for Local AI qualification, including UMA devices.
/// </summary>
public sealed class CudaHostHardwareProbe : IHostHardwareProbe
{
    public HostHardwareInfo Probe()
    {
        PhysicalMemorySnapshot? physicalMemory = null;
        try { physicalMemory = PhysicalMemoryProbe.TryRead(); } catch { }

        IReadOnlyList<GpuInfo> gpus;
        try { gpus = CaptureCudaGpus(); } catch { gpus = []; }

        return new HostHardwareInfo(
            RuntimeInformation.OSArchitecture,
            physicalMemory?.TotalBytes,
            physicalMemory?.AvailableBytes,
            gpus,
            VulkanAvailable: false);
    }

    private static IReadOnlyList<GpuInfo> CaptureCudaGpus()
    {
        if (!OperatingSystem.IsWindows() || CuInit(0) != CudaSuccess ||
            CuDeviceGetCount(out int count) != CudaSuccess)
        {
            return [];
        }

        int? cudaMajorVersion = CuDriverGetVersion(out int driverVersion) == CudaSuccess && driverVersion > 0
            ? driverVersion / 1000
            : null;
        var gpus = new List<GpuInfo>();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            if (CuDeviceGet(out int device, ordinal) != CudaSuccess)
                continue;

            string? name = ReadDeviceName(device);
            string? gpuUuid = ReadDeviceUuid(device);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gpuUuid))
                continue;

            var identifiedGpu = new GpuInfo(
                GpuVendor.Nvidia,
                name,
                CudaMajorVersion: cudaMajorVersion,
                StableId: gpuUuid);
            IntPtr context = IntPtr.Zero;
            try
            {
                if (CuCtxCreate(out context, 0, device) != CudaSuccess ||
                    CuMemGetInfo(out nuint freeBytes, out nuint totalBytes) != CudaSuccess ||
                    totalBytes == 0 || totalBytes > long.MaxValue || freeBytes > totalBytes)
                {
                    gpus.Add(identifiedGpu);
                    continue;
                }

                gpus.Add(identifiedGpu with
                {
                    GpuVisibleMemoryBytes = (long)totalBytes,
                    FreeGpuVisibleMemoryBytes = (long)freeBytes,
                });
            }
            finally
            {
                if (context != IntPtr.Zero)
                    _ = CuCtxDestroy(context);
            }
        }

        return gpus;
    }

    internal static string ToCudaVisibleDevicesSelector(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != CudaUuidSize)
            throw new ArgumentException($"A CUDA GPU UUID must contain {CudaUuidSize} bytes.", nameof(uuid));

        string hex = Convert.ToHexString(uuid).ToLowerInvariant();
        return $"GPU-{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string? ReadDeviceName(int device)
    {
        var buffer = new byte[DeviceNameCapacity];
        return CuDeviceGetName(buffer, buffer.Length, device) == CudaSuccess ? DecodeUtf8(buffer) : null;
    }

    private static string? ReadDeviceUuid(int device)
    {
        if (CuDeviceGetUuid(out CudaUuid uuid, device) != CudaSuccess)
            return null;

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref uuid, 1));
        return ToCudaVisibleDevicesSelector(bytes);
    }

    private static string? DecodeUtf8(byte[] buffer)
    {
        int terminator = Array.IndexOf(buffer, (byte)0);
        string value = Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private const int CudaSuccess = 0;
    private const int DeviceNameCapacity = 256;
    private const int CudaUuidSize = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct CudaUuid
    {
        public ulong FirstBytes;
        public ulong LastBytes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuInit", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuInit(uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDriverGetVersion(out int driverVersion);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetCount(out int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGet", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGet(out int device, int ordinal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetName", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetName([Out] byte[] name, int length, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetUuid_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetUuid(out CudaUuid uuid, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxCreate_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxCreate(out IntPtr context, uint flags, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxDestroy_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxDestroy(IntPtr context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuMemGetInfo_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuMemGetInfo(out nuint freeBytes, out nuint totalBytes);
}
