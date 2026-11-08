using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MeetSimple.Core;
using Microsoft.Win32;

namespace MeetSimple.Theming;

public enum ThemeChoice
{
    /// <summary>Follow the Windows light/dark setting, including when it changes while the app is open.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Switches the whole app between the light and dark palettes, live, and keeps window title bars in step.</summary>
public static class ThemeManager
{
    private const int ImmersiveDarkModeAttribute = 20;
    private const int CaptionColorAttribute = 35;
    private const int CaptionTextColorAttribute = 36;

    private static readonly ConditionalWeakTable<Window, string> CaptionBrushKeys = new();
    private static ThemeChoice currentChoice = ThemeChoice.System;
    private static bool? appliedDark;

    /// <summary>Raised after the palette has been swapped.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Lets the main window cross-fade the swap. It receives the action that performs the swap.</summary>
    public static Action<Action>? Transition { get; set; }

    public static bool IsDark { get; private set; }

    public static ThemeChoice Choice => currentChoice;

    public static ThemeChoice Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => ThemeChoice.Light,
        "dark" => ThemeChoice.Dark,
        _ => ThemeChoice.System,
    };

    public static string Serialize(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => "light",
        ThemeChoice.Dark => "dark",
        _ => "system",
    };

    /// <summary>Call once before any window is created, so the first frame already has the right colours.</summary>
    public static void Initialize()
    {
        currentChoice = Parse(AppSettings.Current.Theme);
        ApplyPalette(ResolveDark());
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public static void SetChoice(ThemeChoice choice)
    {
        currentChoice = choice;
        AppSettings.Current.Theme = Serialize(choice);
        AppSettings.Current.Save();
        Refresh();
    }

    /// <summary>Makes a window's title bar match the theme (dark title bar and matching colour).</summary>
    public static void Attach(Window window, string backgroundBrushKey)
    {
        CaptionBrushKeys.AddOrUpdate(window, backgroundBrushKey);
        window.SourceInitialized += (_, _) => ApplyCaption(window);
    }

    private static bool ResolveDark() => currentChoice switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,
        _ => SystemPrefersDark(),
    };

    private static void Refresh()
    {
        bool wantDark = ResolveDark();
        if (appliedDark == wantDark) return;

        void Swap()
        {
            ApplyPalette(wantDark);
            foreach (Window window in Application.Current.Windows)
            {
                ApplyCaption(window);
            }
            ThemeChanged?.Invoke();
        }

        if (Transition != null) Transition(Swap); else Swap();
    }

    private static void ApplyPalette(bool dark)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml", UriKind.Absolute),
        };

        bool firstIsPalette = merged.Count > 0 && merged[0].Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true;
        if (firstIsPalette) merged[0] = palette; else merged.Insert(0, palette);

        IsDark = dark;
        appliedDark = dark;
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (currentChoice != ThemeChoice.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int appsUseLight && appsUseLight == 0;
        }
        catch (Exception error) when (error is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ApplyCaption(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int useDark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, ImmersiveDarkModeAttribute, ref useDark, sizeof(int));

        // Windows 11 can also colour the title bar itself; older versions just ignore these.
        if (CaptionBrushKeys.TryGetValue(window, out string? brushKey)
            && Application.Current.TryFindResource(brushKey) is SolidColorBrush background
            && Application.Current.TryFindResource("TextBrush") is SolidColorBrush text)
        {
            int captionColor = ToColorRef(background.Color);
            int captionTextColor = ToColorRef(text.Color);
            _ = DwmSetWindowAttribute(handle, CaptionColorAttribute, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(handle, CaptionTextColorAttribute, ref captionTextColor, sizeof(int));
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int attributeValue, int attributeSize);
}
