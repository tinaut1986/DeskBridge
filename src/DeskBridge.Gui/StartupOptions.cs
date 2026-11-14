using DeskBridge.Core.Configuration;

namespace DeskBridge.Gui;

/// <param name="SnapshotDir">Development aid: render every tab to PNG files in this folder and exit.</param>
/// <param name="Profile">Profile selected at start (defaults to the first one).</param>
/// <param name="Language">Overrides the language setting for this run (development aid).</param>
public sealed record StartupOptions(
    string ConfigPath,
    string LocalPath,
    string SettingsPath,
    bool StartAgent,
    string? SnapshotDir,
    string? Profile = null,
    string? Language = null,
    ThemeChoice? Theme = null,
    bool Minimized = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var snapshot = Value("--snapshot");
        return new StartupOptions(
            Value("--config") ?? AppPaths.Config,
            Value("--local") ?? AppPaths.Local,
            Value("--settings") ?? UserSettings.DefaultPath,
            StartAgent: !args.Contains("--no-agent") && snapshot is null,
            snapshot,
            Value("--profile"),
            Value("--language"),
            Enum.TryParse<ThemeChoice>(Value("--theme"), ignoreCase: true, out var theme) ? theme : null,
            args.Contains("--minimized"));
    }
}
