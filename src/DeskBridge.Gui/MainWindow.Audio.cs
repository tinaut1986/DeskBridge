using System.Collections.Concurrent;
using DeskBridge.Core.Control;
using DeskBridge.Core.Video;
using DeskBridge.Platform.Windows.Audio;

namespace DeskBridge.Gui;

/// <summary>
/// Sound. A machine that is not the coordinator and has "send this PC's sound" on opens an audio connection
/// to the coordinator itself (like video: its firewall may block incoming ones) and sends everything it plays,
/// muting its own output meanwhile; the coordinator plays it if "play the other machines' sound here" is on,
/// and otherwise closes it saying so (the sender then waits a while before trying again).
/// </summary>
public partial class MainWindow
{
    /// <summary>The stream key of audio connections.</summary>
    private const string AudioKey = "audio";

    /// <summary>Said by a coordinator that does not play other machines' sound.</summary>
    private const string AudioOff = "off";

    /// <summary>Client: the sound this PC sends, if any.</summary>
    private AudioSender? _audioSender;

    private bool _audioStarting;
    private DateTime _audioRetryAfter;

    /// <summary>Coordinator: the sound of each machine being played here.</summary>
    private readonly ConcurrentDictionary<string, AudioPlayer> _audioPlayers = new();

    /// <summary>
    /// Client, on the UI thread, whenever something changes (a connection, the setting): starts sending if it
    /// should and does not yet, stops if it should not.
    /// </summary>
    private void UpdateAudio()
    {
        if (_options.SnapshotDir is not null)
            return;
        bool coordinator = IsCoordinatorHere();
        if (coordinator || !_settings.SendAudio)
        {
            if (_audioSender is { } sender)
            {
                _audioSender = null;
                _ = sender.DisposeAsync().AsTask();
                AddLog("[audio] stopped sending this PC's sound");
            }
        }
        if (!coordinator && _settings.SendAudio && _audioSender is null && !_audioStarting && DateTime.UtcNow >= _audioRetryAfter)
            _ = StartAudioSenderAsync();

        if (!coordinator || !_settings.PlayAudio)
            foreach (var (machine, player) in _audioPlayers.ToArray())
                if (_audioPlayers.TryRemove(new KeyValuePair<string, AudioPlayer>(machine, player)))
                    _ = player.DisposeAsync(AudioOff).AsTask();
    }

    private async Task StartAudioSenderAsync()
    {
        if (_agent is not { } agent)
            return;
        string? coordinator = agent.Reverse?.ConnectedTo ?? _vm.Model.Machines.FirstOrDefault(m => m.HasInput)?.Id.Trim();
        if (coordinator is null || coordinator == agent.Machine || SessionOf(coordinator) is null)
            return; // not connected to the coordinator yet: tried again when it connects
        var endpoint = agent.Reverse?.ConnectedTo == coordinator ? agent.CoordinatorEndpoint : CoordinatorEndpointFromConfig(coordinator);
        if (endpoint is not { } ep)
            return;

        _audioStarting = true;
        try
        {
            var key = ControlAuth.DecodeKey(_vm.LocalKey.Trim());
            var channel = await ControlClient.OpenVideoAsync(ep.Host, ep.Port, agent.Machine, coordinator, key, AudioKey);
            var sender = new AudioSender(channel, muteSpeakers: true, AddLog);
            _audioSender = sender;
            AddLog($"[audio] sending this PC's sound to {MachineName(coordinator)}");
            _ = sender.Completion.ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
            {
                if (_audioSender != sender)
                    return; // stopped on purpose
                _audioSender = null;
                await sender.DisposeAsync();
                if (sender.StopReason == AudioOff)
                {
                    AddLog($"[audio] {MachineName(coordinator)} does not play other machines' sound: trying again in a minute");
                    _audioRetryAfter = DateTime.UtcNow.AddMinutes(1);
                }
                else
                {
                    AddLog(sender.Failure is { } failure ? $"[audio] sound stopped: {failure}" : "[audio] sound stopped");
                    _audioRetryAfter = DateTime.UtcNow.AddSeconds(3);
                }
                await Task.Delay(_audioRetryAfter - DateTime.UtcNow + TimeSpan.FromMilliseconds(100));
                UpdateAudio();
            }), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is ControlException or FormatException or System.Runtime.InteropServices.COMException)
        {
            AddLog($"[audio] could not send this PC's sound: {ex.Message}");
            _audioRetryAfter = DateTime.UtcNow.AddSeconds(10);
            _ = Task.Delay(TimeSpan.FromSeconds(10.5)).ContinueWith(_ => Dispatcher.InvokeAsync(UpdateAudio), TaskScheduler.Default);
        }
        finally
        {
            _audioStarting = false;
        }
    }

    /// <summary>Coordinator, on a thread-pool thread: a machine opened an audio connection.</summary>
    private void OnAudioOpened(VideoChannel channel)
    {
        bool play = Dispatcher.Invoke(() => IsCoordinatorHere() && _settings.PlayAudio);
        if (!play)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.SendAsync(VideoPacket.Stop, default, System.Text.Encoding.UTF8.GetBytes(AudioOff));
                }
                catch (Exception)
                {
                }
                await channel.DisposeAsync();
            });
            return;
        }
        var player = new AudioPlayer(channel, AddLog);
        if (_audioPlayers.TryGetValue(channel.RemoteMachine, out var replaced))
            _ = replaced.DisposeAsync().AsTask();
        _audioPlayers[channel.RemoteMachine] = player;
        _ = player.Completion.ContinueWith(async _ =>
        {
            _audioPlayers.TryRemove(new KeyValuePair<string, AudioPlayer>(channel.RemoteMachine, player));
            await player.DisposeAsync();
        }, TaskScheduler.Default);
    }

    private async Task StopAllAudioAsync()
    {
        if (_audioSender is { } sender)
        {
            _audioSender = null;
            await sender.DisposeAsync(); // unmutes this PC's output
        }
        foreach (var (machine, player) in _audioPlayers.ToArray())
        {
            _audioPlayers.TryRemove(machine, out _);
            await player.DisposeAsync();
        }
    }
}
