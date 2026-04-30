using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record OperatorAppearanceSettings
{
    public const string DefaultTheme = "amber-dark";
    public const string DefaultAccent = "amber";
    public const string DefaultDensity = "comfortable";
    public const string DefaultBodyFont = "sans";
    public const string DefaultRailMode = "expanded";
    public const string DefaultConsoleMode = "preview";
    public const string DefaultActiveTab = "operator";

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = DefaultTheme;

    [JsonPropertyName("accent")]
    public string Accent { get; init; } = DefaultAccent;

    [JsonPropertyName("density")]
    public string Density { get; init; } = DefaultDensity;

    [JsonPropertyName("bodyFont")]
    public string BodyFont { get; init; } = DefaultBodyFont;

    [JsonPropertyName("railMode")]
    public string RailMode { get; init; } = DefaultRailMode;

    [JsonPropertyName("consoleMode")]
    public string ConsoleMode { get; init; } = DefaultConsoleMode;

    [JsonPropertyName("activeTab")]
    public string ActiveTab { get; init; } = DefaultActiveTab;

    public static OperatorAppearanceSettings CreateDefault()
    {
        return new OperatorAppearanceSettings();
    }

    public OperatorAppearanceSettings Normalized()
    {
        return this with
        {
            Theme = NormalizeChoice(Theme, KnownThemes, DefaultTheme),
            Accent = NormalizeChoice(Accent, KnownAccents, DefaultAccent),
            Density = NormalizeChoice(Density, KnownDensities, DefaultDensity),
            BodyFont = NormalizeChoice(BodyFont, KnownBodyFonts, DefaultBodyFont),
            RailMode = NormalizeChoice(RailMode, KnownRailModes, DefaultRailMode),
            ConsoleMode = NormalizeChoice(ConsoleMode, KnownConsoleModes, DefaultConsoleMode),
            ActiveTab = NormalizeChoice(ActiveTab, KnownActiveTabs, DefaultActiveTab),
        };
    }

    private static readonly string[] KnownThemes = ["amber-dark", "graphite-dark"];
    private static readonly string[] KnownAccents = ["amber", "cyan", "green", "violet"];
    private static readonly string[] KnownDensities = ["compact", "comfortable", "spacious"];
    private static readonly string[] KnownBodyFonts = ["sans", "mono"];
    private static readonly string[] KnownRailModes = ["expanded", "collapsed", "hidden"];
    private static readonly string[] KnownConsoleModes = ["collapsed", "preview", "half", "full"];
    private static readonly string[] KnownActiveTabs = ["operator", "tasks", "git", "compare", "terminals", "collaboration", "settings"];

    private static string NormalizeChoice(string value, string[] knownValues, string fallback)
    {
        return Array.IndexOf(knownValues, value) >= 0 ? value : fallback;
    }
}

public sealed record SaveOperatorAppearanceSettingsRequest
{
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("accent")]
    public string? Accent { get; init; }

    [JsonPropertyName("density")]
    public string? Density { get; init; }

    [JsonPropertyName("bodyFont")]
    public string? BodyFont { get; init; }

    [JsonPropertyName("railMode")]
    public string? RailMode { get; init; }

    [JsonPropertyName("consoleMode")]
    public string? ConsoleMode { get; init; }

    [JsonPropertyName("activeTab")]
    public string? ActiveTab { get; init; }

    public OperatorAppearanceSettings MergeInto(OperatorAppearanceSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return new OperatorAppearanceSettings
        {
            Theme = Theme ?? current.Theme,
            Accent = Accent ?? current.Accent,
            Density = Density ?? current.Density,
            BodyFont = BodyFont ?? current.BodyFont,
            RailMode = RailMode ?? current.RailMode,
            ConsoleMode = ConsoleMode ?? current.ConsoleMode,
            ActiveTab = ActiveTab ?? current.ActiveTab,
        }.Normalized();
    }
}
