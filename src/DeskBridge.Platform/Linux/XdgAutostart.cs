
namespace DeskBridge.Platform.Linux;

/// <summary>~/.config/autostart/deskbridge.desktop, which KDE, GNOME and others start at login.</summary>
public sealed class XdgAutostart : IAutostart
{
    private static string File
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "deskbridge.desktop");
        }
    }

    public bool IsEnabled(string executable) =>
        System.IO.File.Exists(File) && System.IO.File.ReadAllText(File).Contains(executable, StringComparison.Ordinal);

    public void Set(string executable, bool enabled)
    {
        if (!enabled)
        {
            System.IO.File.Delete(File);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, Entry(executable));
    }

    /// <summary>The .desktop entry (public for tests).</summary>
    public static string Entry(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name=DeskBridge
        Comment=Share and reassign displays between computers
        Exec="{executable.Replace("\"", "\\\"")}" --minimized
        Terminal=false
        X-GNOME-Autostart-enabled=true
        X-KDE-autostart-after=panel

        """;
}
