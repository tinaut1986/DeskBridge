using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Video;
using DeskBridge.Platform;
using DeskBridge.Platform.Windows.Video;

namespace DeskBridge.Gui;

/// <summary>
/// Video streams. The machine that connects to the coordinator always opens the video connection (its
/// firewall may block incoming ones), whichever way the picture goes: the coordinator asks for it over the
/// control session (<see cref="StartVideoMessage"/>) and gets the connection on its own port.
/// For now: a test that shows another machine's main display in a window on the coordinator.
/// </summary>
public partial class MainWindow
{
    /// <summary>Client: streams this machine sends, by stream key.</summary>
    private readonly ConcurrentDictionary<string, VideoSender> _videoSenders = new();

    /// <summary>Coordinator: streams it asked for and is waiting to receive (window title, monitor or null).</summary>
    private readonly ConcurrentDictionary<string, (string Machine, string? Title, (int X, int Y, int W, int H)? Monitor)> _videoPending = new();

    /// <summary>
    /// Coordinator: displays of this PC showing another machine's display, by stream key (this PC's display, the
    /// machine, and its display in its own coordinates). The cursor goes to that machine when it enters them.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Machine, Core.Planning.DisplayRect Here, Core.Planning.DisplayRect There)> _lentDisplays = new();

    /// <summary>Coordinator: streams being shown, by stream key.</summary>
    private readonly ConcurrentDictionary<string, VideoReceiver> _videoReceivers = new();

    /// <summary>On the agent's connection thread: the coordinator asks this machine to start or stop a stream.</summary>
    private async Task<ControlMessage> HandleVideoAsync(string from, ControlMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case StartVideoMessage start when !start.Send:
                return new ErrorMessage(L.T("This machine cannot receive video yet."));

            case StartVideoMessage start:
            {
                if (_agent is not { } agent)
                    return new ErrorMessage(L.T("The agent is not running."));
                var endpoint = agent.Reverse?.ConnectedTo == from ? agent.CoordinatorEndpoint : CoordinatorEndpointFromConfig(from);
                if (endpoint is not { } ep)
                    return new ErrorMessage(L.T("This machine does not know how to reach {machine}.", ("machine", from)));

                // A planned virtual display (where it ended up), a physical display by id, or the main display.
                var displays = Platform.Windows.WindowsDisplayDetector.DetectAll();
                // (A virtual display that has gone away, e.g. the driver stopped: the main display instead.)
                var found = start.Display.Contains('/') ? Platform.Windows.VirtualDisplays.MttVirtualDisplays.Find(start.Display) : null;
                var placed = found ?? default;
                bool isVirtual = found is not null && displays.Any(d => d.X == placed.X && d.Y == placed.Y);
                if (start.Display.Contains('/') && !isVirtual)
                    AddLog($"[video] {start.Display}: placed {(found is { } f ? $"at ({f.X},{f.Y})" : "nowhere")}; displays here: "
                           + string.Join(", ", displays.Select(d => $"{d.Id} ({d.X},{d.Y})")));
                (int X, int Y)? position = isVirtual
                    ? placed
                    : (displays.FirstOrDefault(d => d.Id == start.Display)
                       ?? displays.FirstOrDefault(d => d.X == 0 && d.Y == 0)
                       ?? displays.FirstOrDefault()) is { } d ? (d.X, d.Y) : null;
                if (position is not { } display)
                    return new ErrorMessage(L.T("This machine has no display to send."));

                var key = Dispatcher.Invoke(() => _vm.LocalKey.Trim());
                VideoChannel channel;
                try
                {
                    channel = await ControlClient.OpenVideoAsync(ep.Host, ep.Port, agent.Machine, from, ControlAuth.DecodeKey(key), start.Key, ct);
                }
                catch (ControlException ex)
                {
                    return new ErrorMessage(ex.Message);
                }
                if (_videoSenders.TryRemove(start.Key, out var previous))
                    await previous.DisposeAsync(); // asked again (the coordinator reconnected): one sender per stream
                var sender = new VideoSender(channel, display.X, display.Y, start.Fps, start.BitrateMbps, AddLog);
                var sent = Platform.Windows.WindowsDisplayDetector.DetectAll().FirstOrDefault(d => d.X == display.X && d.Y == display.Y);
                _videoSenders[start.Key] = sender;
                _ = sender.Completion.ContinueWith(_ =>
                {
                    if (_videoSenders.TryRemove(new KeyValuePair<string, VideoSender>(start.Key, sender)))
                        AddLog(sender.Failure is { } failure ? $"[video] stream to {from} stopped: {failure}" : $"[video] stream to {from} stopped");
                    _ = sender.DisposeAsync().AsTask();
                }, TaskScheduler.Default);
                // A planned virtual display that is not there (driver missing or stopped) was replaced by a copy.
                bool mirror = start.Display.Contains('/') && !(isVirtual && Platform.Windows.VirtualDisplays.MttVirtualDisplays.IsInstalled());
                if (mirror)
                    AddLog($"[video] {start.Key}: no virtual display here (Virtual Display Driver missing or stopped): sending a copy of the main display");
                return sent is null ? new OkMessage() : new VideoStartedMessage(sent.X, sent.Y, sent.Width, sent.Height, mirror);
            }

            case StopVideoMessage stop:
                foreach (var (key, sender) in _videoSenders.ToArray())
                {
                    if (stop.Key is null || stop.Key == key)
                    {
                        _videoSenders.TryRemove(key, out _);
                        await sender.DisposeAsync();
                    }
                }
                return new OkMessage();

            default:
                return new ErrorMessage(L.T("Unexpected request: {request}.", ("request", message.GetType().Name)));
        }
    }

    private (string Host, int Port)? CoordinatorEndpointFromConfig(string machine) =>
        Dispatcher.Invoke(() =>
        {
            if (PeerContext() is not { } context || context.Config.Machines.FirstOrDefault(m => m.Id == machine) is not { } config)
                return ((string, int)?)null;
            return ((string, int)?)Peers.Endpoint(context.Config, context.Local, config);
        });

    /// <summary>On a thread-pool thread: a machine opened a video connection to this one.</summary>
    private void OnVideoOpened(VideoChannel channel)
    {
        if (channel.StreamKey is AudioKey or MicKey)
        {
            OnAudioOpened(channel);
            return;
        }
        if (!_videoPending.TryRemove(channel.StreamKey, out var target) || target.Machine != channel.RemoteMachine)
        {
            AddLog($"[video] unexpected video connection from {channel.RemoteMachine} ({channel.StreamKey}), closed");
            _ = channel.DisposeAsync().AsTask();
            return;
        }
        var receiver = new VideoReceiver(channel, target.Title, target.Monitor, AddLog);
        if (_videoReceivers.TryGetValue(channel.StreamKey, out var replaced))
            _ = replaced.DisposeAsync().AsTask();
        _videoReceivers[channel.StreamKey] = receiver;
        _ = Task.Run(async () =>
        {
            // No picture after a few seconds: fetch the sender's log right away, to see why.
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (!receiver.Completion.IsCompleted && receiver.Stats.Frames == 0 && SessionOf(target.Machine) is { } session)
            {
                AddLog($"[video] no picture from {target.Machine} after 6 s");
                if (await SaveDiagnosticsAsync(session) is { } path)
                    AddLog($"[video] diagnostics of {target.Machine} saved: {path}");
            }
        });
        _ = receiver.Completion.ContinueWith(async _ =>
        {
            bool current = _videoReceivers.TryRemove(new KeyValuePair<string, VideoReceiver>(channel.StreamKey, receiver));
            if (current && _lentDisplays.TryRemove(channel.StreamKey, out var _))
                await Dispatcher.InvokeAsync(RefreshInputTargets);
            if (current && _mirroredDisplays.TryRemove(channel.StreamKey, out var _))
                await Dispatcher.InvokeAsync(ScheduleRedraw);
            await receiver.DisposeAsync();
            if (!current)
                return; // replaced by a newer receiver for the same stream: nothing else to do

            // The window was closed here, or the stream ended: the other machine stops sending (before any restart).
            if (SessionOf(target.Machine) is { } session)
            {
                try
                {
                    await session.RequestAsync(new StopVideoMessage(channel.StreamKey));
                }
                catch (ControlException)
                {
                }
                await SaveDiagnosticsAsync(session); // the sender's side of the story, for troubleshooting
            }

            // A stream of the applied profile that ended by itself (the sender gave up, the connection broke):
            // ask for it again; it stays off only when the profile changes or the tray stops it.
            if (_streamPlan?.Streams.FirstOrDefault(s => s.Key == channel.StreamKey) is { } stream)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (_streamPlan?.Streams.Contains(stream) == true)
                {
                    AddLog($"[video] {stream.Key} stopped by itself: starting it again");
                    await Dispatcher.InvokeAsync(() => StartProfileStreamAsync(stream)).Task.Unwrap();
                }
            }
        }, TaskScheduler.Default);
    }

    private void VideoTest_Click(object sender, RoutedEventArgs e)
    {
        VideoTestMenu_SubmenuOpened(sender, e);
        VideoTestMenu.PlacementTarget = VideoTestButton;
        VideoTestMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        VideoTestMenu.IsOpen = true;
    }

    private void VideoTestMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        VideoTestMenu.Items.Clear();
        var connected = _agent?.Directory.Connected.ToHashSet() ?? [];
        foreach (var machine in OtherMachines().Where(m => connected.Contains(m.Id.Trim())))
        {
            var item = new MenuItem { Header = machine.ToString() };
            var id = machine.Id.Trim();
            item.Click += async (_, _) => await StartVideoTestAsync(id);
            VideoTestMenu.Items.Add(item);
        }
        if (VideoTestMenu.Items.Count == 0)
            VideoTestMenu.Items.Add(new MenuItem { Header = L.T("No machine is connected"), IsEnabled = false });
    }

    /// <summary>Shows another machine's main display in a window here (the first real use of the video path).</summary>
    private async Task StartVideoTestAsync(string machine)
    {
        if (SessionOf(machine) is not { } session)
            return;
        var key = "test-" + Guid.NewGuid().ToString("N")[..8];
        _videoPending[key] = (machine, L.T("{machine}: screen (video test)", ("machine", MachineName(machine))), null);
        try
        {
            if (await session.RequestAsync(new StartVideoMessage(key, Send: true, Display: "", Fps: 60, BitrateMbps: 20)) is ErrorMessage error)
                throw new ControlException(error.Message);
        }
        catch (ControlException ex)
        {
            _videoPending.TryRemove(key, out _);
            var text = L.T("Could not start the video from {machine}: {error}", ("machine", MachineName(machine)), ("error", ex.Message));
            AddLog(text);
            _vm.Status = text;
        }
    }

    /// <summary>The profile whose streams are running (to start them again when a machine reconnects).</summary>
    private Core.Model.LayoutPlan? _streamPlan;

    /// <summary>
    /// Coordinator, after a profile is applied: every stream another machine sends here is asked for and shown
    /// full screen on the display it is assigned to; streams of the previous profile stop.
    /// </summary>
    private async Task StartProfileStreamsAsync(Core.Model.LayoutPlan plan)
    {
        await StopProfileStreamsAsync();
        _streamPlan = plan;
        foreach (var stream in plan.Streams)
            await StartProfileStreamAsync(stream);
    }

    private async Task StartProfileStreamAsync(Core.Model.StreamSpec stream)
    {
        var here = _vm.LocalMachine.Trim();
        if (stream.SourceMachine == here)
        {
            AddLog($"[video] {stream.Key}: showing this PC's displays on another machine is not available yet");
            return;
        }
        if (stream.SinkMachine != here || _videoReceivers.ContainsKey(stream.Key) || _videoPending.ContainsKey(stream.Key))
            return;
        // A reconnection and a restart can both ask at once: one request per stream at a time.
        if (!_videoStarting.TryAdd(stream.Key, 0))
            return;
        try
        {
            await StartProfileStreamCoreAsync(stream);
        }
        finally
        {
            _videoStarting.TryRemove(stream.Key, out _);
        }
    }

    private readonly ConcurrentDictionary<string, byte> _videoStarting = new();

    /// <summary>Coordinator: displays here (by stream key) showing a copy of another machine's main display.</summary>
    private readonly ConcurrentDictionary<string, string> _mirroredDisplays = new();

    private async Task StartProfileStreamCoreAsync(Core.Model.StreamSpec stream)
    {
        var monitor = PlatformServices.DisplayDetector().DetectDisplays().FirstOrDefault(d => d.Id == stream.SinkDisplay);
        if (monitor is null)
        {
            AddLog($"[video] {stream.Key}: display {stream.SinkDisplay} of this PC is not connected");
            return;
        }
        if (SessionOf(stream.SourceMachine) is not { } session)
        {
            AddLog($"[video] {stream.Key}: {stream.SourceMachine} is not connected; it starts when it connects");
            return;
        }
        _videoPending[stream.Key] = (stream.SourceMachine, $"DeskBridge: {MachineName(stream.SourceMachine)}",
            (monitor.X, monitor.Y, monitor.Width, monitor.Height));
        try
        {
            var reply = await session.RequestAsync(new StartVideoMessage(stream.Key, Send: true, Display: stream.SourceVirtualDisplay,
                Fps: stream.RefreshHz, BitrateMbps: stream.BitrateMbps));
            if (reply is ErrorMessage error)
                throw new ControlException(error.Message);
            if (reply is VideoStartedMessage started)
            {
                if (started.Mirror)
                {
                    _mirroredDisplays[stream.Key] = stream.SinkDisplay;
                    AddLog($"[video] display {stream.SinkDisplay} shows a copy of {MachineName(stream.SourceMachine)}'s main display, not a second one: "
                           + "it has no working Virtual Display Driver");
                }
                else
                {
                    _mirroredDisplays.TryRemove(stream.Key, out _);
                }
                ScheduleRedraw();
                _lentDisplays[stream.Key] = (stream.SourceMachine,
                    new Core.Planning.DisplayRect(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                    new Core.Planning.DisplayRect(started.X, started.Y, started.Width, started.Height));
                RefreshInputTargets();
            }
        }
        catch (ControlException ex)
        {
            _videoPending.TryRemove(stream.Key, out _);
            AddLog(L.T("Could not start the video from {machine}: {error}", ("machine", MachineName(stream.SourceMachine)), ("error", ex.Message)));
        }
    }

    /// <summary>Stops the streams of the applied profile (test and probe streams keep running).</summary>
    private async Task StopProfileStreamsAsync()
    {
        _streamPlan = null;
        foreach (var (key, receiver) in _videoReceivers.ToArray())
        {
            if (key.StartsWith("test-", StringComparison.Ordinal) || key.StartsWith("probe-", StringComparison.Ordinal))
                continue;
            _videoReceivers.TryRemove(key, out _);
            await receiver.DisposeAsync(); // its completion tells the sender to stop
        }
    }

    /// <summary>A machine (re)connected: start the streams of the applied profile it sends.</summary>
    private void ResumeProfileStreams(string machine)
    {
        if (_streamPlan is not { } plan)
            return;
        foreach (var stream in plan.Streams.Where(s => s.SourceMachine == machine))
            _ = StartProfileStreamAsync(stream);
    }

    private async Task StopAllVideoAsync()
    {
        await StopAllAudioAsync();
        foreach (var (key, sender) in _videoSenders.ToArray())
        {
            _videoSenders.TryRemove(key, out _);
            await sender.DisposeAsync();
        }
        foreach (var (key, receiver) in _videoReceivers.ToArray())
        {
            _videoReceivers.TryRemove(key, out _);
            await receiver.DisposeAsync();
        }
    }
}
