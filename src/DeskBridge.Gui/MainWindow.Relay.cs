using System.Collections.Concurrent;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Video;

namespace DeskBridge.Gui;

/// <summary>
/// A display of one machine showing a screen of another, neither of them the coordinator (the tablet showing the
/// laptop): the other machines only connect to the coordinator, so the coordinator relays the stream. Both open a
/// video connection to it for the stream; packets go across as they are (no decoding here), the picture one way and
/// the receiver's key frame requests and delay reports the other.
/// </summary>
public partial class MainWindow
{
    private sealed class Relay(StreamSpec stream)
    {
        public StreamSpec Stream { get; } = stream;
        public VideoChannel? From;
        public VideoChannel? To;
        public readonly CancellationTokenSource Stop = new();

        /// <summary>The display sent, in the sending machine's coordinates (where the cursor goes over the one showing it).</summary>
        public Core.Planning.DisplayRect? There; // set once, then read (the input refresh runs after it is set)
        public int Started;
    }

    /// <summary>Coordinator: streams it relays between two other machines, by key.</summary>
    private readonly ConcurrentDictionary<string, Relay> _relays = new();

    private async Task StartRelayAsync(StreamSpec stream)
    {
        if (_relays.ContainsKey(stream.Key))
            return;
        if (SessionOf(stream.SourceMachine) is not { } from || SessionOf(stream.SinkMachine) is not { } to)
        {
            AddLog($"[video] {stream.Key}: {stream.SourceMachine} or {stream.SinkMachine} is not connected; it starts when both are");
            return;
        }
        var relay = new Relay(stream);
        if (!_relays.TryAdd(stream.Key, relay))
            return;
        try
        {
            // The one that shows it first (it waits for the picture), then the one that sends it.
            if (await to.RequestAsync(new StartVideoMessage(stream.Key, Send: false, Display: stream.SinkDisplay,
                    Fps: stream.RefreshHz, BitrateMbps: stream.BitrateMbps)) is ErrorMessage toError)
                throw new ControlException(toError.Message);
            var reply = await from.RequestAsync(new StartVideoMessage(stream.Key, Send: true, Display: stream.SourceVirtualDisplay,
                Fps: stream.RefreshHz, BitrateMbps: stream.BitrateMbps));
            if (reply is ErrorMessage fromError)
                throw new ControlException(fromError.Message);
            if (reply is VideoStartedMessage started)
            {
                relay.There = new Core.Planning.DisplayRect(started.X, started.Y, started.Width, started.Height);
                await Dispatcher.InvokeAsync(RefreshInputTargets);
            }
            if (reply is VideoStartedMessage { Mirror: true })
                AddLog($"[video] {stream.Key}: {MachineName(stream.SinkMachine)} shows a copy of {MachineName(stream.SourceMachine)}'s main display, not a display of its own: it has no working Virtual Display Driver");
            AddLog($"[video] {stream.Key}: {MachineName(stream.SinkMachine)} shows {MachineName(stream.SourceMachine)}'s screen, relayed through this PC");
        }
        catch (ControlException ex)
        {
            EndRelay(relay, restart: false);
            AddLog(L.T("Could not start the video from {machine}: {error}", ("machine", MachineName(stream.SourceMachine)), ("error", ex.Message)));
        }
    }

    /// <summary>On a thread-pool thread: one end of a relayed stream connected. False when the connection is not one.</summary>
    private bool TryJoinRelay(VideoChannel channel)
    {
        if (!_relays.TryGetValue(channel.StreamKey, out var relay))
            return false;
        lock (relay)
        {
            if (channel.RemoteMachine == relay.Stream.SourceMachine && relay.From is null)
                relay.From = channel;
            else if (channel.RemoteMachine == relay.Stream.SinkMachine && relay.To is null)
                relay.To = channel;
            else
            {
                _ = channel.DisposeAsync().AsTask();
                return true;
            }
            if (relay.From is null || relay.To is null || Interlocked.Exchange(ref relay.Started, 1) != 0)
                return true;
        }
        _ = Task.Run(() => PumpAsync(relay, relay.From!, relay.To!));
        _ = Task.Run(() => PumpAsync(relay, relay.To!, relay.From!));
        return true;
    }

    /// <summary>Packets from one end to the other, as they are, until either end goes.</summary>
    private async Task PumpAsync(Relay relay, VideoChannel source, VideoChannel target)
    {
        try
        {
            while (!relay.Stop.IsCancellationRequested
                   && await source.ReceiveAsync(relay.Stop.Token) is { } packet)
            {
                await target.SendAsync(packet.Type, packet.Body, default, relay.Stop.Token);
                if (packet.Type == VideoPacket.Stop)
                    break;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or ObjectDisposedException
                                       or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException)
        {
        }
        EndRelay(relay, restart: true);
    }

    /// <summary>Both ends close; a stream of the applied profile is asked for again a moment later.</summary>
    private void EndRelay(Relay relay, bool restart)
    {
        if (!_relays.TryRemove(new KeyValuePair<string, Relay>(relay.Stream.Key, relay)))
            return;
        relay.Stop.Cancel();
        _ = relay.From?.DisposeAsync().AsTask();
        _ = relay.To?.DisposeAsync().AsTask();
        if (!restart)
            return;
        AddLog($"[video] relayed stream {relay.Stream.Key} stopped");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_streamPlan?.Streams.Contains(relay.Stream) == true)
                await Dispatcher.InvokeAsync(() => StartRelayAsync(relay.Stream)).Task.Unwrap();
        });
    }

    /// <summary>Relayed streams stop: all of them, or those of machines <paramref name="which"/> says.</summary>
    private void StopRelays(Func<string, bool>? which = null)
    {
        foreach (var relay in _relays.Values.ToArray())
            if (which is null || which(relay.Stream.SourceMachine) || which(relay.Stream.SinkMachine))
                EndRelay(relay, restart: false);
    }
}
