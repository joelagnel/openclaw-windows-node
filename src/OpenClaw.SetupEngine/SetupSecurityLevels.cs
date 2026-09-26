namespace OpenClaw.SetupEngine;

/// <summary>
/// Setup's security levels, from most to least restrictive. A level only chooses which node
/// capabilities are registered; command approvals and command sandbox settings keep their
/// defaults at every level and stay adjustable in Settings.
/// </summary>
public enum SetupSecurityLevel
{
    /// <summary>See the screen and show content on a canvas. No command execution.</summary>
    LookOnly,

    /// <summary>Recommended: Look only plus commands and voice, without camera, location, or browser control.</summary>
    Balanced,

    /// <summary>Every selectable capability.</summary>
    FullAccess,
}

/// <summary>
/// The capability mapping behind <see cref="SetupSecurityLevel"/>. Capability names are the
/// <see cref="CapabilitiesConfig"/> switch names. Device info/status is not selectable: it is
/// always on while Node Mode is enabled.
/// </summary>
public static class SetupSecurityLevels
{
    /// <summary>Every selectable capability, in display order.</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
        Array.AsReadOnly(new[] { "System", "Canvas", "Screen", "Camera", "Location", "Browser", "Tts", "Stt" });

    private static readonly IReadOnlyList<string> LookOnly = Array.AsReadOnly(new[] { "Canvas", "Screen" });
    private static readonly IReadOnlyList<string> Balanced = Array.AsReadOnly(new[] { "System", "Canvas", "Screen", "Tts", "Stt" });

    public static IReadOnlyList<string> EnabledCapabilities(SetupSecurityLevel level) => level switch
    {
        SetupSecurityLevel.LookOnly => LookOnly,
        SetupSecurityLevel.Balanced => Balanced,
        SetupSecurityLevel.FullAccess => Capabilities,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown setup security level."),
    };

    /// <summary>The level whose capabilities are exactly <paramref name="enabled"/>, or <c>null</c> for a custom set.</summary>
    public static SetupSecurityLevel? Match(IEnumerable<string> enabled)
    {
        var set = enabled.ToHashSet(StringComparer.Ordinal);
        foreach (var level in Enum.GetValues<SetupSecurityLevel>())
        {
            if (set.SetEquals(EnabledCapabilities(level)))
                return level;
        }

        return null;
    }

    /// <summary>
    /// The level to show for <paramref name="enabled"/>, or <c>null</c> for a custom set. When
    /// <paramref name="allOnIsPlaceholder"/> is set, every capability on is the bundled config's
    /// placeholder rather than a Full access choice, so it shows as Balanced (recommended).
    /// </summary>
    public static SetupSecurityLevel? Detect(IEnumerable<string> enabled, bool allOnIsPlaceholder)
    {
        var level = Match(enabled);
        return level == SetupSecurityLevel.FullAccess && allOnIsPlaceholder ? SetupSecurityLevel.Balanced : level;
    }
}
