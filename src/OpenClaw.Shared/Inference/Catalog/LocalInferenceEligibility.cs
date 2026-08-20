namespace OpenClaw.Shared.Inference.Catalog;

public enum LocalInferenceEligibilityStatus
{
    Eligible = 0,
    EligibleButBusy = 1,
    Unsupported = 2,
}

public enum LocalInferenceEligibilityFailureCode
{
    None = 0,
    CatalogSelectionFailed = 1,
    HardwareFactsIncomplete = 2,
    InsufficientGpuMemory = 3,
    DriverTooOld = 4,
    CudaCapabilityTooLow = 5,
}

public sealed record LocalInferenceEligibilityResult(
    LocalInferenceEligibilityStatus Status,
    LocalInferenceEligibilityFailureCode FailureCode,
    LocalInferenceSelectionFailureCode SelectionFailureCode,
    LocalInferencePlan? Plan,
    GpuInfo? SelectedGpu,
    long RequiredFreeMemoryBytes,
    long? AvailableFreeMemoryBytes)
{
    public bool CanInstall => Status is
        LocalInferenceEligibilityStatus.Eligible or
        LocalInferenceEligibilityStatus.EligibleButBusy;
}

/// <summary>
/// Applies the measured first-release capacity and driver guardrails after the
/// pure SKU/model catalog selection. Total GPU-visible memory is stable
/// capacity. Free GPU-visible memory is launch readiness and never changes the
/// selected model automatically.
/// </summary>
public static class LocalInferenceEligibility
{
    public const long MinimumQualifiedGpuMemoryMiB = 24_000;
    public const long MinimumQualifiedGpuMemoryBytes = MinimumQualifiedGpuMemoryMiB * 1024 * 1024;
    public static Version MinimumNvidiaDriverVersion { get; } = new(615, 0);

    public static LocalInferenceEligibilityResult Evaluate(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        LocalInferenceSelectionResult selection = LocalInferenceSelector.Select(hardware, requestedModelId);
        if (!selection.IsSelected || selection.Plan is null)
        {
            return Unsupported(
                LocalInferenceEligibilityFailureCode.CatalogSelectionFailed,
                selection.FailureCode);
        }

        LocalInferencePlan plan = selection.Plan;
        GpuInfo? gpu = plan.HardwareProfile.IsMemoryQualifiedFallback
            ? hardware.NvidiaGpus.FirstOrDefault(LocalInferenceSelector.IsMemoryQualifiedFallbackCandidate)
            : hardware.NvidiaGpus.FirstOrDefault(
                candidate => SupportedHardwareProfiles.Find(hardware.CpuArchitecture, candidate.Name)?.Id ==
                    plan.HardwareProfile.Id);
        if (gpu is null ||
            string.IsNullOrWhiteSpace(gpu.StableId) ||
            gpu.GpuVisibleMemoryBytes is not > 0 ||
            string.IsNullOrWhiteSpace(gpu.DriverVersion) ||
            gpu.CudaMajorVersion is null)
        {
            return Unsupported(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete);
        }

        long totalEligibleMemoryBytes = GetEffectiveGpuMemoryBytes(
            gpu.GpuVisibleMemoryBytes.Value,
            gpu.SharedGpuMemoryBytes,
            plan.HardwareProfile.UsesSharedGpuMemory);
        if (totalEligibleMemoryBytes < MinimumQualifiedGpuMemoryBytes)
            return Unsupported(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, selectedGpu: gpu);

        if (!Version.TryParse(gpu.DriverVersion, out Version? driverVersion) ||
            driverVersion < MinimumNvidiaDriverVersion)
        {
            return Unsupported(LocalInferenceEligibilityFailureCode.DriverTooOld, selectedGpu: gpu);
        }

        if (gpu.CudaMajorVersion < plan.Runtime.CudaVersion.Major)
            return Unsupported(LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow, selectedGpu: gpu);

        long requiredFreeMemoryBytes = plan.Model.Weights.SizeBytes;
        long? availableFreeMemoryBytes = GetAvailableGpuMemoryBytes(
            gpu,
            plan.HardwareProfile.UsesSharedGpuMemory);
        LocalInferenceEligibilityStatus status =
            availableFreeMemoryBytes is not null && availableFreeMemoryBytes < requiredFreeMemoryBytes
                ? LocalInferenceEligibilityStatus.EligibleButBusy
                : LocalInferenceEligibilityStatus.Eligible;

        return new LocalInferenceEligibilityResult(
            status,
            LocalInferenceEligibilityFailureCode.None,
            LocalInferenceSelectionFailureCode.None,
            plan,
            gpu,
            requiredFreeMemoryBytes,
            availableFreeMemoryBytes);
    }

    private static LocalInferenceEligibilityResult Unsupported(
        LocalInferenceEligibilityFailureCode failureCode,
        LocalInferenceSelectionFailureCode selectionFailureCode = LocalInferenceSelectionFailureCode.None,
        GpuInfo? selectedGpu = null) =>
        new(
            LocalInferenceEligibilityStatus.Unsupported,
            failureCode,
            selectionFailureCode,
            null,
            selectedGpu,
            0,
            selectedGpu?.FreeGpuVisibleMemoryBytes);

    private static long GetEffectiveGpuMemoryBytes(
        long gpuMemoryBytes,
        long? sharedGpuMemoryBytes,
        bool usesSharedGpuMemory)
    {
        if (!usesSharedGpuMemory || sharedGpuMemoryBytes is not > 0)
            return gpuMemoryBytes;

        return sharedGpuMemoryBytes.Value > long.MaxValue - gpuMemoryBytes
            ? long.MaxValue
            : gpuMemoryBytes + sharedGpuMemoryBytes.Value;
    }

    private static long? GetAvailableGpuMemoryBytes(GpuInfo gpu, bool usesSharedGpuMemory)
    {
        if (gpu.FreeGpuVisibleMemoryBytes is not { } freeGpuMemoryBytes)
            return null;

        if (usesSharedGpuMemory &&
            gpu.SharedGpuMemoryBytes is > 0 &&
            gpu.FreeSharedGpuMemoryBytes is null)
        {
            return null;
        }

        return GetEffectiveGpuMemoryBytes(
            freeGpuMemoryBytes,
            gpu.FreeSharedGpuMemoryBytes,
            usesSharedGpuMemory);
    }
}
