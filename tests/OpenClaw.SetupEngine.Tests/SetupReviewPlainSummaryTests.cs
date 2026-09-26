namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Pins the locale-neutral facts the plain-language setup review is built from. The review
/// hides implementation details behind an expander, so exposure and installer-trust facts
/// must stay explicit and correct for the summary to remain honest.
/// </summary>
public sealed class SetupReviewPlainSummaryTests
{
    [Fact]
    public void Exposure_DefaultsToThisPcOnly()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig());

        Assert.Equal(SetupGatewayExposure.ThisPcOnly, summary.Exposure);
    }

    [Fact]
    public void Exposure_LanBind_IsLocalNetwork()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig { Gateway = { Bind = "lan" } });

        Assert.Equal(SetupGatewayExposure.LocalNetwork, summary.Exposure);
    }

    [Fact]
    public void Exposure_Tailscale_IsTailnet()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig { Tailscale = new TailscaleConfig { Enabled = true } });

        Assert.Equal(SetupGatewayExposure.Tailnet, summary.Exposure);
    }

    [Fact]
    public void Exposure_LanBindWithTailscale_ReportsTheLocalNetwork()
    {
        // Setup refuses this combination, so the review reports the LAN exposure rather than
        // implying that only the tailnet can connect.
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig
        {
            Gateway = { Bind = "lan" },
            Tailscale = new TailscaleConfig { Enabled = true },
        });

        Assert.Equal(SetupGatewayExposure.LocalNetwork, summary.Exposure);
    }

    [Fact]
    public void InstallerTrust_OfficialHttpsInstaller_IsOfficial()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig());

        Assert.Equal(SetupInstallerTrust.Official, summary.InstallerTrust);
        Assert.NotNull(summary.InstallerHost);
    }

    [Fact]
    public void InstallerTrust_CustomHttpsInstaller_IsCustom_AndNamesTheHost()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig
        {
            Gateway = { InstallUrl = "https://example.test/install.sh", Version = "2026.8.1" },
        });

        Assert.Equal(SetupInstallerTrust.Custom, summary.InstallerTrust);
        Assert.Equal("example.test", summary.InstallerHost);
    }

    [Fact]
    public void InstallerTrust_NonHttpsInstaller_IsInsecure()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig
        {
            Gateway = { InstallUrl = "http://example.test/install.sh", Version = "2026.8.1" },
        });

        Assert.Equal(SetupInstallerTrust.InsecureUrl, summary.InstallerTrust);
        Assert.Null(summary.InstallerHost);
        Assert.DoesNotContain("before downloading", summary.InstallerDescription);
    }

    [Fact]
    public void InstallerTrust_CustomInstaller_NamesLookalikeHostsInTheirAsciiForm()
    {
        // "\u0435" is a Cyrillic "e"; showing the Unicode host would read as the Latin name.
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig
        {
            Gateway = { InstallUrl = "https://\u0435xample.test/install.sh", Version = "2026.8.1" },
        });

        Assert.Equal(SetupInstallerTrust.Custom, summary.InstallerTrust);
        Assert.StartsWith("xn--", summary.InstallerHost);
    }
}
