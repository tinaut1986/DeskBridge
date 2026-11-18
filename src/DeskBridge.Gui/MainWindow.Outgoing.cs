using System.Collections.Concurrent;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Video;
using DeskBridge.Platform;
using DeskBridge.Platform.Windows.Video;

namespace DeskBridge.Gui;

/// <summary>
/// The other way round: a display of another machine shows one of this PC's screens (e.g. the laptop's display
/// as a fourth monitor of the desktop). The coordinator sends its virtual display; the other machine opens the
/// video connection (it may not accept incoming ones, see <see cref="StartVideoMessage"/>) and shows the picture
/// full screen on its display.
/// </summary>
public partial class MainWindow
{
    /// <summary>Coordinator: streams it is waiting to send, by key (the machine that will connect for it, and what to send).</summary>
    private readonly ConcurrentDictionary<string, (string Machine, int X, int Y, int Fps, int BitrateMbps)> _outgoingPending = new();

    /// <summary>Coordinator: streams it sends, by key, and the machine showing each.</summary>
    private readonly ConcurrentDictionary<string, string> _outgoingTargets = new();

    /// <summary>
    /// Where the picture for a stream comes from on this PC: its planned virtual display, or (driver missing or
    /// stopped) a copy of the main display. Null when this PC has no display at all.
    /// </summary>
    private static (int X, int Y, bool Mirror)? SendPosition(string display)
    {
        var displays = Platform.Windows.WindowsDisplayDetector.DetectAll();
        var found = display.Contains('/') ? Platform.Windows.VirtualDisplays.MttVirtualDisplays.Find(display) : null;
        if (found is { } placed && displays.Any(d => d.X == placed.X && d.Y == placed.Y)
            && Platform.Windows.VirtualDisplays.MttVirtualDisplays.IsInstalled())
            return (placed.X, placed.Y, false);
        var fallback = displays.FirstOrDefault(d => d.Id == display)
                       ?? displays.FirstOrDefault(d => d.X == 0 && d.Y == 0)
                       ?? displays.FirstOrDefault();
        return fallback is null ? null : (fallback.X, fallback.Y, display.Contains('/'));
    }

    /// <summary>Coordinator: a stream of the applied profile that this PC sends to another machine.</summary>
    private async Task StartOutgoingStreamAsync(StreamSpec stream)
    {
        if (_videoSenders.ContainsKey(stream.Key) || _outgoingPending.ContainsKey(stream.Key) || !_videoStarting.TryAdd(stream.Key, 0))
            return;
        try
        {
            if (SessionOf(stream.SinkMachine) is not { } session)
            {
                AddLog($"[video] {stream.Key}: {stream.SinkMachine} is not connected; it starts when it connects");
                return;
            }
            var source = SendPosition(stream.SourceVirtualDisplay);
            // The other machine connected before this PC put the profile back in place here (after a restart, or
            // connecting again): its virtual display is coming. Waited for, rather than sending a copy of the main
            // display until the profile is applied again.
            for (int i = 0; i < 20 && source is { Mirror: true } && Platform.Windows.VirtualDisplays.MttVirtualDisplays.IsInstalled(); i++)
            {
                await Task.Delay(500);
                source = SendPosition(stream.SourceVirtualDisplay);
            }
            if (source is not { } found)
            {
                AddLog(L.T("This machine has no display to send."));
                return;
            }
            if (GraphicsHealth.Problem(found.X, found.Y) is { } broken)
            {
                AddLog($"[video] {stream.Key}: {broken}");
                return;
            }
            if (found.Mirror)
                AddLog(L.T("{machine} shows a copy of this PC's main display, not a display of its own: this PC has no working Virtual Display Driver.",
                    ("machine", MachineName(stream.SinkMachine))));
            _outgoingPending[stream.Key] = (stream.SinkMachine, found.X, found.Y, stream.RefreshHz, stream.BitrateMbps);
            try
            {
                var reply = await session.RequestAsync(new StartVideoMessage(stream.Key, Send: false, Display: stream.SinkDisplay,
                    Fps: stream.RefreshHz, BitrateMbps: stream.BitrateMbps));
                if (reply is ErrorMessage error)
                    throw new ControlException(error.Message);
                AddLog($"[video] {stream.Key}: {MachineName(stream.SinkMachine)} shows this PC's display {stream.SourceVirtualDisplay} on its display {stream.SinkDisplay}");
            }
            catch (ControlException ex)
            {
                _outgoingPending.TryRemove(stream.Key, out _);
                AddLog(L.T("Could not show this PC's screen on {machine}: {error}", ("machine", MachineName(stream.SinkMachine)), ("error", ex.Message)));
            }
        }
        finally
        {
            _videoStarting.TryRemove(stream.Key, out _);
        }
    }

    /// <summary>
    /// On a thread-pool thread: the machine asked to show one of this PC's screens connected for it. False when
    /// the connection is not one of those.
    /// </summary>
    private bool TryStartOutgoing(VideoChannel channel)
    {
        if (!_outgoingPending.TryRemove(channel.StreamKey, out var pending))
            return false;
        if (pending.Machine != channel.RemoteMachine)
        {
            _ = channel.DisposeAsync().AsTask();
            return true;
        }
        var key = channel.StreamKey;
        var sender = new VideoSender(channel, pending.X, pending.Y, pending.Fps, pending.BitrateMbps, AddLog);
        if (_videoSenders.TryGetValue(key, out var previous))
            _ = previous.DisposeAsync().AsTask();
        _videoSenders[key] = sender;
        _outgoingTargets[key] = pending.Machine;
        _ = sender.Completion.ContinueWith(async _ =>
        {
            bool current = _videoSenders.TryRemove(new KeyValuePair<string, VideoSender>(key, sender));
            if (current)
            {
                _outgoingTargets.TryRemove(key, out var _);
                AddLog(sender.Failure is { } failure ? $"[video] stream to {pending.Machine} stopped: {failure}" : $"[video] stream to {pending.Machine} stopped");
            }
            await sender.DisposeAsync();
            // Part of the applied profile and ended by itself (the other machine closed it, the network): again.
            if (current && _streamPlan?.Streams.FirstOrDefault(s => s.Key == key) is { } stream)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (_streamPlan?.Streams.Contains(stream) == true)
                    await Dispatcher.InvokeAsync(() => StartOutgoingStreamAsync(stream)).Task.Unwrap();
            }
        }, TaskScheduler.Default);
        return true;
    }

    /// <summary>Coordinator: this PC's streams to other machines stop (the profile changed or was paused).</summary>
    private async Task StopOutgoingStreamsAsync(Func<string, bool>? which = null)
    {
        foreach (var (key, machine) in _outgoingTargets.ToArray())
        {
            if (which is not null && !which(machine))
                continue;
            _outgoingTargets.TryRemove(key, out _);
            if (_videoSenders.TryRemove(key, out var sender))
                await sender.DisposeAsync(); // the other machine's window closes when the connection does
        }
        foreach (var key in _outgoingPending.Keys.ToArray())
            if (which is null || (_outgoingPending.TryGetValue(key, out var p) && which(p.Machine)))
                _outgoingPending.TryRemove(key, out _);
    }

    /// <summary>
    /// On the agent's connection thread, on the machine that shows the picture: the coordinator asks it to show one
    /// of its screens on display <see cref="StartVideoMessage.Display"/> here.
    /// </summary>
    private async Task<ControlMessage> ReceiveForCoordinatorAsync(string from, StartVideoMessage start, (string Host, int Port) coordinator, CancellationToken ct)
    {
        var monitor = PlatformServices.DisplayDetector().DetectDisplays().FirstOrDefault(d => d.Id == start.Display);
        if (monitor is null)
            return new ErrorMessage(L.T("Display {display} of this PC is not connected.", ("display", start.Display)));
        var key = Dispatcher.Invoke(() => _vm.LocalKey.Trim());
        VideoChannel channel;
        try
        {
            channel = await ControlClient.OpenVideoAsync(coordinator.Host, coordinator.Port, _agent!.Machine, from, ControlAuth.DecodeKey(key), start.Key, ct);
        }
        catch (ControlException ex)
        {
            return new ErrorMessage(ex.Message);
        }
        var receiver = new VideoReceiver(channel, $"DeskBridge: {MachineName(from)}", (monitor.X, monitor.Y, monitor.Width, monitor.Height), AddLog);
        if (_videoReceivers.TryGetValue(start.Key, out var replaced))
            _ = replaced.DisposeAsync().AsTask();
        _videoReceivers[start.Key] = receiver;
        TrackCoordinatorScreen(start.Key, from, (monitor.X, monitor.Y));
        _ = receiver.Completion.ContinueWith(async _ =>
        {
            if (_videoReceivers.TryRemove(new KeyValuePair<string, VideoReceiver>(start.Key, receiver)))
                TrackCoordinatorScreen(start.Key, from, null);
            await receiver.DisposeAsync();
            await Dispatcher.InvokeAsync(ScheduleRedraw);
        }, TaskScheduler.Default);
        AddLog($"[video] showing {MachineName(from)}'s screen on display {start.Display}");
        await Dispatcher.InvokeAsync(() =>
        {
            ScheduleRedraw();
            WatchCoveredDisplays();
        });
        return new OkMessage();
    }

    private System.Windows.Threading.DispatcherTimer? _rescueTimer;

    /// <summary>
    /// While a display here shows a screen of the coordinator, this PC's windows that open on it (behind the video,
    /// out of the coordinator's mouse's reach) are moved to a display of this PC that is not covered (see WindowRescue).
    /// </summary>
    private void WatchCoveredDisplays()
    {
        if (_rescueTimer is not null)
            return;
        // Often enough for the coordinator to follow a window moved or opened over the picture (see ReportWindowsOverVideo).
        _rescueTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _rescueTimer.Tick += (_, _) =>
        {
            if (_videoReceivers.IsEmpty || IsCoordinatorHere())
            {
                _rescueTimer?.Stop();
                _rescueTimer = null;
                return;
            }
            var covered = VideoWindow.Areas();
            var free = Platform.Windows.WindowsDisplayDetector.DetectAll()
                .Where(d => !covered.Any(c => c.X == d.X && c.Y == d.Y))
                .Select(d => (d.X, d.Y, d.Width, d.Height))
                .ToList();
            WindowRescue.Run(free, AddLog);
            ReportWindowsOverVideo();
        };
        _rescueTimer.Start();
    }
}
