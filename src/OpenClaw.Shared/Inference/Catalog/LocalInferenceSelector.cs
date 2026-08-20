using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Whether catalog selection produced a complete native inference plan.</summary>
public enum LocalInferenceSelectionStatus
{
    Selected = 0,
    Unsupported = 1,
}

/// <summary>Stable reason returned when no inference plan can be selected.</summary>
public enum LocalInferenceSelectionFailureCode
{
    None = 0,
    UnsupportedArchitecture = 1,
    UnsupportedGpu = 2,
    UnknownModel = 3,
}

/// <summary>Whether a caller accepted the catalog default or named a model explicitly.</summary>
public enum LocalInferenceModelSelectionOrigin
{
    Default = 0,
    Explicit = 1,
}

/// <summary>A complete, immutable native inference choice.</summary>
public sealed record LocalInferencePlan(
    SupportedHardwareProfile HardwareProfile,
    LlamaRuntimeVariant Runtime,
    LocalModelInfo Model,
    LocalInferenceModelSelectionOrigin ModelSelectionOrigin);

/// <summary>The deterministic result of selecting from the pinned local inference catalog.</summary>
public sealed record LocalInferenceSelectionResult
{
    private LocalInferenceSelectionResult(
        LocalInferenceSelectionStatus status,
        LocalInferenceSelectionFailureCode failureCode,
        LocalInferencePlan? plan)
    {
        Status = status;
        FailureCode = failureCode;
        Plan = plan;
    }

    public LocalInferenceSelectionStatus Status { get; }
    public LocalInferenceSelectionFailureCode FailureCode { get; }
    public LocalInferencePlan? Plan { get; }
    public bool IsSelected => Status == LocalInferenceSelectionStatus.Selected;

    internal static LocalInferenceSelectionResult Selected(LocalInferencePlan plan) =>
        new(LocalInferenceSelectionStatus.Selected, LocalInferenceSelectionFailureCode.None, plan);

    internal static LocalInferenceSelectionResult Unsupported(LocalInferenceSelectionFailureCode failureCode) =>
        new(LocalInferenceSelectionStatus.Unsupported, failureCode, null);
}

/// <summary>
/// Pure selection from a hardware snapshot and optional model ID. Qualified
/// names are preferred; otherwise a supported architecture may use the
/// documented minimum NVIDIA memory threshold. Model-fit eligibility remains
/// separate and explicit model choices are never silently downgraded.
/// </summary>
public static class LocalInferenceSelector
{
    public static LocalInferenceSelectionResult Select(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (hardware.CpuArchitecture is not (Architecture.X64 or Architecture.Arm64))
            return LocalInferenceSelectionResult.Unsupported(
                LocalInferenceSelectionFailureCode.UnsupportedArchitecture);

        SupportedHardwareProfile? profile = FindPreferredProfile(hardware);
        if (profile is null)
        {
            LocalInferenceSelectionFailureCode failureCode = HasQualifiedGpuNameOnAnotherArchitecture(hardware)
                ? LocalInferenceSelectionFailureCode.UnsupportedArchitecture
                : LocalInferenceSelectionFailureCode.UnsupportedGpu;
            return LocalInferenceSelectionResult.Unsupported(failureCode);
        }

        LocalModelInfo? model;
        LocalInferenceModelSelectionOrigin modelSelectionOrigin;
        if (string.IsNullOrWhiteSpace(requestedModelId))
        {
            model = LocalModelCatalog.Default;
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Default;
        }
        else
        {
            model = LocalModelCatalog.Find(requestedModelId);
            if (model is null)
                return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.UnknownModel);
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Explicit;
        }

        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Variants.Single(
            candidate => string.Equals(candidate.Id, profile.RuntimeId, StringComparison.Ordinal));
        return LocalInferenceSelectionResult.Selected(
            new LocalInferencePlan(profile, runtime, model, modelSelectionOrigin));
    }

    private static SupportedHardwareProfile? FindPreferredProfile(HostHardwareInfo hardware)
    {
        string[] reportedNvidiaNames = hardware.NvidiaGpus.Select(gpu => gpu.Name).ToArray();
        foreach (SupportedHardwareProfile candidate in SupportedHardwareProfiles.Profiles)
        {
            if (candidate.IsMemoryQualifiedFallback)
                continue;
            if (candidate.Architecture != hardware.CpuArchitecture)
                continue;
            if (reportedNvidiaNames.Any(
                name => SupportedHardwareProfiles.Find(hardware.CpuArchitecture, name)?.Id == candidate.Id))
            {
                return candidate;
            }
        }

        return hardware.NvidiaGpus.Any(IsMemoryQualifiedFallbackCandidate)
            ? SupportedHardwareProfiles.FindMemoryQualifiedFallback(hardware.CpuArchitecture)
            : null;
    }

    internal static bool IsMemoryQualifiedFallbackCandidate(GpuInfo gpu) =>
        gpu.GpuVisibleMemoryBytes is >= LocalInferenceEligibility.MinimumQualifiedGpuMemoryBytes;

    private static bool HasQualifiedGpuNameOnAnotherArchitecture(HostHardwareInfo hardware)
    {
        Architecture otherArchitecture = hardware.CpuArchitecture == Architecture.X64
            ? Architecture.Arm64
            : Architecture.X64;
        return hardware.NvidiaGpus.Any(
            gpu => SupportedHardwareProfiles.Find(otherArchitecture, gpu.Name) is not null);
    }
}
