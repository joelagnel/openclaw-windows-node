using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.SetupEngine;

public sealed record LocalAiGpuMemory(
    string Name,
    ulong TotalBytes,
    ulong FreeBytes);

public sealed record LocalAiHardwareSnapshot(
    IReadOnlyList<LocalAiGpuMemory> NvidiaGpus,
    string? ProbeError = null);

public sealed record LocalAiHardwareEligibilityResult(
    bool IsEligible,
    LocalAiGpuMemory? SelectedGpu,
    string Message);

public interface ILocalAiHardwareProbe
{
    LocalAiHardwareSnapshot Probe();
}

/// <summary>
/// The first qualified recipe needs the complete model and runtime overhead to
/// remain GPU-resident. This threshold is deliberately expressed in MiB because
/// that is the unit reported by NVIDIA's driver tools on Windows.
/// </summary>
public static class LocalAiHardwareEligibilityPolicy
{
    public const ulong MinimumGpuMemoryMiB = 24_000;
    public const ulong MinimumGpuMemoryBytes = MinimumGpuMemoryMiB * 1024 * 1024;

    public static LocalAiHardwareEligibilityResult Evaluate(LocalAiHardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var selected = snapshot.NvidiaGpus
            .Where(gpu => gpu.TotalBytes >= MinimumGpuMemoryBytes)
            .OrderByDescending(gpu => gpu.TotalBytes)
            .FirstOrDefault();
        if (selected is not null)
        {
            return new LocalAiHardwareEligibilityResult(
                true,
                selected,
                $"{selected.Name} has {ToMiB(selected.TotalBytes):N0} MiB of GPU memory.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ProbeError))
        {
            return new LocalAiHardwareEligibilityResult(
                false,
                null,
                "Local AI hardware could not be verified. The qualified Qwen3.6 recipe requires " +
                $"an NVIDIA GPU with at least {MinimumGpuMemoryMiB:N0} MiB of GPU memory.");
        }

        var largest = snapshot.NvidiaGpus
            .OrderByDescending(gpu => gpu.TotalBytes)
            .FirstOrDefault();
        var detected = largest is null
            ? "No compatible NVIDIA GPU was detected."
            : $"{largest.Name} reports {ToMiB(largest.TotalBytes):N0} MiB.";
        return new LocalAiHardwareEligibilityResult(
            false,
            largest,
            $"{detected} The qualified Qwen3.6 recipe requires at least " +
            $"{MinimumGpuMemoryMiB:N0} MiB of GPU memory.");
    }

    private static ulong ToMiB(ulong bytes) => bytes / (1024 * 1024);
}

/// <summary>
/// Reads CUDA-visible NVIDIA device memory from the NVML library installed by
/// the Windows display driver. Unsupported accelerators, such as an NPU exposed
/// by the same driver, are omitted when they do not provide GPU memory data.
/// </summary>
public sealed class NvmlLocalAiHardwareProbe : ILocalAiHardwareProbe
{
    private const int NvmlSuccess = 0;
    private const int DeviceNameCapacity = 192;

    public LocalAiHardwareSnapshot Probe()
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable("NVML hardware detection is available only on Windows.");

        IntPtr library;
        try
        {
            if (!TryLoadNvml(out library))
                return Unavailable("The NVIDIA NVML library is not installed.");
        }
        catch (BadImageFormatException)
        {
            return Unavailable("The installed NVIDIA NVML library is incompatible.");
        }

        var initialized = false;
        NvmlShutdown? shutdown = null;
        try
        {
            var initialize = GetDelegate<NvmlInitialize>(library, "nvmlInit_v2");
            shutdown = GetDelegate<NvmlShutdown>(library, "nvmlShutdown");
            var getCount = GetDelegate<NvmlDeviceGetCount>(library, "nvmlDeviceGetCount_v2");
            var getHandle = GetDelegate<NvmlDeviceGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getName = GetDelegate<NvmlDeviceGetName>(library, "nvmlDeviceGetName");
            var getMemory = GetDelegate<NvmlDeviceGetMemoryInfo>(library, "nvmlDeviceGetMemoryInfo");

            var status = initialize();
            if (status != NvmlSuccess)
                return Unavailable($"NVML initialization failed with status {status}.");
            initialized = true;

            status = getCount(out var count);
            if (status != NvmlSuccess)
                return Unavailable($"NVML device enumeration failed with status {status}.");

            var devices = new List<LocalAiGpuMemory>();
            for (uint index = 0; index < count; index++)
            {
                if (getHandle(index, out var device) != NvmlSuccess)
                    continue;
                if (getMemory(device, out var memory) != NvmlSuccess || memory.Total == 0)
                    continue;

                devices.Add(new LocalAiGpuMemory(
                    ReadDeviceName(device, index, getName),
                    memory.Total,
                    memory.Free));
            }

            return new LocalAiHardwareSnapshot(devices);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException
            or BadImageFormatException
            or MarshalDirectiveException
            or SEHException)
        {
            return Unavailable("The installed NVIDIA NVML library is incompatible.");
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

    private static string ReadDeviceName(IntPtr device, uint index, NvmlDeviceGetName getName)
    {
        var buffer = Marshal.AllocHGlobal(DeviceNameCapacity);
        try
        {
            Marshal.Copy(new byte[DeviceNameCapacity], 0, buffer, DeviceNameCapacity);
            if (getName(device, buffer, DeviceNameCapacity) != NvmlSuccess)
                return $"NVIDIA GPU {index}";

            var bytes = new byte[DeviceNameCapacity];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var terminator = Array.IndexOf(bytes, (byte)0);
            var name = Encoding.UTF8.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length).Trim();
            return string.IsNullOrWhiteSpace(name) ? $"NVIDIA GPU {index}" : name;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryLoadNvml(out IntPtr library)
    {
        foreach (var candidate in GetNvmlLibraryCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, out library))
                return true;
        }

        library = IntPtr.Zero;
        return false;
    }

    internal static IReadOnlyList<string> GetNvmlLibraryCandidates()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll"),
        };
        return candidates
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static T GetDelegate<T>(IntPtr library, string exportName) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, exportName));

    private static LocalAiHardwareSnapshot Unavailable(string error) => new([], error);

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
    private delegate int NvmlDeviceGetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetName(IntPtr device, IntPtr name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}

public sealed class PreflightLocalAiHardwareStep : SetupStep
{
    private readonly ILocalAiHardwareProbe _probe;

    public const string StepId = "preflight-local-ai-hardware";
    public override string Id => StepId;
    public override string DisplayName => "Check Local AI hardware";
    public override bool CanRetry => false;

    public PreflightLocalAiHardwareStep()
        : this(new NvmlLocalAiHardwareProbe())
    {
    }

    internal PreflightLocalAiHardwareStep(ILocalAiHardwareProbe probe)
    {
        _probe = probe;
    }

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var eligibility = LocalAiHardwareEligibilityPolicy.Evaluate(_probe.Probe());
        if (!eligibility.IsEligible)
            return Task.FromResult(StepResult.Fail(eligibility.Message));

        ctx.Logger.Info($"Local AI hardware qualified: {eligibility.Message}");
        return Task.FromResult(StepResult.Ok(eligibility.Message));
    }
}
