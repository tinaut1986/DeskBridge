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
                var displays = PlatformServices.DisplayDetector().DetectDisplays();
                (int X, int Y)? position = Platform.Windows.VirtualDisplays.MttVirtualDisplays.Placed.TryGetValue(start.Display, out var placed)
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
                var sender = new VideoSender(channel, display.X, display.Y, start.Fps, start.BitrateMbps, AddLog);
                _videoSenders[start.Key] = sender;
                _ = sender.Completion.ContinueWith(_ =>
                {
                    if (_videoSenders.TryRemove(new KeyValuePair<string, VideoSender>(start.Key, sender)))
                        AddLog(sender.Failure is { } failure ? $"[video] stream to {from} stopped: {failure}" : $"[video] stream to {from} stopped");
                    _ = sender.DisposeAsync().AsTask();
                }, TaskScheduler.Default);
                return new OkMessage();
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
        if (!_videoPending.TryRemove(channel.StreamKey, out var target) || target.Machine != channel.RemoteMachine)
        {
            AddLog($"[video] unexpected video connection from {channel.RemoteMachine} ({channel.StreamKey}), closed");
            _ = channel.DisposeAsync().AsTask();
            return;
        }
        var receiver = new VideoReceiver(channel, target.Title, target.Monitor, AddLog);
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
            _videoReceivers.TryRemove(channel.StreamKey, out var _);
            await receiver.DisposeAsync();
            if (SessionOf(target.Machine) is { } diagnosed)
                await SaveDiagnosticsAsync(diagnosed); // the sender's side of the story, for troubleshooting
            // The window was closed here: the other machine stops sending.
            if (SessionOf(target.Machine) is { } session)
            {
                try
                {
                    await session.RequestAsync(new StopVideoMessage(channel.StreamKey));
                }
                catch (ControlException)
                {
                }
            }
        }, TaskScheduler.Default);
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
