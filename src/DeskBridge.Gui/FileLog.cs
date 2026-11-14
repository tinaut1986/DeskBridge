using System.IO;
using System.Text;

namespace DeskBridge.Gui;

/// <summary>
/// The log also goes to %LOCALAPPDATA%\DeskBridge\logs\deskbridge-YYYY-MM-DD.log (one file per day,
/// a week kept), so it can be read after the fact or copied somewhere for someone else to read.
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private static bool _cleaned;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "logs");

    public static bool Enabled { get; set; } = true;

    public static void Write(string line)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (!_cleaned)
                {
                    _cleaned = true;
                    foreach (var old in System.IO.Directory.EnumerateFiles(Directory, "deskbridge-*.log")
                                 .Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)))
                        File.Delete(old);
                }
                File.AppendAllText(Path.Combine(Directory, $"deskbridge-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // logging must never break the app
            }
        }
    }
}
