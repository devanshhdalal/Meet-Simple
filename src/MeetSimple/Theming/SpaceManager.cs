using System.Windows;
using System.Windows.Media;
using MeetSimple.Core;

namespace MeetSimple.Theming;

/// <summary>One colour "space": a gradient backdrop for the window plus the accent colour used on buttons and highlights.</summary>
public sealed record SpaceDefinition(
    string Id,
    string Label,
    Color LightStart,
    Color LightEnd,
    Color DarkStart,
    Color DarkEnd,
    Color LightAccent,
    Color LightAccentText,
    Color LightAccentSoft,
    Color DarkAccent,
    Color DarkAccentText,
    Color DarkAccentSoft);

/// <summary>
/// Arc-style spaces: pick a colour world and the window backdrop and accent colours follow it, in both light and dark.
/// The backdrop and accent brushes are written into Application.Resources, which take priority over the light/dark palette.
/// </summary>
public static class SpaceManager
{
    public const string DefaultId = "violet";

    public static IReadOnlyList<SpaceDefinition> Spaces { get; } = new[]
    {
        Define("violet", "Violet",
            "#C9B8FF", "#F6B9E4", "#2A2150", "#4A2158",
            "#6D4AF0", "#5B3BD6", "#EEE9FF", "#7B5CF5", "#BBA8FF", "#2A2252"),
        Define("ocean", "Ocean",
            "#A8D8FF", "#A8F0E4", "#0F2A4A", "#0E4A55",
            "#0369A1", "#075985", "#E0F2FE", "#0284C7", "#7DD3FC", "#0C2D48"),
        Define("forest", "Forest",
            "#BDEBC4", "#E5F2A8", "#12352A", "#2A4318",
            "#15803D", "#166534", "#DCFCE7", "#16A34A", "#86EFAC", "#12301F"),
        Define("sunset", "Sunset",
            "#FFC9A8", "#FFA8C5", "#4A2112", "#4A1230",
            "#C2410C", "#9A3412", "#FFEDD5", "#EA580C", "#FDBA74", "#3B2113"),
        Define("graphite", "Graphite",
            "#D5D9E3", "#EEF0F6", "#1B1E26", "#2B303B",
            "#475569", "#334155", "#E2E8F0", "#64748B", "#CBD5E1", "#262C3A"),
    };

    private static SpaceDefinition currentSpace = Spaces[0];

    public static SpaceDefinition Current => currentSpace;

    /// <summary>Lets the main window cross-fade a space change. It receives the action that performs the change.</summary>
    public static Action<Action>? Transition { get; set; }

    /// <summary>Raised after the active space has changed (after any transition).</summary>
    public static event Action? SpaceChanged;

    public static bool IsKnown(string? id) => Spaces.Any(space => space.Id == id);

    /// <summary>Call once, after <see cref="ThemeManager.Initialize"/> and before any window is created.</summary>
    public static void Initialize()
    {
        currentSpace = Find(AppSettings.Current.Space);
        Apply();
        ThemeManager.ThemeChanged += Apply;
    }

    public static void SetSpace(string id)
    {
        SpaceDefinition chosen = Find(id);
        if (chosen == currentSpace) return;

        void Swap()
        {
            currentSpace = chosen;
            Apply();
        }

        if (Transition != null) Transition(Swap); else Swap();
        AppSettings.Current.Space = chosen.Id;
        AppSettings.Current.Save();
        SpaceChanged?.Invoke();
    }

    private static SpaceDefinition Find(string? id) => Spaces.FirstOrDefault(space => space.Id == id) ?? Spaces[0];

    private static void Apply()
    {
        // Application.Resources freezes Freezables on insert (.NET 8+), so mutate-by-reference
        // gradient stops cannot work. Replace the whole brush whenever space or theme changes.
        bool dark = ThemeManager.IsDark;
        var resources = Application.Current.Resources;
        resources["SpaceBackdropBrush"] = FrozenGradient(
            dark ? currentSpace.DarkStart : currentSpace.LightStart,
            dark ? currentSpace.DarkEnd : currentSpace.LightEnd);
        resources["AccentBrush"] = Frozen(dark ? currentSpace.DarkAccent : currentSpace.LightAccent);
        resources["AccentTextBrush"] = Frozen(dark ? currentSpace.DarkAccentText : currentSpace.LightAccentText);
        resources["AccentSoftBrush"] = Frozen(dark ? currentSpace.DarkAccentSoft : currentSpace.LightAccentSoft);
    }

    private static LinearGradientBrush FrozenGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(start, 0.0),
                new GradientStop(end, 1.0),
            },
            new Point(0.0, 0.0),
            new Point(1.0, 1.0));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SpaceDefinition Define(
        string id, string label,
        string lightStart, string lightEnd, string darkStart, string darkEnd,
        string lightAccent, string lightAccentText, string lightAccentSoft,
        string darkAccent, string darkAccentText, string darkAccentSoft) =>
        new(id, label,
            Hex(lightStart), Hex(lightEnd), Hex(darkStart), Hex(darkEnd),
            Hex(lightAccent), Hex(lightAccentText), Hex(lightAccentSoft),
            Hex(darkAccent), Hex(darkAccentText), Hex(darkAccentSoft));

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
}
