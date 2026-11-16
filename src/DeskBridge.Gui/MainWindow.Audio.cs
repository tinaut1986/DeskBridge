using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Video;
using DeskBridge.Platform.Windows.Audio;

namespace DeskBridge.Gui;

/// <summary>
/// Sound and microphone. A machine that is not the coordinator opens every audio connection itself (like
/// video: its firewall may block incoming ones):
/// <list type="bullet">
/// <item>"audio": it sends everything it plays (its own output muted meanwhile), played on the coordinator if
/// "play the other machines' sound here" is on there, at the volume chosen there for that machine;</item>
/// <item>"mic": the coordinator sends its microphone, if "let the other machines use my microphone" is on
/// there, played here into VB-CABLE's input so that apps here can use "CABLE Output" as a microphone.</item>
/// </list>
/// A coordinator that does not want a connection closes it saying so ("off"), and the machine waits a while
/// before trying again.
/// </summary>
public partial class MainWindow
{
    private const string AudioKey = "audio";
    private const string MicKey = "mic";

    /// <summary>Said by a coordinator that does not play other machines' sound or share its microphone.</summary>
    private const string AudioOff = "off";

    /// <summary>The output VB-CABLE adds; what is played into it comes out of its "CABLE Output" microphone.</summary>
    private const string CableInput = "CABLE Input";

    private const string CablePage = "https://vb-audio.com/Cable/";

    /// <summary>Client: a connection it keeps open while it should (the stream, or when to try again).</summary>
    private sealed class Outgoing
    {
        public IAudioStream? Stream;
        public bool Starting;
        public DateTime RetryAfter;
    }

    private readonly Dictionary<string, Outgoing> _outgoing = new() { [AudioKey] = new(), [MicKey] = new() };

    /// <summary>Coordinator: the sound of each machine being played here, and the microphone sent to each.</summary>
    private readonly ConcurrentDictionary<string, AudioPlayer> _audioPlayers = new();

    private readonly ConcurrentDictionary<string, AudioSender> _micSenders = new();

    /// <summary>
    /// On the UI thread, whenever something changes (a connection, a setting): starts what should run and
    /// does not yet, stops what should not.
    /// </summary>
    private void UpdateAudio()
    {
        if (_options.SnapshotDir is not null)
        {
            UpdateAudioUi();
            return;
        }
        bool coordinator = IsCoordinatorHere();
        bool cable = AudioPlayer.HasOutput(CableInput);
        Want(AudioKey, !coordinator && _settings.SendAudio);
        Want(MicKey, !coordinator && _settings.UseRemoteMic && cable);

        if (!coordinator || !_settings.PlayAudio)
            foreach (var (machine, player) in _audioPlayers.ToArray())
                if (_audioPlayers.TryRemove(new KeyValuePair<string, AudioPlayer>(machine, player)))
                    _ = player.DisposeAsync(AudioOff).AsTask();
        if (!coordinator || !_settings.ShareMic)
            foreach (var (machine, sender) in _micSenders.ToArray())
                if (_micSenders.TryRemove(new KeyValuePair<string, AudioSender>(machine, sender)))
                    _ = sender.DisposeAsync(AudioOff).AsTask();
        UpdateAudioUi();
    }

    private void Want(string key, bool wanted)
    {
        var link = _outgoing[key];
        if (!wanted && link.Stream is { } stream)
        {
            link.Stream = null;
            _ = stream.DisposeAsync("stopped").AsTask();
            AddLog(key == AudioKey ? "[audio] stopped sending this PC's sound" : "[audio] stopped using the coordinator's microphone");
        }
        if (wanted && link.Stream is null && !link.Starting && DateTime.UtcNow >= link.RetryAfter)
            _ = OpenAsync(key, link);
    }

    private async Task OpenAsync(string key, Outgoing link)
    {
        if (_agent is not { } agent)
            return;
        string? coordinator = agent.Reverse?.ConnectedTo ?? _vm.Model.Machines.FirstOrDefault(m => m.HasInput)?.Id.Trim();
        if (coordinator is null || coordinator == agent.Machine || SessionOf(coordinator) is null)
            return; // not connected to the coordinator yet: tried again when it connects
        var endpoint = agent.Reverse?.ConnectedTo == coordinator ? agent.CoordinatorEndpoint : CoordinatorEndpointFromConfig(coordinator);
        if (endpoint is not { } ep)
            return;

        string name = MachineName(coordinator);
        link.Starting = true;
        try
        {
            var channel = await ControlClient.OpenVideoAsync(ep.Host, ep.Port, agent.Machine, coordinator, ControlAuth.DecodeKey(_vm.LocalKey.Trim()), key);
            IAudioStream stream = key == AudioKey
                ? new AudioSender(channel, AudioSource.Output, AddLog)
                : new AudioPlayer(channel, CableInput, AddLog);
            link.Stream = stream;
            AddLog(key == AudioKey ? $"[audio] sending this PC's sound to {name}" : $"[audio] {name}'s microphone comes out of '{CableInput}' here");
            _ = stream.Completion.ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
            {
                if (link.Stream != stream)
                    return; // stopped on purpose
                link.Stream = null;
                await stream.DisposeAsync("stopped");
                if (stream.StopReason == AudioOff)
                {
                    AddLog(key == AudioKey
                        ? $"[audio] {name} does not play other machines' sound: trying again in a minute"
                        : $"[audio] {name} does not share its microphone: trying again in a minute");
                    link.RetryAfter = DateTime.UtcNow.AddMinutes(1);
                }
                else
                {
                    AddLog(stream.Failure is { } failure ? $"[audio] {key} stopped: {failure}" : $"[audio] {key} stopped");
                    link.RetryAfter = DateTime.UtcNow.AddSeconds(3);
                }
                RetryLater(link);
            }), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is ControlException or FormatException or System.Runtime.InteropServices.COMException)
        {
            AddLog($"[audio] could not open {key} with {name}: {ex.Message}");
            link.RetryAfter = DateTime.UtcNow.AddSeconds(10);
            RetryLater(link);
        }
        finally
        {
            link.Starting = false;
        }
    }

    private void RetryLater(Outgoing link) =>
        _ = Task.Delay(link.RetryAfter - DateTime.UtcNow + TimeSpan.FromMilliseconds(200))
            .ContinueWith(_ => Dispatcher.InvokeAsync(UpdateAudio), TaskScheduler.Default);

    /// <summary>Coordinator, on a thread-pool thread: a machine opened an audio connection.</summary>
    private void OnAudioOpened(VideoChannel channel)
    {
        var machine = channel.RemoteMachine;
        var (wanted, volume) = Dispatcher.Invoke(() => (
            IsCoordinatorHere() && (channel.StreamKey == AudioKey ? _settings.PlayAudio : _settings.ShareMic),
            VolumeOf(machine)));
        if (!wanted)
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

        if (channel.StreamKey == AudioKey)
        {
            var player = new AudioPlayer(channel, null, AddLog, volume);
            if (_audioPlayers.TryGetValue(machine, out var replaced))
                _ = replaced.DisposeAsync().AsTask();
            _audioPlayers[machine] = player;
            _ = player.Completion.ContinueWith(async _ =>
            {
                _audioPlayers.TryRemove(new KeyValuePair<string, AudioPlayer>(machine, player));
                await player.DisposeAsync();
            }, TaskScheduler.Default);
        }
        else
        {
            var sender = new AudioSender(channel, AudioSource.Microphone, AddLog);
            if (_micSenders.TryGetValue(machine, out var replaced))
                _ = replaced.DisposeAsync().AsTask();
            _micSenders[machine] = sender;
            AddLog($"[audio] sending this PC's microphone to {MachineName(machine)}");
            _ = sender.Completion.ContinueWith(async _ =>
            {
                _micSenders.TryRemove(new KeyValuePair<string, AudioSender>(machine, sender));
                await sender.DisposeAsync();
            }, TaskScheduler.Default);
        }
    }

    private async Task StopAllAudioAsync()
    {
        foreach (var link in _outgoing.Values)
        {
            if (link.Stream is { } stream)
            {
                link.Stream = null;
                await stream.DisposeAsync("stopped"); // unmutes this PC's output
            }
        }
        foreach (var (machine, player) in _audioPlayers.ToArray())
        {
            _audioPlayers.TryRemove(machine, out _);
            await player.DisposeAsync();
        }
        foreach (var (machine, sender) in _micSenders.ToArray())
        {
            _micSenders.TryRemove(machine, out _);
            await sender.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ volume of each machine, and the settings page

    private float VolumeOf(string machine) =>
        (_settings.AudioVolumes is { } volumes && volumes.TryGetValue(machine, out int percent) ? percent : 100) / 100f;

    private DispatcherTimer? _volumeSave;

    /// <summary>The coordinator's volume sliders (one per other machine) and the microphone's VB-CABLE note.</summary>
    private void UpdateAudioUi()
    {
        bool coordinator = IsCoordinatorHere();
        var machines = _vm.Model.Machines.Select(m => (Id: m.Id.Trim(), m.Name)).Where(m => m.Id != _vm.LocalMachine.Trim()).ToList();
        AudioVolumesPanel.Visibility = coordinator && machines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = AudioVolumesPanel.Children.OfType<FrameworkElement>().Select(e => e.Tag as string).ToList();
        if (!shown.SequenceEqual(machines.Select(m => m.Id)))
        {
            AudioVolumesPanel.Children.Clear();
            foreach (var (id, name) in machines)
                AudioVolumesPanel.Children.Add(VolumeRow(id, name));
        }

        bool cable = _options.SnapshotDir is null && AudioPlayer.HasOutput(CableInput);
        CableMissingPanel.Visibility = cable || coordinator ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement VolumeRow(string machine, string name)
    {
        int percent = (int)Math.Round(VolumeOf(machine) * 100);
        var value = new TextBlock { Text = $"{percent} %", Width = 50, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "NormalText");
        var slider = new Slider { Minimum = 0, Maximum = 150, Value = percent, Width = 240, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 5, SmallChange = 5, LargeChange = 10 };
        slider.ValueChanged += (_, e) =>
        {
            int p = (int)e.NewValue;
            value.Text = $"{p} %";
            if (_audioPlayers.TryGetValue(machine, out var player))
                player.Volume = p / 100f;
            var volumes = new Dictionary<string, int>(_settings.AudioVolumes ?? []) { [machine] = p };
            _settings = _settings with { AudioVolumes = volumes };
            _volumeSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
            {
                _volumeSave!.Stop();
                SaveUserSettings(_settings);
            }, Dispatcher);
            _volumeSave.Stop();
            _volumeSave.Start();
        };
        var label = new TextBlock { Text = L.T("Volume of {machine}", ("machine", name)) };
        label.SetResourceReference(StyleProperty, "RowTitle");
        var panel = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(slider);
        right.Children.Add(value);
        DockPanel.SetDock(right, Dock.Right);
        panel.Children.Add(right);
        panel.Children.Add(label);
        var row = new Border { Child = panel, Tag = machine };
        row.SetResourceReference(StyleProperty, "Row");
        return row;
    }

    private void CablePage_Click(object sender, RoutedEventArgs e) => OpenExternal(CablePage);
}
