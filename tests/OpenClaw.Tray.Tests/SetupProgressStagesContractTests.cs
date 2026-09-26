using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the setup progress page as three user-facing stages (Check your PC, Install OpenClaw,
/// Finish setup) driven by SetupStageTracker, with each installation step and the live log
/// kept under a collapsed Details section, and actions such as Tailscale authorization kept in
/// view.
/// </summary>
public sealed class SetupProgressStagesContractTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName SetupTextUid = XNamespace.Get("using:OpenClaw.SetupEngine.UI") + "SetupText.Uid";
    private static readonly string[] Locales = ["en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw"];

    private static string PagePath(string fileName) => Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages", fileName);

    private static XDocument LoadXaml() => XDocument.Load(PagePath("ProgressPage.xaml"));

    private static string LoadSource() => File.ReadAllText(PagePath("ProgressPage.xaml.cs")).Replace("\r\n", "\n");

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == name);

    [Fact]
    public void Progress_ShowsStagesFirst_WithStepsAndLogUnderCollapsedDetails()
    {
        var xaml = File.ReadAllText(PagePath("ProgressPage.xaml"));
        AssertInOrder(xaml, "x:Name=\"TailscaleAuthorizationPanel\"", "x:Name=\"StagesPanel\"", "x:Name=\"DetailsExpander\"");

        var doc = LoadXaml();
        var details = Named(doc, "DetailsExpander");
        Assert.Equal("False", details.Attribute("IsExpanded")?.Value);
        Assert.Equal("Onboarding_Progress_Details", details.Attribute(SetupTextUid)?.Value);
        foreach (var technical in new[] { "StepsPanel", "LogText", "OpenLogButton" })
            Assert.Contains(details.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == technical);
        foreach (var visible in new[] { "StagesPanel", "TailscaleAuthorizationPanel" })
            Assert.DoesNotContain(Named(doc, visible), details.Descendants());
        Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Expander");
        var liveActivity = Assert.Single(details.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_Progress_LiveActivity");
        Assert.Equal("Level3", liveActivity.Attribute("AutomationProperties.HeadingLevel")?.Value);
    }

    [Fact]
    public void Stages_AreBuiltFromTheTracker_AndHideStagesTheRunDoesNotHave()
    {
        var source = LoadSource();
        var build = ExtractMethod(source, "private void BuildStageRows");

        Assert.Contains("Enum.GetValues<SetupStage>()", build);
        Assert.Contains("if (!_stages.Includes(stage))", build);
        Assert.Contains("_stages = new SetupStageTracker(_activeStepIds);", ExtractMethod(source, "protected override void OnNavigatedTo"));

        var english = Resources("en-us");
        Assert.Equal("Check your PC", english["Onboarding_Progress_Stage_CheckPc"]);
        Assert.Equal("Install OpenClaw", english["Onboarding_Progress_Stage_Install"]);
        Assert.Equal("Finish setup", english["Onboarding_Progress_Stage_Finish"]);
    }

    [Fact]
    public void StepProgress_UpdatesStages_BeforeLookingUpDetailRows()
    {
        // Some steps (for example reconcile-local-ai-installation) have no detail row, and the
        // row lookup returns early for them; the stages must still see those steps finish, and
        // the stage must not keep naming the previous row while they run.
        AssertInOrder(
            ExtractMethod(LoadSource(), "private void OnStepProgress"),
            "_stages.StepFinished(e.StepId, outcome);",
            "_stages.StepStarted(e.StepId);",
            "RefreshStages();",
            "var groupIndex",
            "if (groupIndex < 0)",
            "ShowStageActivity(e.StepId, null);",
            "return;",
            "ShowStageActivity(e.StepId, group.DisplayName);");
        Assert.Contains("if (activity is null || activity == StageName(stage))", ExtractMethod(LoadSource(), "private void ShowStageActivity"));
    }

    [Fact]
    public void LastStage_CompletesOnlyAfterTheRunSucceeds()
    {
        var source = LoadSource();

        AssertInOrder(
            ExtractMethod(source, "private async Task StartPipelineAsync"),
            "if (success)",
            "_stages.RunSucceeded();",
            "RefreshStages();");
        Assert.Single(Regex.Matches(source, @"_stages\.RunSucceeded\(\)"));
    }

    [Fact]
    public void Stages_ShowStatusForAssistiveTech_AndAFailedStageKeepsItsLastActivity()
    {
        var refresh = ExtractMethod(LoadSource(), "private void RefreshStages");

        Assert.Contains("SetupStageState.Failed => StepStatus.Failed", refresh);
        Assert.Contains("if (state is SetupStageState.Pending or SetupStageState.Done)", refresh);
        Assert.Contains("row.SetAccessibleName(SetupLocalization.Format($\"Onboarding_Progress_StageState_{state}\", StageName(stage)));", refresh);
    }

    /// <summary>
    /// A stage shows its current Details group as its activity, so a group that spanned two
    /// stages would name the wrong step for the active stage.
    /// </summary>
    [Fact]
    public void EveryDetailsGroup_BelongsToExactlyOneStage()
    {
        var trackerSource = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine", "SetupStageTracker.cs"));
        var stageByStep = Regex.Matches(trackerSource, @"\[""([\w-]+)""\] = SetupStage\.(\w+)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var groups = Regex.Matches(ExtractStepGroups(LoadSource()), @"\(""([\w-]+)"",[^\[]*\[([^\]]*)\]\)").ToList();

        Assert.Equal(18, groups.Count);
        foreach (var group in groups)
        {
            var steps = Regex.Matches(group.Groups[2].Value, @"""([\w-]+)""").Select(m => m.Groups[1].Value).ToList();
            Assert.All(steps, step => Assert.True(stageByStep.ContainsKey(step), $"{step} has no stage."));
            Assert.Single(steps.Select(step => stageByStep[step]).Distinct());
        }
    }

    [Fact]
    public void DetailsGroupNames_AreLocalized_AndAStageDoesNotRepeatItsOwnName()
    {
        var source = LoadSource();
        var groups = ExtractStepGroups(source);

        Assert.Equal(18, Regex.Matches(groups, @"SetupLocalization\.GetString\(""Onboarding_Progress_Group_\w+""\)").Count);
        Assert.DoesNotContain("\"Check compatibility\"", groups);
        Assert.Contains("activity == StageName(stage)", ExtractMethod(source, "private void ShowStageActivity"));
    }

    /// <summary>
    /// A failed step can be followed by up to a minute of rollback per step before the Complete
    /// page opens, so the status line says setup hit a problem (and is undoing its changes). A
    /// step that only needs a restart is not a problem; the Complete page asks for the restart.
    /// </summary>
    [Fact]
    public void AFailedStep_UpdatesAndAnnouncesTheStatusLine()
    {
        var source = LoadSource();

        AssertInOrder(
            ExtractMethod(source, "private void OnStepProgress"),
            "_stages.StepFinished(e.StepId, outcome);",
            "if (outcome is StepOutcome.Failed or StepOutcome.FailedTerminal && !e.RequiresRestart)",
            "ShowFailureStatus();");
        var status = ExtractMethod(source, "private void ShowFailureStatus");
        Assert.Contains("_config!.RollbackOnFailure ? \"Onboarding_Progress_FailedUndoing\" : \"Onboarding_Progress_Failed\"", status);
        Assert.Contains("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", status);
        Assert.Equal("Polite", Named(LoadXaml(), "SubtitleText").Attribute("AutomationProperties.LiveSetting")?.Value);
    }

    [Fact]
    public void LocalAiRecovery_NamesItsInstallStageForLocalAi()
    {
        Assert.Contains(
            "stage == SetupStage.Install && _localAiRecoveryOnly",
            ExtractMethod(LoadSource(), "private string StageName"));
        Assert.Equal("Set up Local AI", Resources("en-us")["Onboarding_Progress_Stage_InstallLocalAi"]);
    }

    [Fact]
    public void LocalAiPreview_EnablesLocalAi_BeforeBuildingRows()
    {
        AssertInOrder(
            ExtractMethod(LoadSource(), "protected override void OnNavigatedTo"),
            "if (SetupPreview.RequestedPage == \"progress-local-ai\")",
            "_config.LocalAi.Enabled = true;",
            "BuildStepRows();");
    }

    [Fact]
    public void ProgressCopy_IsLocalizedInEverySupportedLocale()
    {
        var english = Resources("en-us");
        var keys = english.Keys.Where(key => key.StartsWith("Onboarding_Progress_", StringComparison.Ordinal)).ToList();
        var expected = new[] { "CheckPc", "Install", "InstallLocalAi", "Finish" }.Select(s => "Onboarding_Progress_Stage_" + s)
            .Concat(new[] { "Pending", "Active", "Done", "Failed" }.Select(s => "Onboarding_Progress_StageState_" + s))
            .Concat(Regex.Matches(ExtractStepGroups(LoadSource()), @"""(Onboarding_Progress_Group_\w+)""").Select(m => m.Groups[1].Value))
            .Concat(["Onboarding_Progress_Details.Header", "Onboarding_Progress_LiveActivity.Text",
                "Onboarding_Progress_Failed", "Onboarding_Progress_FailedUndoing"]);
        Assert.Equal(expected.Order(), keys.Order());

        foreach (var locale in Locales.Where(l => l != "en-us"))
        {
            var resources = Resources(locale);
            foreach (var key in keys)
            {
                Assert.NotEqual(english[key], resources[key]);
                Assert.Equal(english[key].Contains("{0}"), resources[key].Contains("{0}"));
            }
        }
    }

    private static string ExtractStepGroups(string source)
    {
        var start = source.IndexOf("StepGroups =", StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing StepGroups.");
        return source[start..source.IndexOf("];", start, StringComparison.Ordinal)];
    }

    private static Dictionary<string, string> Resources(string locale) =>
        XDocument.Load(Path.Combine(
                TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"))
            .Descendants("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing '{signature}'.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var index = 0;
        foreach (var marker in markers)
        {
            var next = source.IndexOf(marker, index, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Expected '{marker}' after index {index}.");
            index = next + marker.Length;
        }
    }
}
