using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// A qualified NVIDIA Windows system identity. Reported GPU names are an
/// explicit allowlist. Spark N1X accepts its stable reported-name prefix because
/// NVIDIA reports multiple core-count variants of that qualified ARM64 SKU.
/// </summary>
public sealed record SupportedHardwareProfile
{
    public SupportedHardwareProfile(
        string id,
        string displayName,
        Architecture architecture,
        string runtimeId,
        IReadOnlyList<string> reportedGpuNames,
        CatalogProvenance catalogProvenance,
        bool usesSharedGpuMemory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        ArgumentNullException.ThrowIfNull(reportedGpuNames);
        ArgumentNullException.ThrowIfNull(catalogProvenance);
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new ArgumentOutOfRangeException(nameof(architecture));
        if (reportedGpuNames.Count == 0 || reportedGpuNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty reported GPU name is required.", nameof(reportedGpuNames));

        Id = id;
        DisplayName = displayName;
        Architecture = architecture;
        RuntimeId = runtimeId;
        ReportedGpuNames = Array.AsReadOnly(reportedGpuNames.ToArray());
        CatalogProvenance = catalogProvenance;
        UsesSharedGpuMemory = usesSharedGpuMemory;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Architecture Architecture { get; }
    public string RuntimeId { get; }
    public IReadOnlyList<string> ReportedGpuNames { get; }
    public CatalogProvenance CatalogProvenance { get; }
    public bool UsesSharedGpuMemory { get; }
}

/// <summary>
/// CAIR-qualified SKU identities for the first native llama-server release.
/// Catalog order is the deterministic preference order when a host exposes
/// more than one qualified adapter.
/// </summary>
public static class SupportedHardwareProfiles
{
    public const string RtxPro6000ProfileId = "rtx-pro-6000-blackwell-workstation";
    public const string Rtx5090ProfileId = "geforce-rtx-5090";
    public const string RtxSparkN1XProfileId = "rtx-spark-n1x";

    private static readonly ReadOnlyCollection<SupportedHardwareProfile> s_profiles = Array.AsReadOnly(
        new[]
        {
            Profile(
                RtxPro6000ProfileId,
                "NVIDIA RTX PRO 6000 Blackwell Workstation Edition",
                Architecture.X64,
                LlamaRuntimeCatalog.X64RuntimeId,
                ["NVIDIA RTX PRO 6000 Blackwell Workstation Edition"]),
            Profile(
                Rtx5090ProfileId,
                "NVIDIA GeForce RTX 5090",
                Architecture.X64,
                LlamaRuntimeCatalog.X64RuntimeId,
                ["NVIDIA GeForce RTX 5090"]),
            Profile(
                RtxSparkN1XProfileId,
                "NVIDIA RTX Spark N1X",
                Architecture.Arm64,
                LlamaRuntimeCatalog.Arm64RuntimeId,
                [
                    "NVIDIA RTX Spark N1X",
                    "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                ],
                usesSharedGpuMemory: true),
        });

    public static IReadOnlyList<SupportedHardwareProfile> Profiles => s_profiles;

    public static SupportedHardwareProfile? Find(Architecture architecture, string? reportedGpuName)
    {
        if (string.IsNullOrWhiteSpace(reportedGpuName))
            return null;

        string normalizedName = NormalizeReportedGpuName(reportedGpuName);
        return s_profiles.SingleOrDefault(
            profile =>
                profile.Architecture == architecture &&
                MatchesReportedGpuName(profile, normalizedName));
    }

    internal static string NormalizeReportedGpuName(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool MatchesReportedGpuName(
        SupportedHardwareProfile profile,
        string normalizedReportedGpuName)
    {
        if (profile.ReportedGpuNames.Any(
                name => string.Equals(
                    NormalizeReportedGpuName(name),
                    normalizedReportedGpuName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return profile.Id == RtxSparkN1XProfileId &&
            normalizedReportedGpuName.Contains(
                NormalizeReportedGpuName("NVIDIA RTX Spark N1X"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static SupportedHardwareProfile Profile(
        string id,
        string displayName,
        Architecture architecture,
        string runtimeId,
        string[] reportedGpuNames,
        bool usesSharedGpuMemory = false) =>
        new(
            id,
            displayName,
            architecture,
            runtimeId,
            reportedGpuNames,
            LocalInferenceCatalogProvenance.NvidiaCair,
            usesSharedGpuMemory);
}
