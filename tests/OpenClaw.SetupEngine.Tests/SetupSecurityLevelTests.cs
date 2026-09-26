namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Pins the policy mapping behind setup's security levels. A level only chooses which node
/// capabilities are registered, so these tests pin that choice down to the command ids the
/// gateway receives: Look only never registers command execution, and Balanced never registers
/// the camera, location, or browser control.
/// </summary>
public sealed class SetupSecurityLevelTests
{
    [Fact]
    public void Capabilities_AreTheSelectableCapabilitiesConfigSwitches_WithoutDevice()
    {
        var switches = typeof(CapabilitiesConfig).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.Name != nameof(CapabilitiesConfig.Device))
            .Select(p => p.Name);

        Assert.Equal(switches.Order(), SetupSecurityLevels.Capabilities.Order());
    }

    [Theory]
    [InlineData(SetupSecurityLevel.LookOnly, new[] { "Canvas", "Screen" })]
    [InlineData(SetupSecurityLevel.Balanced, new[] { "System", "Canvas", "Screen", "Tts", "Stt" })]
    [InlineData(SetupSecurityLevel.FullAccess, new[] { "System", "Canvas", "Screen", "Camera", "Location", "Browser", "Tts", "Stt" })]
    public void EnabledCapabilities_MatchTheDocumentedMapping(SetupSecurityLevel level, string[] expected)
    {
        Assert.Equal(expected, SetupSecurityLevels.EnabledCapabilities(level));
    }

    [Fact]
    public void LookOnly_RegistersNoCommandExecution()
    {
        var commands = CommandsFor(SetupSecurityLevel.LookOnly);

        Assert.DoesNotContain("system.run", commands);
        Assert.DoesNotContain("camera.snap", commands);
        Assert.DoesNotContain("location.get", commands);
        Assert.DoesNotContain("browser.proxy", commands);
        Assert.Contains("screen.snapshot", commands);
    }

    [Fact]
    public void Balanced_RunsCommands_ButNeverRegistersCameraLocationOrBrowser()
    {
        var commands = CommandsFor(SetupSecurityLevel.Balanced);

        Assert.Contains("system.run", commands);
        Assert.Contains("stt.transcribe", commands);
        Assert.DoesNotContain(commands, c => c.StartsWith("camera.", StringComparison.Ordinal));
        Assert.DoesNotContain("location.get", commands);
        Assert.DoesNotContain("browser.proxy", commands);
    }

    [Fact]
    public void FullAccess_RegistersEveryCapability()
    {
        var commands = CommandsFor(SetupSecurityLevel.FullAccess);

        Assert.Equal(new CapabilitiesConfig().GetEnabledCommandIds(), commands);
    }

    [Theory]
    [InlineData(SetupSecurityLevel.LookOnly)]
    [InlineData(SetupSecurityLevel.Balanced)]
    [InlineData(SetupSecurityLevel.FullAccess)]
    public void Match_RoundTripsEveryLevel_RegardlessOfOrder(SetupSecurityLevel level)
    {
        Assert.Equal(level, SetupSecurityLevels.Match(SetupSecurityLevels.EnabledCapabilities(level).Reverse()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Screen")]
    [InlineData("System,Canvas,Screen,Tts,Stt,Camera")]
    public void Match_ReturnsNullForCustomSets(string enabled)
    {
        Assert.Null(SetupSecurityLevels.Match(enabled.Split(',', StringSplitOptions.RemoveEmptyEntries)));
    }

    // Configs saved under the earlier Read-only and Standard profiles keep their meaning.
    [Theory]
    [InlineData("Canvas,Screen", SetupSecurityLevel.LookOnly)]
    [InlineData("System,Canvas,Screen,Tts,Stt", SetupSecurityLevel.Balanced)]
    public void Detect_MapsConfigsFromTheEarlierProfilesToTheirLevels(string enabled, SetupSecurityLevel expected)
    {
        foreach (var allOnIsPlaceholder in new[] { false, true })
            Assert.Equal(expected, SetupSecurityLevels.Detect(enabled.Split(','), allOnIsPlaceholder));
    }

    [Fact]
    public void Detect_ShowsTheBundledAllOnPlaceholderAsBalanced_ButAnExplicitAllOnAsFullAccess()
    {
        Assert.Equal(SetupSecurityLevel.Balanced, SetupSecurityLevels.Detect(SetupSecurityLevels.Capabilities, allOnIsPlaceholder: true));
        Assert.Equal(SetupSecurityLevel.FullAccess, SetupSecurityLevels.Detect(SetupSecurityLevels.Capabilities, allOnIsPlaceholder: false));
    }

    [Fact]
    public void Detect_KeepsCustomSetsCustom_EvenForTheBundledConfig()
    {
        Assert.Null(SetupSecurityLevels.Detect(["System", "Camera"], allOnIsPlaceholder: true));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void EnabledCapabilities_RejectsUnknownLevels_InsteadOfGrantingEverything(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupSecurityLevels.EnabledCapabilities((SetupSecurityLevel)level));
    }

    [Fact]
    public void CapabilityLists_CannotBeModifiedByCallers()
    {
        foreach (var list in Enum.GetValues<SetupSecurityLevel>().Select(SetupSecurityLevels.EnabledCapabilities).Append(SetupSecurityLevels.Capabilities))
            Assert.Throws<NotSupportedException>(() => ((IList<string>)list)[0] = "Camera");
    }

    private static IReadOnlyList<string> CommandsFor(SetupSecurityLevel level)
    {
        var enabled = SetupSecurityLevels.EnabledCapabilities(level);
        var config = new CapabilitiesConfig();
        foreach (var name in SetupSecurityLevels.Capabilities)
            typeof(CapabilitiesConfig).GetProperty(name)!.SetValue(config, enabled.Contains(name));
        config.Device = true;
        return config.GetEnabledCommandIds();
    }
}
