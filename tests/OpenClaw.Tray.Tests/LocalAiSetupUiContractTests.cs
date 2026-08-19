namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUiContractTests
{
    private static string Root => TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void Welcome_AdvertisesQualifiedLocalAiBeforeGatewayChoice()
    {
        string xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml");
        string source = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs");
        string window = Read("src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs");
        string capabilities = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"WelcomeLocalAiAvailable\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WelcomeLocalAiHardware\"", xaml);
        Assert.Contains("Local AI available", xaml);
        Assert.Contains("without installing a gateway or configuring Local AI", xaml);
        Assert.Contains("DetectLocalAiAvailabilityAsync", source);
        Assert.Contains("eligibility.CanInstall", source);
        Assert.Contains("gpu.Name", source);
        Assert.Contains("GetLocalAiHardwareAsync", window);
        Assert.Contains("await setupWindow.GetLocalAiHardwareAsync()", source);
        Assert.Contains("await setupWindow.GetLocalAiHardwareAsync()", capabilities);
    }

    [Fact]
    public void CapabilityProfile_DoesNotGateLocalAiOrWslNetworking()
    {
        string xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml");

        Assert.Contains("does not require Full access", xaml);
        Assert.Contains("Local AI needs mirrored WSL networking", xaml);
        Assert.Contains("LocalAiNetworkingConsentCheckBox", xaml);
    }

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

    [Fact]
    public void ProgressPage_MapsEveryLocalAiStepAndUsesTypedByteProgress()
    {
        string source = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs");
        string context = Read("src", "OpenClaw.SetupEngine", "SetupContext.cs");

        string[] stepIds =
        [
            "preflight-local-ai-hardware",
            "configure-local-ai-wsl-networking",
            "acquire-local-ai-runtime",
            "acquire-local-ai-model",
            "persist-local-ai-manifest",
            "start-local-ai-runtime",
            "capture-local-ai-gpu-baseline",
            "verify-local-ai-inference",
            "verify-local-ai-gpu-load",
            "verify-local-ai-wsl",
            "configure-local-ai-gateway",
        ];
        foreach (string stepId in stepIds)
            Assert.Contains($"\"{stepId}\"", source);

        Assert.Contains("IProgress<SetupDetailProgressEvent>? DetailProgress", context);
        Assert.Contains("ctx.DetailProgress = new DirectProgress<SetupDetailProgressEvent>", source);
        Assert.Contains("SetupDetailProgressUnit.Bytes", source);
        Assert.Contains("progress-local-ai", source);
        int detailHandlerStart = source.IndexOf("private void OnDetailProgress", StringComparison.Ordinal);
        int logHandlerStart = source.IndexOf("private void OnLogEmitted", StringComparison.Ordinal);
        Assert.DoesNotContain("LogEntry", source[detailHandlerStart..logHandlerStart]);
    }

    [Fact]
    public void CompletePage_ReportsVerifiedOnDemandLocalAiAndOpensChat()
    {
        string xaml = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml");
        string source = Read("src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiCompletionSummary\"", xaml);
        Assert.Contains("review.LocalAiEnabled", source);
        Assert.Contains("model loads on the first request", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LaunchButton.Content = \"Open chat\"", source);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
}
