using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace DeskBridge.Gui.Secure;

/// <summary>
/// The optional Windows service that lets the coordinator see and use this PC's secure desktop (the lock screen,
/// administrator prompts), which no normal program can. The service (SYSTEM) only starts a helper in the session
/// at the console (<see cref="SecureHelper"/>); DeskBridge talks to it through a local pipe that only
/// administrators can open (<see cref="SecureLink"/>), so it gives nothing to a program that is not already
/// running as administrator.
/// <para>
/// It runs a copy of DeskBridge kept in Program Files (only administrators can change it there: a SYSTEM service
/// running a file the user can replace would let any program of the user become SYSTEM), refreshed by DeskBridge
/// itself when it runs as administrator.
/// </para>
/// </summary>
public static class SecureService
{
    public const string Name = "DeskBridgeHelper";

    /// <summary>Where the service's copy of DeskBridge lives.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeskBridge");

    public static string Exe => Path.Combine(Folder, "DeskBridge.exe");

    public static bool IsInstalled()
    {
        try
        {
            using var service = new ServiceController(Name);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var service = new ServiceController(Name);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The version of the service's copy, or null.</summary>
    public static string? InstalledVersion()
    {
        try
        {
            return File.Exists(Exe) ? FileVersionInfo.GetVersionInfo(Exe).ProductVersion?.Split('+')[0] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installs (or refreshes) the service with a copy of <paramref name="exe"/> and starts it. Needs
    /// administrator rights: run from an elevated DeskBridge, or from <c>--install-service</c> after Windows' prompt.
    /// </summary>
    public static void Install(string exe, Action<string> log)
    {
        StopAndWait();
        Directory.CreateDirectory(Folder);
        File.Copy(exe, Exe, overwrite: true);
        if (!IsInstalled())
        {
            Sc($"create {Name} binPath= \"\\\"{Exe}\\\" --service\" start= auto DisplayName= \"DeskBridge Helper\"", log);
            Sc($"description {Name} \"Lets DeskBridge show and use the lock screen and administrator prompts of this PC from the coordinator.\"", log);
            // Started again by Windows if it ever stops on its own.
            Sc($"failure {Name} reset= 60 actions= restart/5000/restart/5000/restart/30000", log);
        }
        Sc($"start {Name}", log);
        log($"[secure] service installed ({Exe})");
    }

    /// <summary>Stops and removes the service and its copy of DeskBridge (administrator rights).</summary>
    public static void Uninstall(Action<string> log)
    {
        StopAndWait();
        if (IsInstalled())
            Sc($"delete {Name}", log);
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"[secure] could not delete {Folder}: {ex.Message}");
        }
        log("[secure] service removed");
    }

    /// <summary>
    /// The service runs an older copy than <paramref name="exe"/>: refreshed (only possible as administrator; the
    /// pipe tells an outdated helper apart anyway).
    /// </summary>
    public static void RefreshIfOutdated(string exe, string version, Action<string> log)
    {
        if (!Elevation.IsElevated || !IsInstalled() || InstalledVersion() == version)
            return;
        try
        {
            Install(exe, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log($"[secure] could not update the service's copy: {ex.Message}");
        }
    }

    private static void StopAndWait()
    {
        try
        {
            using var service = new ServiceController(Name);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
        }
        // The helper it started may take a moment to go (its file is in use until then).
        foreach (var helper in Process.GetProcessesByName("DeskBridge"))
        {
            try
            {
                if (string.Equals(helper.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                    helper.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            finally
            {
                helper.Dispose();
            }
        }
    }

    private static void Sc(string arguments, Action<string> log)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            log($"[secure] sc {arguments.Split(' ')[0]}: {output.Trim()}");
    }
}
