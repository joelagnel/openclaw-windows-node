using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Setup pages live in the OpenClaw.SetupEngine.UI library, whose resource map holds no
/// strings, so an x:Uid there never reaches the Tray app's Resources.resw entries and the
/// page silently keeps its inline English text. Setup XAML uses SetupText.Uid instead, which
/// applies the same "Key.Property" entries through SetupLocalization.
/// </summary>
public sealed class SetupTextLocalizationTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName SetupTextUid = XNamespace.Get("using:OpenClaw.SetupEngine.UI") + "SetupText.Uid";
    private const string AutomationName = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    // Element type -> properties SetupText applies to it (AutomationProperties.Name applies to all).
    // Keep in sync with the switch in SetupText.ApplyOnFirstLoad; add a type here when setup XAML
    // first puts a SetupText.Uid on it.
    private static readonly Dictionary<string, string[]> AppliedProperties = new(StringComparer.Ordinal)
    {
        ["TextBlock"] = ["Text"],
        ["InfoBar"] = ["Title", "Message"],
        ["Expander"] = ["Header"],
        ["Button"] = ["Content"],
        ["HyperlinkButton"] = ["Content"],
    };

    [Fact]
    public void SetupXaml_DoesNotUseXUid()
    {
        var offenders = SetupXamlElements()
            .Where(item => item.Element.Attribute(XamlNs + "Uid") is not null)
            .Select(item => item.Location)
            .ToList();

        Assert.True(offenders.Count == 0,
            "x:Uid does not resolve in OpenClaw.SetupEngine.UI; use setup:SetupText.Uid. Found: " +
            string.Join("; ", offenders));
    }

    /// <summary>
    /// Every SetupText.Uid has en-us resources for the properties SetupText applies to that
    /// element, and inline defaults match them so the XAML reads like what users see.
    /// </summary>
    [Fact]
    public void SetupTextUids_HaveMatchingEnUsResourcesThatSetupTextApplies()
    {
        var resources = LoadEnUsResources();
        var problems = new List<string>();
        var uids = SetupXamlElements().Where(item => item.Element.Attribute(SetupTextUid) is not null).ToList();

        Assert.NotEmpty(uids);
        foreach (var (element, location) in uids)
        {
            var uid = element.Attribute(SetupTextUid)!.Value;
            var type = element.Name.LocalName;
            // MakePri splits resource names on dots, so a dotted Uid never resolves at runtime.
            if (!Regex.IsMatch(uid, "^[A-Za-z0-9_]+$"))
            {
                problems.Add($"{location} SetupText.Uid '{uid}' must be letters, digits, and underscores");
                continue;
            }

            if (!AppliedProperties.TryGetValue(type, out var properties))
            {
                problems.Add($"{location} <{type}> is not in AppliedProperties; add it (and a case in SetupText if it is not a ContentControl)");
                continue;
            }

            var keys = resources.Keys.Where(key => key.StartsWith(uid + ".", StringComparison.Ordinal)).ToList();
            if (keys.Count == 0)
                problems.Add($"{location} has no resources for SetupText.Uid '{uid}'");
            problems.AddRange(keys
                .Where(key => !properties.Append(AutomationName).Contains(key[(uid.Length + 1)..]))
                .Select(key => $"{location} <{type}> ignores {key}"));

            var inlineAttributes = properties
                .Select(property => (Attribute: property, Key: $"{uid}.{property}"))
                .Append(("AutomationProperties.Name", $"{uid}.{AutomationName}"));
            foreach (var (attribute, key) in inlineAttributes)
            {
                var inline = element.Attribute(attribute)?.Value;
                if (string.IsNullOrWhiteSpace(inline) || inline.StartsWith('{'))
                    continue;
                if (!resources.TryGetValue(key, out var english))
                    problems.Add($"{location} missing {key}");
                else if (english != inline)
                    problems.Add($"{location} inline {attribute}=\"{inline}\" differs from en-us \"{english}\"");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// SetupText applies resources when an element first loads, after the constructor and
    /// OnNavigatedTo have run, so a value code-behind sets there for a property that has a
    /// resource would be silently replaced. Code must own such a property entirely instead.
    /// </summary>
    [Fact]
    public void SetupCodeBehind_DoesNotSetPropertiesSetupTextApplies()
    {
        var resources = LoadEnUsResources();
        var problems = new List<string>();
        foreach (var (element, location) in SetupXamlElements())
        {
            var uid = element.Attribute(SetupTextUid)?.Value;
            var name = element.Attribute(XamlNs + "Name")?.Value;
            if (uid is null || name is null || !AppliedProperties.TryGetValue(element.Name.LocalName, out var properties))
                continue;

            var codeBehindPath = Path.Combine(
                TestRepositoryPaths.GetRepositoryRoot(), location[..location.LastIndexOf(':')] + ".cs");
            if (!File.Exists(codeBehindPath))
                continue;

            var codeBehind = File.ReadAllText(codeBehindPath);
            var setters = properties
                .Where(property => resources.ContainsKey($"{uid}.{property}"))
                .Select(property => (Key: $"{uid}.{property}", Pattern: $@"\b{name}\.{property}\s*=(?!=)"));
            if (resources.ContainsKey($"{uid}.{AutomationName}"))
                setters = setters.Append(($"{uid}.{AutomationName}", $@"AutomationProperties\.SetName\(\s*{name}\s*,"));
            problems.AddRange(setters
                .Where(setter => Regex.IsMatch(codeBehind, setter.Pattern))
                .Select(setter => $"{location} code-behind sets {name}, which SetupText overwrites from {setter.Key}"));
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// The runtime half of the contract: SetupText must apply every property the tests above
    /// allow, through the "Key/Property" path the PRI index uses. A missing case or a wrong path
    /// would silently bring back the inline English these tests exist to prevent.
    /// </summary>
    [Fact]
    public void SetupText_AppliesEveryPropertyTheTestsAllow()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "SetupText.cs")).Replace("\r\n", "\n");

        Assert.Contains("\"[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name\"", source);
        Assert.Contains("SetupLocalization.TryGetString($\"{uid}/{property}\")", source);
        Assert.Contains("Apply(uid, AutomationNameProperty, value => AutomationProperties.SetName(element, value));", source);
        foreach (var (type, properties) in AppliedProperties.Where(entry => entry.Value.Length > 0))
        {
            // Types without their own case get Content through the ContentControl case.
            var handler = source.Contains($"case {type} ", StringComparison.Ordinal) ? type : "ContentControl";
            var start = source.IndexOf($"case {handler} ", StringComparison.Ordinal);
            Assert.True(start >= 0, $"SetupText has no case for <{type}>");
            var body = source[start..source.IndexOf("break;", start, StringComparison.Ordinal)];
            foreach (var property in properties)
                Assert.Contains($"Apply(uid, \"{property}\"", body);
        }
    }

    private static IEnumerable<(XElement Element, string Location)> SetupXamlElements()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        foreach (var path in Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
                     .Where(LocalizationValidationTests.IsSourceXaml)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path);
            foreach (var element in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants())
            {
                var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0;
                yield return (element, $"{relative}:{line}");
            }
        }
    }

    private static Dictionary<string, string> LoadEnUsResources() =>
        LocalizationValidationTests.LoadResw(Path.Combine(
            LocalizationValidationTests.GetStringsDirectory(), "en-us", "Resources.resw"));
}
