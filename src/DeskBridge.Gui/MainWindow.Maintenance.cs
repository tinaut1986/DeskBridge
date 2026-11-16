using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui;

/// <summary>
/// Troubleshooting another machine from this one without anyone at it: the command line on the
/// coordinator relays requests to it over the session it keeps open (<see cref="RelayMessage"/>), which
/// lets it read its log, run headless video probes and, when that machine allows it, install a new build.
/// </summary>
public partial class MainWindow
{
    private sealed class IncomingUpdate(string version, long size, string sha256, string path, FileStream file)
    {
        public string Version { get; } = version;
        public long Size { get; } = size;
        public string Sha256 { get; } = sha256;
        public string Path { get; } = path;
        public FileStream File { get; } = file;
    }

    private IncomingUpdate? _incomingUpdate;
    private readonly object _updateLock = new();

    /// <summary>Where builds installed by the coordinator go: one file per version (a running one is never overwritten).</summary>
    private static string UpdatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "bin");

    /// <summary>On the agent's connection thread.</summary>
    private async Task<ControlMessage> HandleMaintenanceAsync(string from, ControlMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case UpdateStartMessage or UpdateChunkMessage or UpdateEndMessage:
            {
                bool fromCoordinator = _agent?.Reverse?.ConnectedTo == from || from == _agent?.Machine;
                if (!_settings.AcceptUpdates || !fromCoordinator)
                    return new ErrorMessage(L.T("This PC does not accept new versions from other machines. It can be enabled on it, in Connection and security."));
                return HandleUpdate(from, message);
            }

            case ProbeMessage { What: "ui" }:
                return new ReportMessage(await Dispatcher.InvokeAsync(SnapshotTabsForProbe));

            case ProbeMessage { What: "video" }:
                return new ReportMessage(Platform.Windows.Video.VideoWindow.Report());

            case ProbeMessage { What: "vdd" }:
                return new ReportMessage(await Task.Run(Platform.Windows.VirtualDisplays.MttVirtualDisplays.Report, ct));

            case VideoProbeMessage probe when from == _agent?.Machine:
                return await ProbeVideoAsync(probe.Machine, probe.Seconds, ct);

            default:
                return new ErrorMessage(L.T("Unexpected request: {request}.", ("request", message.GetType().Name)));
        }
    }

    private ControlMessage HandleUpdate(string from, ControlMessage message)
    {
        lock (_updateLock)
        {
            switch (message)
            {
                case UpdateStartMessage start:
                {
                    if (Core.Updates.SemVersion.TryParse(start.Version) is null || start.Size <= 0 || start.Size > 512L * 1024 * 1024)
                        return new ErrorMessage("Invalid update.");
                    _incomingUpdate?.File.Dispose();
                    Directory.CreateDirectory(UpdatesFolder);
                    var path = Path.Combine(UpdatesFolder, $"incoming-{Guid.NewGuid():N}.tmp");
                    _incomingUpdate = new IncomingUpdate(start.Version, start.Size, start.Sha256.ToLowerInvariant(), path,
                        new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite));
                    AddLog($"[update] receiving DeskBridge {start.Version} from {from}");
                    return new OkMessage();
                }

                case UpdateChunkMessage chunk when _incomingUpdate is { } update:
                {
                    var data = Convert.FromBase64String(chunk.DataBase64);
                    if (chunk.Offset < 0 || chunk.Offset + data.Length > update.Size)
                        return new ErrorMessage("Chunk out of range.");
                    update.File.Position = chunk.Offset;
                    update.File.Write(data);
                    return new OkMessage();
                }

                case UpdateEndMessage when _incomingUpdate is { } update:
                {
                    _incomingUpdate = null;
                    update.File.Position = 0;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(update.File));
                    long length = update.File.Length;
                    update.File.Dispose();
                    if (length != update.Size || hash != update.Sha256)
                    {
                        File.Delete(update.Path);
                        return new ErrorMessage(L.T("The new version arrived damaged; nothing was changed."));
                    }
                    // Always the same file: Windows remembers the firewall permission (and the like) per path, so a
                    // new name for every version asked again each time. A running file can be renamed, not
                    // overwritten: it moves aside to .old (deleted by the next start) and the new one takes its place.
                    var exe = Path.Combine(UpdatesFolder, "DeskBridge.exe");
                    var old = exe + ".old";
                    if (File.Exists(old))
                        TryDelete(old);
                    if (File.Exists(exe))
                        File.Move(exe, File.Exists(old) ? exe + $".{Guid.NewGuid():N}.old" : old);
                    File.Move(update.Path, exe);
                    foreach (var stale in Directory.EnumerateFiles(UpdatesFolder, "DeskBridge-*.exe"))
                        TryDelete(stale); // the versioned files of earlier updates (one of them may still be running)
                    AddLog($"[update] installing DeskBridge {update.Version} ({exe}) and restarting");
                    // Answer first, then hand over: the new process waits for this one to exit (it holds the port).
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var args = $"--after-update {Environment.ProcessId}" + (IsVisible ? "" : " --minimized");
                            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                            Quit();
                        });
                    });
                    return new OkMessage();
                }

                default:
                    return new ErrorMessage("No update in progress.");
            }
        }
    }

    /// <summary>Receives a stream from <paramref name="machine"/> without showing it and reports how it went.</summary>
    private async Task<ControlMessage> ProbeVideoAsync(string machine, double seconds, CancellationToken ct)
    {
        if (SessionOf(machine) is not { } session)
            return new ErrorMessage(L.T("{machine} is not connected.", ("machine", machine)));
        var key = "probe-" + Guid.NewGuid().ToString("N")[..8];
        _videoPending[key] = (machine, null, null);
        var reply = await session.RequestAsync(new StartVideoMessage(key, Send: true, Display: "", Fps: 60, BitrateMbps: 20), ct);
        if (reply is ErrorMessage error)
        {
            _videoPending.TryRemove(key, out _);
            return error;
        }

        Platform.Windows.Video.VideoReceiver? receiver = null;
        for (int i = 0; i < 50 && !_videoReceivers.TryGetValue(key, out receiver); i++)
            await Task.Delay(100, ct);
        if (receiver is null)
            return new ErrorMessage($"{machine} accepted the stream but no video connection arrived.");

        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120)), ct);
        var (frames, bytes, format) = receiver.Stats;
        double elapsed = clock.Elapsed.TotalSeconds;
        await receiver.DisposeAsync(); // its completion stops the sender

        await Task.Delay(1000, ct); // let the sender log how it ended
        var lines = new List<string>
        {
            $"{machine}: {format?.Width}x{format?.Height}@{format?.Fps}, {format?.Gpu}, {format?.Encoder}",
            $"received {frames} frames in {elapsed:F1} s ({frames / elapsed:F1} fps), {bytes * 8 / 1e6 / elapsed:F1} Mbps",
        };
        if (await session.RequestAsync(new GetDiagnosticsMessage(), ct) is DiagnosticsMessage diagnostics)
            lines.AddRange(diagnostics.Log.Where(l => l.Contains("[video]")).TakeLast(8).Select(l => $"{machine} log: {l}"));
        lines.AddRange(_vm_LogSnapshot().Where(l => l.Contains("[video]")).TakeLast(6).Select(l => $"here: {l}"));
        return new ReportMessage(string.Join("\n", lines));
    }

    /// <summary>
    /// Logs what happens to the Windows session (locked, unlocked, remote desktop connected or gone): a locked
    /// session cannot be captured or controlled, which explains streams that pause and input that does nothing.
    /// </summary>
    private void WatchSession()
    {
        if (App.TakeLastCrash() is { } crash)
            AddLog("[error] the previous run " + crash);
        AddLog(Platform.Windows.Input.PointerOverlay.Needed ? "[session] this is a Remote Desktop session" : "[session] console session");
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            AddLog(e.Reason switch
            {
                Microsoft.Win32.SessionSwitchReason.SessionLock => "[session] locked: video and input pause until it is unlocked",
                Microsoft.Win32.SessionSwitchReason.SessionUnlock => "[session] unlocked",
                Microsoft.Win32.SessionSwitchReason.RemoteConnect => "[session] Remote Desktop connected",
                Microsoft.Win32.SessionSwitchReason.RemoteDisconnect => "[session] Remote Desktop disconnected",
                Microsoft.Win32.SessionSwitchReason.ConsoleConnect => "[session] back on this PC's own screen (console)",
                Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect => "[session] left this PC's own screen (console)",
                var other => $"[session] {other}",
            });
    }

    /// <summary>
    /// Each tab of this window drawn as a PNG (only DeskBridge's own window, nothing else on the screen), one per
    /// line as "tabN:base64"; the tab that was open is selected again afterwards.
    /// </summary>
    private string SnapshotTabsForProbe()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || Content is not FrameworkElement root || root.ActualWidth < 1)
            return "window not open (in the notification area): open DeskBridge on that machine to take snapshots";
        int open = Tabs.SelectedIndex;
        var lines = new List<string>();
        try
        {
            for (int i = 0; i < Tabs.Items.Count; i++)
            {
                Tabs.SelectedIndex = i;
                UpdateLayout();
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => { DrawLayout(); UpdateSummary(); });
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => { });
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                lines.Add($"tab{i}:{Convert.ToBase64String(stream.ToArray())}");
            }
        }
        finally
        {
            Tabs.SelectedIndex = open;
        }
        return string.Join("\n", lines);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<string> _vm_LogSnapshot() => Dispatcher.Invoke(() => _vm.Log.ToList());
}
