using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeskBridge.Platform.Windows;

/// <summary>Per-user Run key: no administrator rights needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostart : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DeskBridge";

    /// <summary>Registered for this very executable (a copy elsewhere does not count).</summary>
    public bool IsEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string command && command.Contains(executable, StringComparison.OrdinalIgnoreCase);
    }

    public void Set(string executable, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{executable}\" --minimized");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
