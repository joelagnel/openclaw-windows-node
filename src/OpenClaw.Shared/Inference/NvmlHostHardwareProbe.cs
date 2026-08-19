using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference;

public interface IHostHardwareProbe
{
    HostHardwareInfo Probe();
}

/// <summary>
/// Reads NVIDIA GPU identity and CUDA-visible memory through the NVML library
/// installed by the Windows display driver. The probe loads only explicit
/// driver-owned paths and returns unknown facts instead of guessing.
/// </summary>
public sealed class NvmlHostHardwareProbe : IHostHardwareProbe
{
    private readonly Func<NvmlProbeResult> _captureNvml;
    private readonly Func<PhysicalMemorySnapshot?> _readPhysicalMemory;
    private readonly Func<IReadOnlyDictionary<string, DxgiGpuMemoryInfo>> _captureDxgiMemory;
    private readonly Architecture _architecture;

    public NvmlHostHardwareProbe()
        : this(
            CaptureNvml,
            PhysicalMemoryProbe.TryRead,
            DxgiGpuMemoryProbe.CaptureNvidiaMemoryByName,
            RuntimeInformation.OSArchitecture)
    {
    }

    internal NvmlHostHardwareProbe(
        Func<NvmlProbeResult> captureNvml,
        Func<PhysicalMemorySnapshot?> readPhysicalMemory,
        Func<IReadOnlyDictionary<string, DxgiGpuMemoryInfo>> captureDxgiMemory,
        Architecture architecture)
    {
        _captureNvml = captureNvml ?? throw new ArgumentNullException(nameof(captureNvml));
        _readPhysicalMemory = readPhysicalMemory ?? throw new ArgumentNullException(nameof(readPhysicalMemory));
        _captureDxgiMemory = captureDxgiMemory ?? throw new ArgumentNullException(nameof(captureDxgiMemory));
        _architecture = architecture;
    }

    public HostHardwareInfo Probe()
    {
        PhysicalMemorySnapshot? memory = null;
        NvmlProbeResult nvml = NvmlProbeResult.Empty;
        IReadOnlyDictionary<string, DxgiGpuMemoryInfo> dxgiMemoryByName =
            new Dictionary<string, DxgiGpuMemoryInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            memory = _readPhysicalMemory();
        }
        catch
        {
            // Hardware discovery is fail-closed. Unknown RAM remains null.
        }

        try
        {
            nvml = _captureNvml();
        }
        catch
        {
            // Hardware discovery is fail-closed. Unknown GPUs remain absent.
        }

        try
        {
            dxgiMemoryByName = _captureDxgiMemory();
        }
        catch
        {
            // Shared GPU memory is optional. NVML facts remain usable alone.
        }

        var gpus = nvml.Devices
            .Where(device =>
                device.TotalMemoryBytes is > 0 and <= long.MaxValue &&
                device.FreeMemoryBytes <= device.TotalMemoryBytes &&
                !string.IsNullOrWhiteSpace(device.Name))
            .Select(device =>
            {
                string name = device.Name.Trim();
                DxgiGpuMemoryInfo? dxgiMemory = FindDxgiMemoryByName(dxgiMemoryByName, name);
                return new GpuInfo(
                    GpuVendor.Nvidia,
                    name,
                    GpuVisibleMemoryBytes: (long)device.TotalMemoryBytes,
                    FreeGpuVisibleMemoryBytes: (long)device.FreeMemoryBytes,
                    SharedGpuMemoryBytes: dxgiMemory?.SharedMemoryBytes,
                    FreeSharedGpuMemoryBytes: dxgiMemory?.FreeSharedMemoryBytes,
                    DriverVersion: nvml.DriverVersion,
                    CudaMajorVersion: nvml.CudaMajorVersion,
                    StableId: string.IsNullOrWhiteSpace(device.Uuid) ? null : device.Uuid.Trim());
            })
            .ToArray();

        return new HostHardwareInfo(
            _architecture,
            memory?.TotalBytes,
            memory?.AvailableBytes,
            gpus,
            VulkanAvailable: false);
    }

    private static string NormalizeGpuName(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DxgiGpuMemoryInfo? FindDxgiMemoryByName(
        IReadOnlyDictionary<string, DxgiGpuMemoryInfo> dxgiMemoryByName,
        string nvmlName)
    {
        string normalizedNvmlName = NormalizeGpuName(nvmlName);
        if (dxgiMemoryByName.TryGetValue(normalizedNvmlName, out DxgiGpuMemoryInfo? exactMatch))
            return exactMatch;

        const string SparkN1XPrefix = "NVIDIA RTX Spark N1X";
        if (!normalizedNvmlName.Contains(SparkN1XPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        KeyValuePair<string, DxgiGpuMemoryInfo>[] sparkMatches = dxgiMemoryByName
            .Where(entry => entry.Key.Contains(SparkN1XPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return sparkMatches.Length == 1 ? sparkMatches[0].Value : null;
    }

    internal static IReadOnlyList<string> GetNvmlLibraryCandidates()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll"),
        ];

        return candidates
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static NvmlProbeResult CaptureNvml()
    {
        if (!OperatingSystem.IsWindows() || !TryLoadNvml(out IntPtr library))
            return NvmlProbeResult.Empty;

        bool initialized = false;
        NvmlShutdown? shutdown = null;
        try
        {
            var initialize = GetDelegate<NvmlInitialize>(library, "nvmlInit_v2");
            shutdown = GetDelegate<NvmlShutdown>(library, "nvmlShutdown");
            var getCount = GetDelegate<NvmlDeviceGetCount>(library, "nvmlDeviceGetCount_v2");
            var getHandle = GetDelegate<NvmlDeviceGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getName = GetDelegate<NvmlDeviceGetString>(library, "nvmlDeviceGetName");
            var getUuid = GetDelegate<NvmlDeviceGetString>(library, "nvmlDeviceGetUUID");
            var getMemory = GetDelegate<NvmlDeviceGetMemoryInfo>(library, "nvmlDeviceGetMemoryInfo");
            var getDriver = GetDelegate<NvmlSystemGetString>(library, "nvmlSystemGetDriverVersion");
            var getCuda = GetDelegate<NvmlSystemGetCudaDriverVersion>(library, "nvmlSystemGetCudaDriverVersion_v2");

            if (initialize() != NvmlSuccess)
                return NvmlProbeResult.Empty;
            initialized = true;

            string? driverVersion = ReadSystemString(getDriver, DriverVersionCapacity);
            int? cudaMajorVersion = getCuda(out int cudaDriverVersion) == NvmlSuccess && cudaDriverVersion > 0
                ? cudaDriverVersion / 1000
                : null;

            if (getCount(out uint count) != NvmlSuccess)
                return new NvmlProbeResult([], driverVersion, cudaMajorVersion);

            var devices = new List<NvmlGpuSnapshot>();
            for (uint index = 0; index < count; index++)
            {
                if (getHandle(index, out IntPtr device) != NvmlSuccess ||
                    getMemory(device, out NvmlMemory memory) != NvmlSuccess ||
                    memory.Total == 0)
                {
                    continue;
                }

                string? name = ReadDeviceString(device, getName, DeviceNameCapacity);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                string? uuid = ReadDeviceString(device, getUuid, DeviceUuidCapacity);
                devices.Add(new NvmlGpuSnapshot(name, uuid, memory.Total, memory.Free));
            }

            return new NvmlProbeResult(devices, driverVersion, cudaMajorVersion);
        }
        catch (Exception exception) when (exception is
            EntryPointNotFoundException or
            BadImageFormatException or
            MarshalDirectiveException or
            SEHException)
        {
            return NvmlProbeResult.Empty;
        }
        finally
        {
            try
            {
                if (initialized)
                    shutdown?.Invoke();
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
    }

    private static bool TryLoadNvml(out IntPtr library)
    {
        foreach (string candidate in GetNvmlLibraryCandidates())
        {
            try
            {
                if (NativeLibrary.TryLoad(candidate, out library))
                    return true;
            }
            catch (BadImageFormatException)
            {
                // Try the next explicit driver-owned candidate.
            }
        }

        library = IntPtr.Zero;
        return false;
    }

    private static T GetDelegate<T>(IntPtr library, string exportName) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, exportName));

    private static string? ReadSystemString(NvmlSystemGetString getter, uint capacity)
    {
        var buffer = new byte[capacity];
        return getter(buffer, capacity) == NvmlSuccess ? DecodeUtf8(buffer) : null;
    }

    private static string? ReadDeviceString(IntPtr device, NvmlDeviceGetString getter, uint capacity)
    {
        var buffer = new byte[capacity];
        return getter(device, buffer, capacity) == NvmlSuccess ? DecodeUtf8(buffer) : null;
    }

    private static string? DecodeUtf8(byte[] buffer)
    {
        int terminator = Array.IndexOf(buffer, (byte)0);
        string value = Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private const int NvmlSuccess = 0;
    private const uint DriverVersionCapacity = 96;
    private const uint DeviceNameCapacity = 192;
    private const uint DeviceUuidCapacity = 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetString([Out] byte[] value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetCudaDriverVersion(out int cudaDriverVersion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetString(IntPtr device, [Out] byte[] value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}

internal sealed record NvmlGpuSnapshot(
    string Name,
    string? Uuid,
    ulong TotalMemoryBytes,
    ulong FreeMemoryBytes);

internal sealed record NvmlProbeResult(
    IReadOnlyList<NvmlGpuSnapshot> Devices,
    string? DriverVersion,
    int? CudaMajorVersion)
{
    public static NvmlProbeResult Empty { get; } = new([], null, null);
}
