using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskBridge.Gui;

/// <summary>
/// Light / dark mode. Standard controls follow WPF's Fluent theme (Application.ThemeMode); DeskBridge's
/// own colours are brushes in the application resources (used with DynamicResource), swapped here.
/// "System" follows the Windows apps colour setting, also when it changes while the app runs.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _listening;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the effective theme changes (code-drawn parts redraw on it).</summary>
    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_choice == ThemeChoice.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(Update);
            };
        }
        Update();
    }

    private static void Update()
    {
        bool dark = _choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemUsesDark(),
        };

#pragma warning disable WPF0001 // ThemeMode is marked experimental, but is the supported way to get Fluent light/dark
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var r = Application.Current.Resources;
        r["AppBackground"] = Brush(dark ? "#1E1F24" : "#F5F6F8");
        r["CardBackground"] = Brush(dark ? "#2A2C33" : "#FFFFFF");
        r["CardBorder"] = Brush(dark ? "#3A3D46" : "#E5E7EB");
        r["NavBackground"] = Brush(dark ? "#191A1F" : "#ECEEF2");
        r["NavHover"] = Brush(dark ? "#262830" : "#E1E4EA");
        r["NavSelected"] = Brush(dark ? "#2C2F38" : "#DADEE6");
        r["Accent"] = Brush(dark ? "#4C8DFF" : "#2563EB");
        r["HeaderBackground"] = Brush(dark ? "#26282E" : "#F3F4F6");
        r["NotesBackground"] = Brush(dark ? "#24262C" : "#F9FAFB");
        r["NormalText"] = Brush(dark ? "#E6E6E6" : "#1F2937");
        r["InfoText"] = Brush(dark ? "#C9CBD1" : "#374151");
        r["MutedText"] = Brush(dark ? "#9CA0AA" : "#6B7280");
        r["WarningText"] = Brush(dark ? "#F5B651" : "#B45309");
        r["ErrorText"] = Brush(dark ? "#FF7B7B" : "#DC2626");
        r["SelectedRow"] = Brush(dark ? "#2F4A73" : "#DBEAFE");
        r["ChipBackground"] = Brush(dark ? "#3A3D46" : "#E5E7EB");
        r["BetaChipBackground"] = Brush(dark ? "#5A4A2D" : "#FEF3C7");
        r["OkChipBackground"] = Brush(dark ? "#2D5A3A" : "#D1FAE5");
        r["BadChipBackground"] = Brush(dark ? "#5A2D2D" : "#FEE2E2");

        if (IsDark != dark || !_applied)
        {
            _applied = true;
            IsDark = dark;
            Changed?.Invoke();
        }
    }

    private static bool _applied;

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Windows' "Choose your default app mode": AppsUseLightTheme = 0 means dark.</summary>
    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
