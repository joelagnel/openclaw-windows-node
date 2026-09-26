using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the first capabilities step (CapabilitiesPage step 1) as a security-level choice a
/// non-technical user can make: plain level names, a disclosure of what the selected level
/// allows, when OpenClaw asks first, and how commands are contained (including the uncontained
/// fallback), with individual capabilities still available under Advanced.
/// </summary>
public sealed class SetupSecurityLevelScreenContractTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName SetupTextUid = XNamespace.Get("using:OpenClaw.SetupEngine.UI") + "SetupText.Uid";
    private static readonly string[] Locales = ["en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw"];
    private static readonly string[] CapabilityKeys = ["System", "Canvas", "Screen", "Camera", "Location", "Browser", "Tts", "Stt"];

    private static string PagePath(string fileName) => Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages", fileName);

    private static XDocument LoadXaml() => XDocument.Load(PagePath("CapabilitiesPage.xaml"));

    private static string LoadSource() => File.ReadAllText(PagePath("CapabilitiesPage.xaml.cs")).Replace("\r\n", "\n");

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == name);

    [Fact]
    public void Step_UsesASecurityHeading_AndOffersPlainLevelsFromMostToLeastRestrictive()
    {
        var goToStep = ExtractMethod(LoadSource(), "private void GoToStep");
        Assert.Contains("SetupLocalization.GetString(\"Onboarding_SecurityLevel_Title\")", goToStep);

        var group = Named(LoadXaml(), "ProfileRadio");
        var radios = group.Elements().Where(e => e.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(
            ["Onboarding_SecurityLevel_LookOnly", "Onboarding_SecurityLevel_Balanced", "Onboarding_SecurityLevel_FullAccess"],
            radios.Select(r => r.Attribute(SetupTextUid)?.Value));
        Assert.Contains(radios[1].Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_SecurityLevel_RecommendedBadge");
        Assert.Equal("Onboarding_SecurityLevel_Group", group.Attribute(SetupTextUid)?.Value);

        // "Access level", not "Security level": the tray's Sandbox page already has a Security
        // level card whose Recommended preset means something else.
        var english = Resources("en-us");
        Assert.Equal("Choose an access level", english["Onboarding_SecurityLevel_Title"]);
        Assert.Equal("Access level", english["Onboarding_SecurityLevel_Group.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.Equal("Balanced (Recommended)", english["Onboarding_SecurityLevel_Balanced.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.Equal("Level2", Named(LoadXaml(), "StepTitle").Attribute("AutomationProperties.HeadingLevel")?.Value);

        // Allow Once doesn't stop later prompts, so commands ask unless they're already allowed.
        Assert.Contains("unless you've allowed them", english["Onboarding_SecurityLevel_BalancedDetail.Text"]);
        Assert.All(Locales, locale => Assert.DoesNotContain(
            Resources(locale)["SandboxPage_SecurityLevel.Text"], Resources(locale)["Onboarding_SecurityLevel_Title"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Disclosure_ShowsWhatTheLevelAllows_WhenItAsksFirst_AndHowCommandsAreContained()
    {
        var doc = LoadXaml();
        var step1 = Named(doc, "Step1Content");
        var disclosure = Named(doc, "LevelDisclosure");
        Assert.Contains(disclosure, step1.Descendants());
        foreach (var part in new[] { "LevelAllowsGrid", "LevelAsksFirstPanel", "LevelSandboxPanel" })
            Assert.Contains(disclosure.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == part);
        foreach (var uid in new[] { "AsksCommands", "AsksDevices", "Sandbox", "SandboxFallback" })
            Assert.Contains(disclosure.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_SecurityLevel_" + uid);

        var english = Resources("en-us");
        var asks = english["Onboarding_SecurityLevel_AsksCommands.Text"];
        foreach (var button in new[] { "ExecApproval_Deny", "ExecApproval_AllowOnce", "ExecApproval_AllowAlways" })
            Assert.Contains(english[button], asks);
        Assert.Contains("denied", asks);

        // The sandbox grants read access to the folder a command runs in, and screen, camera,
        // and location are the only capabilities with a consent prompt.
        var sandbox = english["Onboarding_SecurityLevel_Sandbox.Text"];
        foreach (var protectedItem in new[] { "OpenClaw's settings", "SSH keys", "browser profiles", "PowerShell history" })
            Assert.Contains(protectedItem, sandbox);
        Assert.Contains("folder they run in", sandbox);
        Assert.DoesNotContain("personal folders", sandbox);

        // The deny list covers the default locations, so the copy says so instead of promising "never".
        var hedge = new Dictionary<string, (string Says, string[] Never)>
        {
            ["en-us"] = ("usual folders", ["never"]),
            ["fr-fr"] = ("dossiers habituels", ["jamais"]),
            ["nl-nl"] = ("gebruikelijke mappen", ["nooit"]),
            ["pt-br"] = ("pastas habituais", ["nunca"]),
            ["zh-cn"] = ("默认文件夹", ["绝不", "永不", "从不", "决不"]),
            ["zh-tw"] = ("預設資料夾", ["絕不", "永不", "從不", "決不"]),
        };
        foreach (var locale in Locales)
        {
            var text = Resources(locale)["Onboarding_SecurityLevel_Sandbox.Text"];
            Assert.Contains(hedge[locale].Says, text);
            Assert.All(hedge[locale].Never, word => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains("normal access", english["Onboarding_SecurityLevel_SandboxFallback.Text"]);
        var devices = english["Onboarding_SecurityLevel_AsksDevices.Text"];
        Assert.Contains("microphone", devices);
        Assert.Contains("browser", devices);

        // Each locale names the tray pages the way its navigation does.
        foreach (var locale in Locales)
        {
            var resources = Resources(locale);
            Assert.Contains(resources["HubWindow_NavigationViewItem_Sandbox.Content"], resources["Onboarding_SecurityLevel_Sandbox.Text"]);
            Assert.Contains(resources["HubWindow_NavigationViewItem_136.Content"], resources["Onboarding_SecurityLevel_Intro.Text"]);
        }

        var headings = new[] { ("DisclosureTitle", "Level3"), ("AsksFirstTitle", "Level4"), ("SandboxTitle", "Level4") };
        foreach (var (uid, level) in headings)
        {
            var heading = Assert.Single(disclosure.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_SecurityLevel_" + uid);
            Assert.Equal(level, heading.Attribute("AutomationProperties.HeadingLevel")?.Value);
        }
    }

    [Fact]
    public void Disclosure_FollowsTheActualCapabilityToggles()
    {
        var source = LoadSource();
        var update = ExtractMethod(source, "private void UpdateLevelDisclosure");

        Assert.Contains("\"Onboarding_SecurityLevel_AllowedItem\"", update);
        Assert.Contains("\"Onboarding_SecurityLevel_NotAllowedItem\"", update);
        Assert.Contains("AutomationProperties.SetName(", update);
        Assert.Contains("LevelSandboxPanel.Visibility", update);
        Assert.Contains("LevelAsksFirstPanel.Visibility", update);
        Assert.Contains("IsCapOn(\"Stt\")", update);
        Assert.Contains("IsCapOn(\"Browser\")", update);
        Assert.Contains("\"\\uE73E\"", update);
        Assert.Contains("\"\\uE711\"", update);
        // Profile changes flip the toggles while _suppressProfile is set, so the disclosure must
        // refresh before that early return to follow every change, including the entry default.
        AssertInOrder(ExtractMethod(source, "private void Capability_Toggled"), "UpdateLevelDisclosure();", "if (_suppressProfile)");
    }

    [Fact]
    public void Page_UsesTheTestedLevelMapping()
    {
        var source = LoadSource();

        Assert.Contains("SetupSecurityLevels.EnabledCapabilities(", source);
        Assert.Contains("SetupSecurityLevels.Detect(", ExtractMethod(source, "private int DetectProfileIndex"));
        Assert.DoesNotContain("ProfileReadOnly", source);
        Assert.DoesNotContain("ProfileStandard", source);
        // An unselected group (index -1) must not be applied as a level.
        Assert.Contains("ProfileRadio.SelectedIndex < 0", ExtractMethod(source, "private void Profile_Changed"));

        // The page's toggle list and the catalog name the same capabilities in the same order.
        var table = source[source.IndexOf("Capabilities =", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("];", StringComparison.Ordinal)];
        Assert.Equal(CapabilityKeys, Regex.Matches(table, "\\(\"(\\w+)\",").Select(m => m.Groups[1].Value));
        // System registers no clipboard command, and the sandbox copy above says so.
        Assert.DoesNotContain("clipboard", table, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Step_ContentColumnShrinksForNarrowWindows()
    {
        var scroller = Named(LoadXaml(), "Scroller");

        Assert.Null(scroller.Attribute("Width"));
        Assert.Equal("600", scroller.Attribute("MaxWidth")?.Value);
    }

    [Fact]
    public void IndividualCapabilities_StayAvailableUnderAdvanced()
    {
        var expander = Named(LoadXaml(), "CapabilityExpander");
        Assert.Contains(expander.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == "CapGrid");

        var presentation = ExtractMethod(LoadSource(), "private void UpdateCapabilityProfilePresentation");
        Assert.Contains("\"Onboarding_SecurityLevel_CustomExpander\"", presentation);
        Assert.Contains("\"Onboarding_SecurityLevel_AdvancedExpander\"", presentation);
        Assert.Equal("Advanced: choose individual capabilities", Resources("en-us")["Onboarding_SecurityLevel_AdvancedExpander"]);
    }

    [Fact]
    public void Transcript_SummarizesTheChosenLevelByName()
    {
        var source = LoadSource();

        Assert.Contains(
            "AppendTranscript(SetupLocalization.GetString(\"Onboarding_SecurityLevel_TranscriptQuestion\"), ProfileSummary());",
            ExtractMethod(source, "private async Task PrimaryClickAsync"));
        var summary = ExtractMethod(source, "private string ProfileSummary");
        foreach (var title in new[] { "LookOnlyTitle", "BalancedTitle", "FullAccessTitle" })
            Assert.Contains($"\"Onboarding_SecurityLevel_{title}.Text\"", summary);
        Assert.Contains("\"Onboarding_SecurityLevel_Custom\"", summary);
    }

    [Fact]
    public void Step_OpensAtTheTopOfTheActiveCard()
    {
        var scroll = ExtractMethod(LoadSource(), "private void ScrollActiveIntoView");

        AssertInOrder(
            scroll,
            "ActiveCard.TransformToVisual(",
            "var target = Math.Max(0, cardTop - 44);",
            "Scroller.ChangeView(null, target, null);",
            "return;");
    }

    [Fact]
    public void LevelCopy_IsLocalizedInEverySupportedLocale_AndNeverCallsALevelSafe()
    {
        var english = Resources("en-us");
        var keys = english.Keys.Where(key => key.StartsWith("Onboarding_SecurityLevel_", StringComparison.Ordinal)).ToList();
        // Every level string the page uses is a resource, and every resource is used.
        var referenced = Named(LoadXaml(), "Step1Content").DescendantsAndSelf()
            .Where(e => e.Attribute(SetupTextUid) is not null)
            .SelectMany(e => keys.Where(key => key.StartsWith(e.Attribute(SetupTextUid)!.Value + ".", StringComparison.Ordinal)))
            .Concat(Regex.Matches(LoadSource(), "\"(Onboarding_SecurityLevel_[A-Za-z_.]*[A-Za-z])\"").Select(m => m.Groups[1].Value))
            .Concat(CapabilityKeys.Select(key => "Onboarding_SecurityLevel_Cap_" + key));
        Assert.Equal(keys.Order(), referenced.Distinct().Order());

        foreach (var locale in Locales.Where(l => l != "en-us"))
        {
            var resources = Resources(locale);
            foreach (var key in keys)
            {
                Assert.True(resources.ContainsKey(key), $"{locale} is missing {key}.");
                Assert.NotEqual(english[key], resources[key]);
                Assert.Equal(Regex.Matches(english[key], @"\{\d\}").Count, Regex.Matches(resources[key], @"\{\d\}").Count);
            }
        }

        foreach (var key in keys)
        {
            Assert.DoesNotMatch(new Regex(@"\bsafe", RegexOptions.IgnoreCase), english[key]);
            Assert.All(Locales, locale => Assert.DoesNotContain("\u2014", Resources(locale)[key]));
        }
    }

    [Fact]
    public void CapabilityLabels_CoverEveryCapabilityTheLevelsCanTurnOn()
    {
        var update = ExtractMethod(LoadSource(), "private void BuildLevelDisclosureRows");

        Assert.Contains("\"Onboarding_SecurityLevel_Cap_\" + key", update);
        Assert.Contains("SetupSecurityLevels.Capabilities", update);
        Assert.Contains("AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);", update);
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
