using System.Buffers.Binary;
using System.Runtime.Versioning;
using DeskBridge.Core.Audio;
using DeskBridge.Core.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DeskBridge.Platform.Windows.Audio;

/// <summary>
/// Plays the sound another machine sends (<see cref="AudioSender"/>) on an output of this PC: the default one,
/// or a given one (the VB-CABLE input that apps here use as a microphone). Small buffer: when more than
/// <see cref="MaxBehind"/> piles up (the network hiccupped) it is dropped, so the sound never lags behind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioPlayer : IAsyncDisposable, IAudioStream
{
    private static readonly TimeSpan MaxBehind = TimeSpan.FromMilliseconds(150);

    private readonly VideoChannel _channel;
    private readonly string? _deviceName;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volumeStage;
    private OpusDecoder? _opus;
    private WasapiOut? _output;
    private string? _deviceId;
    private int _dropped;
    private float _volume = 1;

    public Task Completion => _done.Task;

    public string RemoteMachine => _channel.RemoteMachine;

    /// <summary>Why it stopped by itself, when it did (no such output here, the stream failed).</summary>
    public string? Failure { get; private set; }

    /// <summary>Why the other side stopped the stream, if it said so.</summary>
    public string? StopReason { get; private set; }

    /// <summary>0 (silent) to 1 (as sent); more than 1 makes it louder.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0, value);
            if (_volumeStage is { } stage)
                stage.Volume = _volume;
        }
    }

    /// <param name="deviceName">Part of the name of the output to play on; null for the default output (followed when it changes).</param>
    public AudioPlayer(VideoChannel channel, string? deviceName, Action<string> log, float volume = 1)
    {
        _channel = channel;
        _deviceName = deviceName;
        _log = log;
        _volume = volume;
        _ = RunAsync();
        if (deviceName is null)
            _ = WatchDeviceAsync();
    }

    /// <summary>The output whose name contains <paramref name="name"/>, if this PC has one.</summary>
    public static bool HasOutput(string name)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Any(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _channel.ReceiveAsync(_stop.Token);
                if (packet is not { } p)
                    break;
                switch (p.Type)
                {
                    case VideoPacket.Stop:
                        StopReason = System.Text.Encoding.UTF8.GetString(p.Body);
                        _stop.Cancel();
                        break;

                    case VideoPacket.AudioFormat:
                        int rate = BinaryPrimitives.ReadInt32LittleEndian(p.Body);
                        int channels = BinaryPrimitives.ReadInt16LittleEndian(p.Body.AsSpan(4));
                        var codec = p.Body.Length > 6 ? (AudioCodec)p.Body[6] : AudioCodec.Pcm;
                        _opus = codec == AudioCodec.Opus ? new OpusDecoder(rate, channels) : null;
                        Start(new WaveFormat(rate, 16, channels));
                        break;

                    case VideoPacket.Audio when _buffer is { } buffer:
                        var pcm = _opus is { } opus ? opus.Decode(p.Body) : p.Body;
                        if (buffer.BufferedDuration > MaxBehind)
                        {
                            buffer.ClearBuffer(); // catch up instead of playing late
                            _dropped++;
                        }
                        buffer.AddSamples(pcm, 0, pcm.Length);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
        }
        finally
        {
            StopOutput();
            _stop.Cancel();
            _done.TrySetResult();
        }
    }

    private void Start(WaveFormat format)
    {
        StopOutput();
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence while nothing arrives (Windows sends nothing while nothing plays)
        };
        StartOutput();
    }

    private MMDevice? FindDevice(MMDeviceEnumerator enumerator) =>
        _deviceName is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(_deviceName, StringComparison.OrdinalIgnoreCase));

    private void StartOutput()
    {
        if (_buffer is not { } buffer)
            return;
        using var enumerator = new MMDeviceEnumerator();
        var device = FindDevice(enumerator) ?? throw new InvalidOperationException($"there is no '{_deviceName}' output on this PC");
        _deviceId = device.ID;
        var mix = device.AudioClient.MixFormat;

        // Float at the output's own rate. The channels stay as sent (stereo or mono): Windows puts them on the
        // front speakers of a 5.1/7.1 output, while a plain float format with more than 2 channels is refused.
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != mix.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        _volumeStage = new VolumeSampleProvider(samples) { Volume = _volume };

        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        output.Init(new SampleToWaveProvider(_volumeStage));
        output.Play();
        _output = output;
        _log($"[audio] playing the sound of {_channel.RemoteMachine} on '{device.FriendlyName}'");
    }

    private void StopOutput()
    {
        var output = Interlocked.Exchange(ref _output, null);
        if (output is null)
            return;
        try
        {
            output.Stop();
        }
        catch (Exception)
        {
        }
        output.Dispose();
    }

    /// <summary>Moves to the new default output when it changes, and reports catch-ups now and then.</summary>
    private async Task WatchDeviceAsync()
    {
        var report = DateTime.UtcNow;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(2000, _stop.Token);
                string? current;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    current = device.ID;
                }
                catch (Exception)
                {
                    current = null;
                }
                if (current is not null && current != _deviceId && _buffer is not null)
                {
                    StopOutput();
                    StartOutput();
                }
                if ((DateTime.UtcNow - report).TotalSeconds >= 60)
                {
                    if (_dropped > 0)
                        _log($"[audio] {_channel.RemoteMachine}: caught up {_dropped} times in the last minute (sound arriving late)");
                    _dropped = 0;
                    report = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[audio] could not switch output: {ex.Message}");
        }
    }

    /// <summary>Stops playing; <paramref name="reason"/> tells the sender why (it waits before trying again).</summary>
    public async ValueTask DisposeAsync(string reason)
    {
        if (!_done.Task.IsCompleted)
        {
            try
            {
                await _channel.SendAsync(VideoPacket.Stop, default, System.Text.Encoding.UTF8.GetBytes(reason));
            }
            catch (Exception)
            {
            }
        }
        _stop.Cancel();
        await _done.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        await _channel.DisposeAsync();
        _stop.Dispose();
    }

    public ValueTask DisposeAsync() => DisposeAsync("stopped");
}
