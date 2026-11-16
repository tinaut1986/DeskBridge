using System.Diagnostics;
using System.IO;
using System.Windows;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Updates;
using DeskBridge.Gui.Updates;
using Microsoft.Win32;

namespace DeskBridge.Gui;

/// <summary>
/// DeskBridge installed for this user, without an installer and without administrator rights: the program in
/// %LOCALAPPDATA%\DeskBridge\bin (where updates from the coordinator also go, so the firewall's permission and
/// the sign-in task keep pointing at it), a Start menu shortcut, and an entry in Settings, Apps with an
/// uninstaller. A published build started from anywhere else (Downloads) copies itself there first.
/// </summary>
public static class Installation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskBridge";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "bin");

    public static string Exe => Path.Combine(Folder, "DeskBridge.exe");

    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeskBridge.lnk");

    public static bool IsInstalledCopy =>
        Environment.ProcessPath is { } exe && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// At start, before anything else. A published build outside the install folder copies itself there and
    /// starts that copy (true: this one must end). Development builds run wherever they are.
    /// </summary>
    public static bool MoveIntoPlace(string[] args)
    {
        if (!AppInfo.IsReleaseBuild || IsInstalledCopy || Environment.ProcessPath is not { } exe)
            return false;
        try
        {
            Directory.CreateDirectory(Folder);
            // An installed copy that is newer stays (this is an older download).
            if (File.Exists(Exe) && FileVersionOf(Exe) is { } installed && installed.CompareTo(AppInfo.Version) > 0)
            {
                Process.Start(new ProcessStartInfo(Exe, string.Join(' ', args.Select(Quote))) { UseShellExecute = false });
                return true;
            }
            File.Copy(exe, Exe, overwrite: true);
            Process.Start(new ProcessStartInfo(Exe, string.Join(' ', args.Select(Quote))) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false; // the installed one is running, or cannot be written: this copy just runs
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static SemVersion? FileVersionOf(string path) =>
        SemVersion.TryParse(FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0]);

    /// <summary>At every start of the installed copy: the shortcut and the Apps entry, with this version.</summary>
    public static void Register()
    {
        if (!IsInstalledCopy)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "DeskBridge");
            key.SetValue("DisplayVersion", AppInfo.Version.ToString());
            key.SetValue("Publisher", "DeskBridge");
            key.SetValue("DisplayIcon", Exe);
            key.SetValue("InstallLocation", Folder);
            key.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
            key.SetValue("URLInfoAbout", ReleaseFeed.ProjectPage);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
        CreateShortcut();
    }

    private static void CreateShortcut()
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } type)
                return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(Shortcut);
            link.TargetPath = Exe;
            link.WorkingDirectory = Folder;
            link.IconLocation = Exe + ",0";
            link.Description = L.T("Use several computers with one keyboard and mouse, and share their displays.");
            link.Save();
        }
        catch (Exception)
        {
            // Without the shortcut the app still works; it is tried again at the next start.
        }
    }

    /// <summary>
    /// "--uninstall" (from Settings, Apps): asks, ends any running copy, removes the sign-in task, the start with
    /// Windows, the shortcut and the Apps entry, and deletes the program once this process has ended. The
    /// settings stay (a later install finds them).
    /// </summary>
    public static void Uninstall()
    {
        if (MessageBox.Show(L.T("Uninstall DeskBridge from this PC? Your settings are kept."), "DeskBridge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var other in Process.GetProcessesByName("DeskBridge").Where(p => p.Id != Environment.ProcessId))
        {
            try
            {
                other.Kill();
                other.WaitForExit(5000);
            }
            catch (Exception)
            {
                // One running as administrator: the elevated command below ends it.
            }
        }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("DeskBridge", throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            File.Delete(Shortcut);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }

        // The program folder goes once this process has ended; the sign-in task (created as administrator)
        // needs administrator rights to go, so Windows asks for permission when there is one.
        string wait = $"powershell.exe -NoProfile -NonInteractive -Command \"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep 1; exit 0\"";
        string remove = $"rmdir /s /q \"{Folder}\"";
        bool task = Elevation.TaskExists();
        string command = task
            ? $"/c taskkill /f /im DeskBridge.exe /fi \"PID ne {Environment.ProcessId}\" & schtasks /Delete /TN \"{Elevation.TaskName}\" /F & {wait} & {remove}"
            : $"/c {wait} & {remove}";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = task,
                Verb = task && !Elevation.IsElevated ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(), // not the folder being deleted
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Permission refused: the task stays (harmless without the program); the folder is removed anyway.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {wait} & {remove}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        MessageBox.Show(L.T("DeskBridge has been uninstalled."), "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
