using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DeskBridge.Gui;

/// <summary>
/// DeskBridge as administrator. Windows does not let a normal program send keys or clicks to one running as
/// administrator, so on a machine controlled from the coordinator, apps opened as administrator would not
/// respond. A scheduled task started at sign-in with the highest privileges runs DeskBridge elevated without a
/// UAC prompt every time; creating or deleting it asks for permission once, on this PC.
/// </summary>
public static class Elevation
{
    public const string TaskName = "DeskBridge";

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool TaskExists() => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"", elevated: false, wait: true) == 0;

    /// <summary>
    /// Creates the sign-in task and starts DeskBridge through it once this copy has closed (the caller closes it
    /// right after). False when the user said no to Windows' prompt or the task could not be created.
    /// </summary>
    public static bool Enable(string exe, Action<string> log)
    {
        string xml = Path.Combine(Path.GetTempPath(), "deskbridge-task.xml");
        File.WriteAllText(xml, TaskXml(exe), Encoding.Unicode);
        // One elevated command: create, wait until this copy has really closed (a new one started earlier
        // would find it still running and give way to it), start the task.
        string command = $"/c schtasks /Create /TN \"{TaskName}\" /XML \"{xml}\" /F && {WaitForThisCopy()} && schtasks /Run /TN \"{TaskName}\"";
        return Run("cmd.exe", command, elevated: true, wait: false, log) == 0;
    }

    /// <summary>
    /// Deletes the task and starts DeskBridge again without administrator rights (through Explorer, which runs
    /// it as a normal program) once this copy has closed.
    /// </summary>
    public static bool Disable(string exe, Action<string> log)
    {
        string command = $"/c schtasks /Delete /TN \"{TaskName}\" /F && {WaitForThisCopy()} && explorer.exe \"{exe}\"";
        return Run("cmd.exe", command, elevated: true, wait: false, log) == 0;
    }

    /// <summary>
    /// Starts DeskBridge through the sign-in task (as administrator, with no prompt: the task was allowed once) once
    /// this copy has closed: for a copy opened by hand while "Run as administrator" is on.
    /// </summary>
    public static bool HandOverToTask() =>
        Run("cmd.exe", $"/c {WaitForThisCopy()} && schtasks /Run /TN \"{TaskName}\"", elevated: false, wait: false) == 0;

    /// <summary>Waits (up to a minute) until this process has ended, then a second more.</summary>
    private static string WaitForThisCopy() =>
        $"powershell.exe -NoProfile -NonInteractive -Command \"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep 1; exit 0\"";

    /// <summary>
    /// At sign-in for this user, with the highest privileges, never stopped (no 3-day limit), on battery too,
    /// a single copy.
    /// </summary>
    private static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string command = SecurityElement.Escape(exe);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DeskBridge, started as administrator at sign-in (so the keyboard and mouse shared from another computer also work in apps opened as administrator).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <returns>The exit code when waited for; 0 when started without waiting; -1 when it could not start (or the user said no).</returns>
    private static int Run(string file, string arguments, bool elevated, bool wait, Action<string>? log = null)
    {
        try
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = elevated,
                Verb = elevated && !IsElevated ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(start);
            if (process is null)
                return -1;
            if (!wait)
                return 0;
            process.WaitForExit(30_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Win32Exception ex)
        {
            // 1223: the user said no to Windows' prompt.
            log?.Invoke(ex.NativeErrorCode == 1223 ? "[admin] Windows' permission was not given" : $"[admin] {ex.Message}");
            return -1;
        }
    }
}
