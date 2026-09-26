using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the setup review step (CapabilitiesPage step 3) as a plain-language summary grouped by
/// where OpenClaw runs, what it can do, AI, and optional connections. Implementation detail lives
/// in a Technical details expander, while security-sensitive choices and warnings stay visible,
/// each summarized choice links back to the screen that owns it, and Install &amp; set up remains
/// the explicit action that starts installation.
/// </summary>
public sealed class SetupReviewScreenContractTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName SetupTextUid = XNamespace.Get("using:OpenClaw.SetupEngine.UI") + "SetupText.Uid";
    private static readonly string[] Locales = ["en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw"];
    private static readonly string[] LocalizableAttributes = ["Text", "Title", "Message", "Content", "Header"];
    private const string AutomationNameSuffix = ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static string PagePath(string fileName) => Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Pages", fileName);

    private static XDocument LoadXaml() => XDocument.Load(PagePath("CapabilitiesPage.xaml"));

    private static string LoadSource() => File.ReadAllText(PagePath("CapabilitiesPage.xaml.cs")).Replace("\r\n", "\n");

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == name);

    [Fact]
    public void ReviewStep_GroupsChoicesInPlainLanguageSections_ThenTechnicalDetails()
    {
        var xaml = File.ReadAllText(PagePath("CapabilitiesPage.xaml"));

        AssertInOrder(
            xaml,
            "x:Name=\"Step3Content\"",
            "setup:SetupText.Uid=\"Onboarding_Review_Intro\"",
            "x:Name=\"ReviewWhereGroup\"",
            "x:Name=\"ReviewCapabilitiesGroup\"",
            "x:Name=\"ReviewAiGroup\"",
            "x:Name=\"ReviewConnectionsGroup\"",
            "x:Name=\"ReviewDetailsExpander\"");

        var doc = LoadXaml();
        var details = Named(doc, "ReviewDetailsExpander");
        foreach (var technical in new[] { "InstallDistroTitleText", "InstallCliDetailText", "GatewayServiceDetailText", "ExactCommandsText" })
            Assert.Contains(details.Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == technical);

        Assert.Contains(Named(doc, "ReviewAiGroup").Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == "LocalAiInstallReviewCard");
        Assert.Contains(Named(doc, "ReviewConnectionsGroup").Descendants(), e => e.Attribute(XamlNs + "Name")?.Value == "TailscaleToggle");
    }

    [Fact]
    public void ReviewStep_KeepsSecuritySensitiveChoicesAndWarningsOutsideTechnicalDetails()
    {
        var doc = LoadXaml();
        var details = Named(doc, "ReviewDetailsExpander");

        foreach (var sensitive in new[]
                 {
                     "ReviewExposureText",
                     "ReviewInstallerWarning",
                     "LocalAiNetworkingConsentPanel",
                     "LocalAiUnavailablePanel",
                     "TailscaleTrustAuthToggle",
                     "ReviewPermissionsText",
                 })
        {
            Assert.DoesNotContain(details.DescendantsAndSelf(), e => e.Attribute(XamlNs + "Name")?.Value == sensitive);
            Named(doc, sensitive);
        }

        var source = LoadSource();
        var method = ExtractMethod(source, "private void UpdateReviewSummaryText");
        Assert.Contains("SetupGatewayExposure.LocalNetwork", method);
        Assert.Contains("SetupGatewayExposure.Tailnet", method);
        Assert.Contains("SetupInstallerTrust.Custom", method);
        Assert.Contains("SetupInstallerTrust.InsecureUrl", method);
        Assert.Contains("ReviewInstallerWarning.Visibility", method);
    }

    [Fact]
    public void ReviewStep_ChangeLinksReturnToTheScreenThatOwnsEachChoice()
    {
        var doc = LoadXaml();
        Assert.Equal("ChangeWhere_Click", Named(doc, "ChangeWhereLink").Attribute("Click")?.Value);
        Assert.Equal("ChangeCapabilities_Click", Named(doc, "ChangeCapabilitiesLink").Attribute("Click")?.Value);

        var source = LoadSource();
        // Welcome rebuilds this page from the config, so the current choices are saved first and
        // no longer treated as the bundled all-on placeholder that would reset them to Standard.
        AssertInOrder(
            ExtractMethod(source, "private void ChangeWhere_Click"),
            "WriteCapabilities();",
            "_config!.UsesBundledDefaultConfig = false;",
            "SetupWindow.Active?.NavigateToWelcome(back: true);");
        AssertInOrder(
            ExtractMethod(source, "private void ChangeCapabilities_Click"),
            "Transcript.Children.Clear();",
            "GoToStep(1);",
            "ProfileRadio.Focus(FocusState.Programmatic);");

        // Local AI recovery installs nothing else and keeps the existing capabilities and
        // connections, so it only reviews Local AI; the full-install technical details are hidden.
        var navigatedTo = ExtractMethod(source, "protected override void OnNavigatedTo");
        AssertInOrder(
            navigatedTo,
            "_localAiRecoveryOnly = args?.StartAtLocalAiReview == true;",
            "ReviewWhereGroup.Visibility",
            "ReviewCapabilitiesGroup.Visibility",
            "ReviewConnectionsGroup.Visibility",
            "ReviewDetailsExpander.Visibility");
        Assert.Contains("TailscaleAuthKeyBox.Password = _config.Tailscale.AuthKey ?? string.Empty;", navigatedTo);
    }

    [Fact]
    public void ReviewStep_SummariesStayTrueForSkippedWizardAndFailedPermissionReads()
    {
        var source = LoadSource();

        var summary = ExtractMethod(source, "private void UpdateReviewSummaryText");
        AssertInOrder(summary, "_skipWizardWithoutLocalAi", "\"Onboarding_Review_AiSkipped\"", "\"Onboarding_Review_AiLater\"");
        AssertInOrder(ExtractMethod(source, "private async Task BuildPermissionRows"), "catch (Exception ex)", "UpdateReviewChoicesText();");

        var english = Resources("en-us");
        Assert.DoesNotContain("before downloading", english["Onboarding_Review_InstallerInsecure"]);
        Assert.StartsWith("Devices on your Tailscale network", english["Onboarding_Review_Exposure_Tailnet"]);
        Assert.Contains("access rules", english["Onboarding_Review_TailscaleDescription.Text"]);
        Assert.Equal("Onboarding_Review_TailscaleToggle", Named(LoadXaml(), "TailscaleToggle").Attribute(SetupTextUid)?.Value);
    }

    /// <summary>
    /// A non-HTTPS installer only fails at the CLI install step, after steps that can replace
    /// the distro and download gigabytes, so the review blocks installing instead of warning.
    /// Local AI recovery installs no CLI and is not blocked.
    /// </summary>
    [Fact]
    public void ReviewStep_BlocksInstallWhenTheInstallerUrlIsNotHttps()
    {
        var source = LoadSource();

        AssertInOrder(
            ExtractMethod(source, "private void UpdateReviewSummaryText"),
            "ReviewInstallerWarning.Severity = summary.InstallerTrust == SetupInstallerTrust.InsecureUrl",
            "? InfoBarSeverity.Error",
            ": InfoBarSeverity.Warning;",
            "_installerUrlInsecure = !_localAiRecoveryOnly && summary.InstallerTrust == SetupInstallerTrust.InsecureUrl;",
            "UpdatePrimaryButtonState();");
        // The warning never opens or closes, so a live region would never be announced.
        Assert.Null(Named(LoadXaml(), "ReviewInstallerWarning").Attribute("AutomationProperties.LiveSetting"));
        AssertInOrder(
            ExtractMethod(source, "private void UpdatePrimaryButtonState"),
            "PrimaryButton.IsEnabled =",
            "if (_step == 3 && _installerUrlInsecure)",
            "PrimaryButton.IsEnabled = false;");
        Assert.EndsWith("Fix the address before you install.", Resources("en-us")["Onboarding_Review_InstallerInsecure"]);
    }

    [Fact]
    public void ReviewStep_KeepsInstallAndSetUpAsTheExplicitInstallAction()
    {
        var source = LoadSource();
        var goToStep = ExtractMethod(source, "private void GoToStep");

        Assert.Contains("PrimaryButton.Content = step == 3", goToStep);
        Assert.Contains("SetupLocalization.GetString(\"Onboarding_Review_InstallButton\")", goToStep);
        Assert.Contains("Transcript.Visibility = step == 3 ? Visibility.Collapsed : Visibility.Visible;", goToStep);
        Assert.Equal("Install & set up", Resources("en-us")["Onboarding_Review_InstallButton"]);
        AssertInOrder(
            ExtractMethod(source, "private async Task PrimaryClickAsync"),
            "default:",
            "WriteCapabilities();",
            "SetupWindow.Active?.NavigateToProgress();");
    }

    /// <summary>
    /// The review hides the transcript, so the whole scroller is the review card. Scrolling to
    /// the bottom would open the review past its first group; the active card's top must stay
    /// in view instead.
    /// </summary>
    [Fact]
    public void ReviewStep_OpensAtTheTopOfTheActiveCard()
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
    public void ReviewStep_ContentColumnShrinksForNarrowWindows()
    {
        var scroller = Named(LoadXaml(), "Scroller");

        Assert.Null(scroller.Attribute("Width"));
        Assert.Equal("600", scroller.Attribute("MaxWidth")?.Value);
    }

    [Fact]
    public void ReviewCopy_IsLocalizedInEverySupportedLocale()
    {
        var doc = LoadXaml();
        var reviewElements = doc.Descendants()
            .Where(e => e.Attribute(SetupTextUid)?.Value.StartsWith("Onboarding_Review_", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(reviewElements);

        var expectedKeys = reviewElements
            .SelectMany(e => e.Attributes()
                .Where(a => a.Name.Namespace == XNamespace.None && LocalizableAttributes.Contains(a.Name.LocalName))
                .Select(a => $"{e.Attribute(SetupTextUid)!.Value}.{a.Name.LocalName}"))
            .Concat(Regex.Matches(LoadSource(), "SetupLocalization\\.(?:GetString|Format)\\(\\s*\"(Onboarding_Review_[A-Za-z_]+)\"")
                .Select(m => m.Groups[1].Value))
            .Concat(new[] { "ChangeWhere", "ChangeCapabilities", "TailscaleToggle" }.Select(uid => $"Onboarding_Review_{uid}{AutomationNameSuffix}"))
            .Distinct()
            .ToList();

        foreach (var locale in Locales)
        {
            var resources = Resources(locale);
            foreach (var key in expectedKeys)
                Assert.True(resources.ContainsKey(key), $"{locale} is missing {key}.");
        }

        var english = Resources("en-us");
        foreach (var locale in Locales.Where(l => l != "en-us"))
        {
            var resources = Resources(locale);
            foreach (var (key, value) in english)
            {
                Assert.NotEqual(value, resources[key]);
                Assert.Equal(Placeholders(value), Placeholders(resources[key]));
                Assert.DoesNotContain("\u2014", resources[key]);
            }
        }
    }

    [Fact]
    public void ReviewCopy_AvoidsImplementationTermsOutsideTechnicalDetails()
    {
        var terms = new Regex(@"\b(WSL|gateway|node|MXC|npm|loopback|Ubuntu|HTTPS/WSS)\b", RegexOptions.IgnoreCase);
        foreach (var (key, value) in Resources("en-us"))
        {
            Assert.DoesNotContain("\u2014", value);
            if (key.StartsWith("Onboarding_Review_DetailsExpander", StringComparison.Ordinal) ||
                key.StartsWith("Onboarding_Review_CommandsLabel", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotMatch(terms, value);
        }
    }

    private static IEnumerable<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d\}").Select(m => m.Value).Order();

    private static Dictionary<string, string> Resources(string locale)
    {
        var path = Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw");
        return XDocument.Load(path).Descendants("data")
            .Where(e => e.Attribute("name")!.Value.StartsWith("Onboarding_Review_", StringComparison.Ordinal))
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value, StringComparer.Ordinal);
    }

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
