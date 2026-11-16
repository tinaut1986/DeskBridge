using System.Buffers.Binary;
using System.Runtime.Versioning;
using DeskBridge.Core.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DeskBridge.Platform.Windows.Audio;

/// <summary>
/// Plays the sound another machine sends (<see cref="AudioSender"/>) on this PC's default output, with a
/// small buffer: when more than <see cref="MaxBehind"/> piles up (the network hiccupped), it is dropped so the
/// sound never lags behind. Follows the default output when it changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioPlayer : IAsyncDisposable
{
    private static readonly TimeSpan MaxBehind = TimeSpan.FromMilliseconds(150);

    private readonly VideoChannel _channel;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BufferedWaveProvider? _buffer;
    private WasapiOut? _output;
    private string? _deviceId;
    private int _dropped;

    public Task Completion => _done.Task;

    public string RemoteMachine => _channel.RemoteMachine;

    public AudioPlayer(VideoChannel channel, Action<string> log)
    {
        _channel = channel;
        _log = log;
        _ = RunAsync();
        _ = WatchDeviceAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _channel.ReceiveAsync(_stop.Token);
                if (packet is not { } p || p.Type == VideoPacket.Stop)
                    break;
                switch (p.Type)
                {
                    case VideoPacket.AudioFormat:
                        int rate = BinaryPrimitives.ReadInt32LittleEndian(p.Body);
                        int channels = BinaryPrimitives.ReadInt16LittleEndian(p.Body.AsSpan(4));
                        Start(new WaveFormat(rate, 16, channels));
                        break;

                    case VideoPacket.Audio when _buffer is { } buffer:
                        if (buffer.BufferedDuration > MaxBehind)
                        {
                            buffer.ClearBuffer(); // catch up instead of playing late
                            _dropped++;
                        }
                        buffer.AddSamples(p.Body, 0, p.Body.Length);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[audio] sound from {_channel.RemoteMachine} stopped: {ex.Message}");
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

    private void StartOutput()
    {
        if (_buffer is not { } buffer)
            return;
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _deviceId = device.ID;
        var mix = device.AudioClient.MixFormat;

        // To the output's own rate and channels (float), which shared mode always accepts.
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != mix.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        if (samples.WaveFormat.Channels != mix.Channels)
        {
            var multiplexer = new MultiplexingSampleProvider([samples], mix.Channels);
            for (int c = 0; c < mix.Channels; c++)
                multiplexer.ConnectInputToOutput(c % samples.WaveFormat.Channels, c);
            samples = multiplexer;
        }

        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        output.Init(new SampleToWaveProvider(samples));
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

    /// <summary>Moves to the new default output when it changes, and reports dropped catch-ups now and then.</summary>
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
        try
        {
            await _channel.SendAsync(VideoPacket.Stop, default, System.Text.Encoding.UTF8.GetBytes(reason));
        }
        catch (Exception)
        {
        }
        _stop.Cancel();
        await _done.Task.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
        await _channel.DisposeAsync();
        _stop.Dispose();
    }

    public ValueTask DisposeAsync() => DisposeAsync("stopped");
}
