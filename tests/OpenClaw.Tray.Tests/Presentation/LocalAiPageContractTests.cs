using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageContractTests
{
    [Fact]
    public void Page_ExposesAccessibleEvidenceAndManagedControls()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "LocalAiPage.xaml"));

        string[] automationIds =
        [
            "LocalAiPageMarker",
            "LocalAiEngineStatus",
            "LocalAiEngineOwnership",
            "LocalAiStart",
            "LocalAiStop",
            "LocalAiRestart",
            "LocalAiOpenLogs",
            "LocalAiModelStatus",
            "LocalAiModelName",
            "LocalAiRetrySetup",
            "LocalAiGatewayStatus",
            "LocalAiRepairConnection",
            "LocalAiOpenChat",
        ];
        foreach (string automationId in automationIds)
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", xaml);

        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("llama-server", xaml);
        Assert.DoesNotContain("Ollama", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_IsAvailableWithoutGatewayConnectivity()
    {
        Assert.Equal(HubPageKind.LocalAi, HubPageRegistry.ResolvePage("local-ai"));
        Assert.False(HubPageRegistry.IsGatewayPageTag("local-ai"));
    }
}
