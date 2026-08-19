using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests;

public class SupportedHardwareProfilesTests
{
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
}
