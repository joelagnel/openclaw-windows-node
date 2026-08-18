namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUiContractTests
{
    [Fact]
    public void InstallReview_OffersQualifiedLocalAiAndRequiresMissingWslConsent()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml");
        var code = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");
        var summary = Read(root, "src", "OpenClaw.SetupEngine", "SetupReviewSummary.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiInstallReviewCard\"", xaml);
        Assert.Contains("Ollama v0.32.14 for Windows", xaml);
        Assert.DoesNotContain("~1.4 GB", xaml);
        Assert.Contains("uses a healthy Ollama already running on Windows", xaml);
        Assert.Contains("downloads the exact model when it is not already available", xaml);
        Assert.Contains("qwen3.6:35b-a3b-mtp-q4_K_M, ~23 GB", xaml);
        Assert.Contains("256K provider context; OpenClaw-managed Ollama uses FP16 KV cache", xaml);
        Assert.Contains("global WSL change and one-time shutdown", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml);
        Assert.Contains("new WslGlobalConfigManager(configPath, backupDirectory).Inspect()", code);
        Assert.Contains("_localAiNetworkingConsentRequired = !status.IsMirrored", code);
        Assert.Contains("config.SkipWizard = enabled || _skipWizardWithoutLocalAi", code);
        Assert.Contains("config.LocalAi.AllowGlobalWslNetworkingChange", code);
        Assert.Contains("LocalAiNetworkingConsentCheckBox.IsChecked == true", code);
        Assert.Contains("BuildLocalAiEngineDescription(", summary);
        Assert.Contains("OllamaReleasePolicy.Resolve(architecture)", summary);
        Assert.Contains("FormatDownloadSize(artifact.SizeBytes)", summary);
    }

    [Fact]
    public void Progress_MapsEveryLocalAiPhaseWithoutChangingStandardRows()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var code = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs");
        var xaml = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml");

        Assert.Contains("configure-local-ai-wsl-networking", code);
        Assert.Contains("acquire-local-ai-engine", code);
        Assert.Contains("download-local-ai-model", code);
        Assert.Contains("verify-local-ai-wsl", code);
        Assert.Contains("configure-local-ai-gateway", code);
        Assert.Contains("Preparing Ollama for Local AI", code);
        Assert.Contains("Downloading Qwen3.6 35B", code);
        Assert.Contains("Configuring the 256K provider context", code);
        Assert.Contains("if (!localAiEnabled)", code);
        Assert.Contains("return StandardStepGroups", code);
        Assert.Contains("group.ShowsDeterminateProgress", code);
        Assert.Contains("AutomationProperties.SetAutomationId(_rowBorder", code);
        Assert.Contains("ShowsDeterminateProgress: true", code);
        Assert.Contains("new ProgressBar", code);
        Assert.Contains("modelRow.SetProgress(38)", code);
        Assert.Contains("context.DetailProgress += OnDetailProgress", code);
        Assert.Contains("context.DetailProgress -= OnDetailProgress", code);
        Assert.Contains("private void OnDetailProgress", code);
        Assert.Contains("row.SetProgress(completed * 100d / total)", code);
        Assert.Contains("row.SetDetail(detail)", code);
        Assert.Contains("SetupDetailProgressUnit.Bytes", code);
        Assert.Contains("FormatDecimalBytes", code);
        Assert.Contains("else if (group.GroupId == \"install-cli\")", code);
        Assert.Contains("new StepGroup(\"configure-gateway\"", code);
        Assert.Contains("new StepGroup(\"install-service\"", code);
        Assert.Contains("AutomationProperties.AutomationId=\"SetupProgressStatus\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    [Fact]
    public void CompletePage_ShowsLocalAiSummaryAndOpenChatOnlyWhenEnabled()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml");
        var code = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs");

        Assert.Contains("x:Name=\"LocalAiSummaryCard\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SetupCompleteLocalAiSummary\"", xaml);
        Assert.Contains("Local AI ready", xaml);
        Assert.Contains("summary.LocalAiEnabled ? \"Open chat\" : \"Finish\"", code);
        Assert.Contains("summary.LocalAiEnabled ? \"Local AI is ready\" : \"All set!\"", code);
        Assert.Contains("LocalAiSummaryCard.Visibility = summary.LocalAiEnabled", code);
    }

    [Fact]
    public void DebugPreview_HasDeterministicLocalAiReviewAndProgressRoutes()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var window = Read(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs");
        var capabilities = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs");
        var progress = Read(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs");

        Assert.Contains("\"capabilities-review\" => typeof(CapabilitiesPage)", window);
        Assert.Contains("\"capabilities-review-consent\" => typeof(CapabilitiesPage)", window);
        Assert.Contains("\"progress-local-ai\" => typeof(ProgressPage)", window);
        Assert.Contains("forceNetworkingConsent: previewPage == \"capabilities-review-consent\"", capabilities);
        Assert.Contains("GoToStep(localAiReviewPreview ? 3 : 1)", capabilities);
        Assert.Contains("RenderLocalAiProgressPreview()", progress);
        Assert.Contains("qwen3.6:35b-a3b-mtp-q4_K_M download in progress", progress);
    }

    private static string Read(string root, params string[] pathParts) =>
        File.ReadAllText(Path.Combine([root, .. pathParts]));
}
