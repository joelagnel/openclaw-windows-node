using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class SupportedHardwareProfilesTests
{
    [Fact]
    public void CatalogContainsOnlyQualifiedSkuIdentitiesInStablePreferenceOrder()
    {
        Assert.Collection(
            SupportedHardwareProfiles.Profiles,
            profile => AssertProfile(
                profile,
                "rtx-pro-6000-blackwell-workstation",
                "NVIDIA RTX PRO 6000 Blackwell Workstation Edition",
                RuntimeArchitecture.X64,
                LlamaRuntimeCatalog.X64RuntimeId),
            profile => AssertProfile(
                profile,
                "geforce-rtx-5090",
                "NVIDIA GeForce RTX 5090",
                RuntimeArchitecture.X64,
                LlamaRuntimeCatalog.X64RuntimeId),
            profile => AssertProfile(
                profile,
                "rtx-spark-n1x",
                "NVIDIA RTX Spark N1X",
                RuntimeArchitecture.Arm64,
                LlamaRuntimeCatalog.Arm64RuntimeId));
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", "rtx-pro-6000-blackwell-workstation")]
    [InlineData(RuntimeArchitecture.X64, "nvidia geforce rtx 5090", "geforce-rtx-5090")]
    [InlineData(RuntimeArchitecture.X64, "  NVIDIA   GeForce RTX 5090  ", "geforce-rtx-5090")]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA RTX Spark N1X", "rtx-spark-n1x")]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)", "rtx-spark-n1x")]
    public void FindMatchesOnlyExplicitNormalizedNames(
        RuntimeArchitecture architecture,
        string reportedName,
        string expectedProfileId)
    {
        Assert.Equal(expectedProfileId, SupportedHardwareProfiles.Find(architecture, reportedName)?.Id);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA GeForce RTX 5090 Laptop GPU")]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA GeForce RTX 5090 Ti")]
    [InlineData(RuntimeArchitecture.X64, "GeForce RTX 5090")]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX PRO 6000 Blackwell Server Edition")]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX 6000 Ada Generation")]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA RTX Spark N1X Server")]
    [InlineData(RuntimeArchitecture.Arm64, "RTX Spark N1X")]
    public void FindRejectsNearMatches(RuntimeArchitecture architecture, string reportedName)
    {
        Assert.Null(SupportedHardwareProfiles.Find(architecture, reportedName));
    }

    [Theory]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA GeForce RTX 5090")]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX Spark N1X")]
    [InlineData(RuntimeArchitecture.X86, "NVIDIA GeForce RTX 5090")]
    public void FindRejectsQualifiedNameOnWrongArchitecture(
        RuntimeArchitecture architecture,
        string reportedName)
    {
        Assert.Null(SupportedHardwareProfiles.Find(architecture, reportedName));
    }

    [Fact]
    public void IdentitiesAndReportedNamesAreUniqueAndResolveToPinnedRuntimes()
    {
        Assert.Equal(
            SupportedHardwareProfiles.Profiles.Count,
            SupportedHardwareProfiles.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count());

        string[] normalizedNames = SupportedHardwareProfiles.Profiles
            .SelectMany(profile => profile.ReportedGpuNames)
            .Select(name => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();
        Assert.Equal(normalizedNames.Length, normalizedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(
            SupportedHardwareProfiles.Profiles,
            profile =>
            {
                Assert.Equal(profile.RuntimeId, LlamaRuntimeCatalog.Find(profile.Architecture)?.Id);
                Assert.Same(LocalInferenceCatalogProvenance.NvidiaCair, profile.CatalogProvenance);
            });
    }

    private static void AssertProfile(
        SupportedHardwareProfile profile,
        string id,
        string displayName,
        RuntimeArchitecture architecture,
        string runtimeId)
    {
        Assert.Equal(id, profile.Id);
        Assert.Equal(displayName, profile.DisplayName);
        Assert.Equal(architecture, profile.Architecture);
        Assert.Equal(runtimeId, profile.RuntimeId);
    }
}
