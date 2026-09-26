using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the first setup screen (SecurityNoticePage) as a benefits-first introduction that
/// a non-technical user can follow: model-neutral copy without gateway jargon, a brief risk
/// notice, links to the detailed security and architecture docs, localized strings, and a
/// layout that still works in a narrow window.
/// </summary>
public sealed class SetupWelcomeIntroContractTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName SetupTextUid = XNamespace.Get("using:OpenClaw.SetupEngine.UI") + "SetupText.Uid";
    private static readonly string[] Locales = ["en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw"];
    private static readonly string[] LocalizableAttributes = ["Text", "Title", "Message", "Content"];
    private static readonly Regex ModelNames = new(
        @"\b(Claude|ChatGPT|GPT|Gemini|Llama|Qwen|Copilot)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex Jargon = new(@"\b(gateways?|nodes?)\b", RegexOptions.IgnoreCase);

    private const string SecurityDocsUri = "https://docs.openclaw.ai/gateway/security";
    private const string ArchitectureDocsUri = "https://docs.openclaw.ai/concepts/architecture";

    private static string PagePath(string fileName) => Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages", fileName);

    private static XDocument LoadXaml() => XDocument.Load(PagePath("SecurityNoticePage.xaml"));

    [Fact]
    public void IntroCopy_IsModelNeutral_AvoidsGatewayJargon_AndUsesNoEmDashes()
    {
        var values = UserFacingAttributes(LoadXaml()).Select(a => a.Value)
            .Concat(IntroResources("en-us").Values)
            .ToList();

        Assert.NotEmpty(values);
        foreach (var value in values)
        {
            Assert.DoesNotMatch(ModelNames, value);
            Assert.DoesNotContain("gateway", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("node", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", value);
        }
    }

    [Fact]
    public void IntroCopy_LeadsWithBenefits_ThenBriefRiskNotice_ThenDetailLinks()
    {
        var xaml = File.ReadAllText(PagePath("SecurityNoticePage.xaml"));

        AssertInOrder(
            xaml,
            "setup:SetupText.Uid=\"Onboarding_Intro_Title\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_Summary\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_BenefitTasksTitle\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_BenefitChatTitle\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_BenefitControlTitle\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_RiskNotice\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_SecurityLink\"",
            "setup:SetupText.Uid=\"Onboarding_Intro_ArchitectureLink\"");

        var risk = IntroResources("en-us")["Onboarding_Intro_RiskNotice.Message"];
        Assert.Contains("can make mistakes", risk);
        Assert.Contains("see and change things on this PC", risk);
        Assert.Contains("your own PC, not one you share", risk);
        Assert.True(risk.Length <= 260, $"Risk notice should stay brief; was {risk.Length} characters.");
    }

    [Fact]
    public void EveryVisibleString_HasSetupTextUid_AndResourcesInEverySupportedLocale()
    {
        var attributes = UserFacingAttributes(LoadXaml()).ToList();
        Assert.NotEmpty(attributes);

        foreach (var attribute in attributes)
        {
            var uid = attribute.Parent!.Attribute(SetupTextUid)?.Value;
            Assert.False(
                string.IsNullOrEmpty(uid),
                $"<{attribute.Parent.Name.LocalName} {attribute.Name}=\"{attribute.Value}\"> needs a setup:SetupText.Uid.");

            foreach (var locale in Locales)
            {
                var resources = IntroResources(locale);
                var key = $"{uid}.{attribute.Name.LocalName}";
                Assert.True(resources.ContainsKey(key), $"{locale} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(resources[key]), $"{locale} has an empty {key}.");
            }
        }
    }

    [Fact]
    public void IntroTranslations_AreProvidedForEveryNonEnglishLocale()
    {
        var english = IntroResources("en-us");
        foreach (var locale in Locales.Where(locale => locale != "en-us"))
        {
            var translated = IntroResources(locale);
            Assert.Equal(english.Keys.OrderBy(k => k), translated.Keys.OrderBy(k => k));
            foreach (var (key, value) in translated)
            {
                Assert.NotEqual(english[key], value);
                Assert.DoesNotMatch(ModelNames, value);
                Assert.DoesNotMatch(Jargon, value);
                Assert.DoesNotContain("\u2014", value);
            }
        }
    }

    [Fact]
    public void DetailLinks_PointToSecurityAndArchitectureDocs()
    {
        var links = LoadXaml().Descendants()
            .Where(e => e.Name.LocalName == "HyperlinkButton")
            .ToList();

        Assert.Equal(
            [ArchitectureDocsUri, SecurityDocsUri],
            links.Select(link => link.Attribute("NavigateUri")?.Value).OrderBy(uri => uri));
        Assert.All(links, link => Assert.NotNull(link.Attribute(SetupTextUid)));
    }

    /// <summary>
    /// In short windows only the introduction and benefits scroll. The risk notice and its
    /// detail links sit in their own row between the scroller and Continue, so the notice is on
    /// screen whenever Continue is. The scroller holds only text, so it must take focus itself
    /// for keyboard users to scroll it.
    /// </summary>
    [Fact]
    public void IntroLayout_ScrollsBenefitsInNarrowWindows_AndKeepsTheRiskNoticeNextToContinue()
    {
        var doc = LoadXaml();
        var root = doc.Root!.Elements().Single();
        var scroller = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "ScrollViewer");
        var notice = Assert.Single(doc.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_Intro_RiskNotice");
        var continueButton = Assert.Single(doc.Descendants(), e =>
            e.Name.LocalName == "Button" && e.Attribute(XamlNs + "Name")?.Value == "ContinueButton");
        XElement RowOf(XElement element) => element.AncestorsAndSelf().First(e => e.Parent == root);

        Assert.Equal("*,Auto,Auto", root.Attribute("RowDefinitions")?.Value);
        Assert.Equal("0", RowOf(scroller).Attribute("Grid.Row")?.Value);
        Assert.Equal("True", scroller.Attribute("IsTabStop")?.Value);
        Assert.Equal("{x:Bind IntroTitle}", scroller.Attribute("AutomationProperties.LabeledBy")?.Value);
        Assert.Equal("1", RowOf(notice).Attribute("Grid.Row")?.Value);
        Assert.Equal("2", RowOf(continueButton).Attribute("Grid.Row")?.Value);
        Assert.All(
            doc.Descendants().Where(e => e.Name.LocalName == "HyperlinkButton"),
            link => Assert.Same(RowOf(notice), RowOf(link)));
        Assert.Contains(scroller.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_Intro_BenefitControlDetail");

        var title = Assert.Single(doc.Descendants(), e => e.Attribute(SetupTextUid)?.Value == "Onboarding_Intro_Title");
        Assert.Equal("Level1", title.Attribute("AutomationProperties.HeadingLevel")?.Value);
        Assert.Equal("IntroTitle", title.Attribute(XamlNs + "Name")?.Value);
        Assert.All(
            doc.Descendants().Where(e => e.Name.LocalName == "TextBlock"),
            text => Assert.Equal("Wrap", text.Attribute("TextWrapping")?.Value));
    }

    [Fact]
    public void Continue_StillOpensTheSetupChoices()
    {
        var source = File.ReadAllText(PagePath("SecurityNoticePage.xaml.cs"));

        Assert.Contains("SetupWindow.Active?.NavigateToWelcome();", source);
    }

    private static IEnumerable<XAttribute> UserFacingAttributes(XDocument doc) =>
        doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.Namespace == XNamespace.None && LocalizableAttributes.Contains(a.Name.LocalName))
            .Where(a => !a.Value.StartsWith('{'));

    private static Dictionary<string, string> IntroResources(string locale)
    {
        var path = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw");
        return XDocument.Load(path).Descendants("data")
            .Where(e => e.Attribute("name")!.Value.StartsWith("Onboarding_Intro_", StringComparison.Ordinal))
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);
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
