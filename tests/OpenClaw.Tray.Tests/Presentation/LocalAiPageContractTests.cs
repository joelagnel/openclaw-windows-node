using System.Xml.Linq;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageContractTests
{
    private static readonly string[] ResourceKeys =
    [
        "HubWindow_LocalAi.Content",
        "LocalAiPage_Title.Text",
        "LocalAiPage_Intro.Text",
        "LocalAiPage_EngineHeading.Text",
        "LocalAiPage_EngineVersionLabel.Text",
        "LocalAiPage_StartButton.Content",
        "LocalAiPage_StopButton.Content",
        "LocalAiPage_RestartButton.Content",
        "LocalAiPage_OpenLogsButton.Content",
        "LocalAiPage_ModelHeading.Text",
        "LocalAiPage_ModelTagLabel.Text",
        "LocalAiPage_ContextLabel.Text",
        "LocalAiPage_KvCacheLabel.Text",
        "LocalAiPage_RetrySetupButton.Content",
        "LocalAiPage_GatewayHeading.Text",
        "LocalAiPage_RepairConnectionButton.Content",
        "LocalAiPage_OpenChatButton.Content",
        "LocalAiPage_Engine_Running",
        "LocalAiPage_Engine_Stopped",
        "LocalAiPage_Engine_Error",
        "LocalAiPage_Engine_Managed",
        "LocalAiPage_Engine_External",
        "LocalAiPage_Engine_NotManaged",
        "LocalAiPage_Model_NotInstalled",
        "LocalAiPage_Model_Unknown",
        "LocalAiPage_Gateway_Connected",
        "LocalAiPage_Gateway_Connecting",
        "LocalAiPage_Gateway_NeedsAttention",
        "LocalAiPage_Gateway_Error",
        "LocalAiPage_Gateway_Disconnected",
        "LocalAiPage_Value_Unknown",
        "Command_GoToLocalAi_Title",
        "Command_GoToLocalAi_Subtitle",
    ];

    [Fact]
    public void PageAndViewModelKeepRuntimeMechanicsBehindRuntime()
    {
        var page = ReadTraySource("Pages", "LocalAiPage.xaml.cs");
        var viewModel = ReadTraySource("Presentation", "LocalAiPageViewModel.cs");
        var registration = ReadTraySource("Presentation", "AppServiceRegistration.cs");
        var app = ReadTraySource("App.xaml.cs");

        Assert.Contains("ILocalAiRuntime _runtime", viewModel);
        Assert.DoesNotContain("Microsoft.UI", viewModel);
        Assert.DoesNotContain("System.Diagnostics.Process", viewModel);
        Assert.DoesNotContain("HttpClient", viewModel);
        Assert.DoesNotContain("OllamaRuntimeService", viewModel);
        Assert.DoesNotContain("Process.", page);
        Assert.DoesNotContain("HttpClient", page);
        Assert.DoesNotContain("OllamaRuntimeService", page);
        Assert.Contains("_viewModel?.StartAsync()", page);
        Assert.Contains("_viewModel?.RetrySetup()", page);
        Assert.Contains("services.AddTransient<LocalAiPageViewModel>()", registration);
        Assert.Contains("[typeof(Pages.LocalAiPage)] = typeof(LocalAiPageViewModel)", app);
        Assert.Contains("void IAppCommands.OpenLocalAiLogs()", app);
        Assert.Contains("new LocalAiPaths(AppIdentity.ResolveSetupLocalDataDirectory()).LogsDirectory", app);
    }

    [Fact]
    public void PageUsesAccessibleTextAndColorStatusWithStableAutomationIds()
    {
        var xaml = ReadTraySource("Pages", "LocalAiPage.xaml");

        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiPageMarker\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiEngineStatus\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiModelStatus\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiModelStatusDot\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiGatewayStatus\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRetrySetup\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiStart\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiStop\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRestart\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiOpenLogs\"", xaml);
        Assert.True(xaml.Split("AutomationProperties.LiveSetting=\"Polite\"").Length - 1 >= 4);
        Assert.Contains("x:Name=\"EngineStatusDot\"", xaml);
        Assert.Contains("x:Name=\"ModelStatusDot\"", xaml);
        Assert.Contains("x:Name=\"GatewayStatusDot\"", xaml);
        Assert.Contains("Text=\"256K\"", xaml);
        Assert.Contains("Text=\"FP16\"", xaml);
        Assert.DoesNotContain('\u2014', xaml);
    }

    [Fact]
    public void NavigationRailPlacesLocalAiUnderThisComputer()
    {
        var hub = ReadTraySource("Windows", "HubWindow.xaml");
        var header = hub.IndexOf("HubWindow_NavigationViewItemHeader_129", StringComparison.Ordinal);
        var localAi = hub.IndexOf("Tag=\"local-ai\"", StringComparison.Ordinal);
        var voice = hub.IndexOf("Tag=\"voice\"", StringComparison.Ordinal);

        Assert.True(header >= 0 && header < localAi);
        Assert.True(localAi < voice);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiNavigationItem\"", hub);
    }

    [Fact]
    public void AllLocalesContainLocalAiPageAndCommandResources()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw" })
        {
            var path = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw");
            var values = XDocument.Load(path)
                .Root!
                .Elements("data")
                .ToDictionary(
                    element => (string)element.Attribute("name")!,
                    element => element.Element("value")?.Value,
                    StringComparer.Ordinal);

            foreach (var key in ResourceKeys)
            {
                Assert.True(values.TryGetValue(key, out var value), $"{locale} is missing {key}");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{locale} has an empty {key}");
                Assert.DoesNotContain('\u2014', value!);
            }
        }
    }

    private static string ReadTraySource(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI" }
                .Concat(parts)
                .ToArray()));
}
