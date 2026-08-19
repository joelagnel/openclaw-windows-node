namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUiContractTests
{
    private static string Root => TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void InstallReview_OffersQualifiedLocalAiWithExplicitModelChoice()
    {
        string xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml");
        string source = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiInstallReviewCard\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiSetupToggle\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiModelSelector\"", xaml);
        Assert.Contains("recommended model is selected by default", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changes it only when you choose another model", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LocalInferenceEligibility.Evaluate", source);
        Assert.Contains("new NvmlHostHardwareProbe().Probe()", source);
        Assert.Contains("_config.LocalAi.SelectedModelId = modelId", source);
        Assert.Contains("LocalInferenceEligibilityStatus.Eligible", source);
        Assert.DoesNotContain("Ollama", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ollama", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallReview_RequiresExplicitConsentForGlobalWslChange()
    {
        string xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml");
        string source = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiNetworkingConsentPanel\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiNetworkingConsentCheckBox\"", xaml);
        Assert.Contains("stop all running WSL distributions once", xaml);
        Assert.Contains("No distributions are deleted", xaml);
        Assert.Contains("new WslGlobalConfigManager", source);
        Assert.Contains("WslMirroredNetworkingConsent", source);
        Assert.Contains("_localAiNetworkingConsentRequired", source);
    }

    [Fact]
    public void LocalAiSetup_SkipsProviderWizardAndHasDeterministicReviewRoutes()
    {
        string page = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");
        string window = Read("src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs");

        Assert.Contains("config.SkipWizard = enabled || _skipWizardWithoutLocalAi", page);
        Assert.Contains("\"capabilities-review\" => typeof(CapabilitiesPage)", window);
        Assert.Contains("\"capabilities-review-consent\" => typeof(CapabilitiesPage)", window);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
}
