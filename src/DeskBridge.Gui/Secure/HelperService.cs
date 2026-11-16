using System.Diagnostics;
using System.ServiceProcess;
using DeskBridge.Gui.Updates;
using DeskBridge.Platform.Windows.Secure;

namespace DeskBridge.Gui.Secure;

/// <summary>
/// The Windows service (<c>DeskBridge.exe --service</c>, SYSTEM): keeps one <see cref="SecureHelper"/> running in
/// the session at the console, started again when it ends or when another session takes the console. It does
/// nothing else: no network, no settings.
/// </summary>
public sealed class HelperService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Process? _helper;
    private uint? _helperSession;
    private Thread? _watch;

    public HelperService()
    {
        ServiceName = SecureService.Name;
        CanHandleSessionChangeEvent = true;
        CanStop = true;
    }

    private static void Log(string line) => FileLog.Write("[service] " + line);

    protected override void OnStart(string[] args)
    {
        Log($"[secure] service started (DeskBridge {AppInfo.Version})");
        _watch = new Thread(Watch) { IsBackground = true, Name = "Secure helper watch" };
        _watch.Start();
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        _watch?.Join(3000);
        StopHelper();
        Log("[secure] service stopped");
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        // Someone else at the console (fast user switching, sign-in after sign-out): the helper moves there.
        if (change.Reason is SessionChangeReason.ConsoleConnect or SessionChangeReason.SessionLogon)
            lock (_gate)
                StopHelper();
    }

    /// <summary>Every second: the helper runs in the console's session.</summary>
    private void Watch()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                lock (_gate)
                {
                    var session = SessionProcess.ConsoleSession();
                    if (session != _helperSession || _helper is null || _helper.HasExited)
                    {
                        StopHelper();
                        if (session is { } s && s != 0)
                        {
                            int id = SessionProcess.Start(s, Environment.ProcessPath!, "--secure-helper");
                            _helper = Process.GetProcessById(id);
                            _helperSession = s;
                            Log($"[secure] helper started in session {s} (process {id})");
                            failures = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Waits longer after each failure, so a helper that cannot start does not fill the log.
                failures++;
                Log($"[secure] could not start the helper: {ex.Message}");
                _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(60, 2 * failures)));
            }
            _stop.Token.WaitHandle.WaitOne(1000);
        }
    }

    private void StopHelper()
    {
        if (_helper is { } helper)
        {
            try
            {
                if (!helper.HasExited)
                    helper.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            helper.Dispose();
        }
        _helper = null;
        _helperSession = null;
    }
}
