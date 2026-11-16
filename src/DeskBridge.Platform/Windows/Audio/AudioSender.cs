using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Threading.Channels;
using DeskBridge.Core.Audio;
using DeskBridge.Core.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskBridge.Platform.Windows.Audio;

/// <summary>What an <see cref="AudioSender"/> captures.</summary>
public enum AudioSource
{
    /// <summary>Everything this PC plays (loopback of the default output), which is muted here meanwhile.</summary>
    Output,

    /// <summary>This PC's default microphone (the one for calls).</summary>
    Microphone,
}

/// <summary>
/// Sends this PC's sound to another machine: what it plays (<see cref="AudioSource.Output"/>: notifications,
/// builds, music…, its own output muted meanwhile so it is heard only there) or its microphone. Opus in 10 ms
/// frames (PCM at rates Opus does not take). Follows the default device when it changes, and unmutes the
/// output when it stops.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioSender : IAsyncDisposable, IAudioStream
{
    private readonly VideoChannel _channel;
    private readonly AudioSource _source;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private bool _mutedByUs;
    private string? _deviceId;
    private WaveFormat? _format;
    private OpusEncoder? _opus;

    // For the 10-second report: sound sent and its loudest sample.
    private long _bytesSent;
    private float _peak;

    /// <summary>Ends when the stream stops (either side, or an error: <see cref="Failure"/>).</summary>
    public Task Completion => _done.Task;

    public string? Failure { get; private set; }

    /// <summary>Why the other side stopped the stream, if it said so.</summary>
    public string? StopReason { get; private set; }

    private string What => _source == AudioSource.Output ? "this PC's sound" : "the microphone";

    public AudioSender(VideoChannel channel, AudioSource source, Action<string> log)
    {
        _channel = channel;
        _source = source;
        _log = log;
        _ = RunAsync();
    }

    private DataFlow Flow => _source == AudioSource.Output ? DataFlow.Render : DataFlow.Capture;
    private Role Role => _source == AudioSource.Output ? Role.Multimedia : Role.Communications;

    private async Task RunAsync()
    {
        var sending = SendLoopAsync();
        var receiving = ReceiveLoopAsync();
        try
        {
            StartCapture();
            var report = DateTime.UtcNow;
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(1000, _stop.Token);
                // The default device changed (headphones, another microphone): capture that one instead.
                string? current = DefaultDeviceId();
                if (current is not null && current != _deviceId)
                {
                    _log($"[audio] the default {(_source == AudioSource.Output ? "output" : "microphone")} changed: capturing the new one");
                    StopCapture();
                    StartCapture();
                }
                if ((DateTime.UtcNow - report).TotalSeconds >= 10)
                {
                    long bytes = Interlocked.Exchange(ref _bytesSent, 0);
                    float peak;
                    lock (_gate)
                    {
                        peak = _peak;
                        _peak = 0;
                    }
                    if (bytes > 0)
                        _log($"[audio] {What}: {bytes * 8 / 1000 / (DateTime.UtcNow - report).TotalSeconds:F0} kbps, loudest {Decibels(peak)}"
                             + (_mutedByUs ? " (this PC's output muted meanwhile)" : ""));
                    report = DateTime.UtcNow;
                }
                if (receiving.IsCompleted || sending.IsCompleted)
                    break;
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
            StopCapture();
            _stop.Cancel();
            _queue.Writer.TryComplete();
            try
            {
                await Task.WhenAll(sending, receiving);
            }
            catch (Exception)
            {
            }
            _done.TrySetResult();
        }
    }

    private static string Decibels(float peak) => peak <= 0.00001f ? "silence" : $"{20 * Math.Log10(peak):F0} dB";

    private string? DefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(Flow, Role);
            return device.ID;
        }
        catch (Exception)
        {
            return null; // no such device right now
        }
    }

    private void StartCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(Flow, Role);
        _deviceId = _device.ID;
        WasapiCapture capture = _source == AudioSource.Output ? new WasapiLoopbackCapture(_device) : new WasapiCapture(_device, true, 20);
        _format = capture.WaveFormat;
        int channels = Math.Min(2, _format.Channels);
        bool voice = _source == AudioSource.Microphone;
        _opus = OpusEncoder.Supports(_format.SampleRate)
            ? new OpusEncoder(_format.SampleRate, channels, (voice ? 48_000 : 128_000) * channels / 2, voice)
            : null;
        var header = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(header, _format.SampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(4), (short)channels);
        header[6] = (byte)(_opus is null ? AudioCodec.Pcm : AudioCodec.Opus);
        _queue.Writer.TryWrite([(byte)VideoPacket.AudioFormat, .. header]);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is { } ex && !_stop.IsCancellationRequested)
                _log($"[audio] capture stopped: {ex.Message}");
        };
        capture.StartRecording();
        _capture = capture;
        _log($"[audio] capturing '{_device.FriendlyName}' ({_format.SampleRate} Hz, {_format.Channels} channels, "
             + (_opus is null ? "sent as PCM" : "sent as Opus") + ")");
        if (_source == AudioSource.Output)
        {
            try
            {
                if (!_device.AudioEndpointVolume.Mute)
                {
                    MutedDevices.Remember(_deviceId);
                    _device.AudioEndpointVolume.Mute = true;
                    _mutedByUs = true;
                }
            }
            catch (Exception ex)
            {
                _log($"[audio] could not mute this PC's output: {ex.Message}");
            }
        }
    }

    private void StopCapture()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
            }
            capture.Dispose();
        }
        if (_mutedByUs && _device is not null)
        {
            try
            {
                _device.AudioEndpointVolume.Mute = false;
            }
            catch (Exception)
            {
            }
            MutedDevices.Forget(_deviceId);
            _mutedByUs = false;
        }
        _device?.Dispose();
        _device = null;
    }

    /// <summary>On the capture thread: samples to float stereo (or mono), encoded and queued for sending.</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_format is not { } format || e.BytesRecorded == 0)
            return;
        int inChannels = format.Channels, outChannels = Math.Min(2, inChannels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * inChannels);
        var samples = new float[frames * outChannels];
        float peak = 0;
        var source = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < outChannels; c++)
            {
                int at = (f * inChannels + c) * bytesPerSample;
                float sample = bytesPerSample == 4
                    ? BitConverter.ToSingle(source.Slice(at, 4))
                    : BinaryPrimitives.ReadInt16LittleEndian(source.Slice(at, 2)) / 32768f;
                peak = Math.Max(peak, Math.Abs(sample));
                samples[f * outChannels + c] = sample;
            }
        }
        lock (_gate)
            _peak = Math.Max(_peak, peak);

        if (_opus is { } opus)
        {
            foreach (var frame in opus.Add(samples))
                _queue.Writer.TryWrite([(byte)VideoPacket.Audio, .. frame]);
            return;
        }
        var packet = new byte[1 + samples.Length * 2];
        packet[0] = (byte)VideoPacket.Audio;
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(1 + i * 2), (short)Math.Clamp((int)(samples[i] * 32767f), short.MinValue, short.MaxValue));
        _queue.Writer.TryWrite(packet);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                await _channel.SendAsync((VideoPacket)packet[0], default, packet.AsMemory(1), _stop.Token);
                Interlocked.Add(ref _bytesSent, packet.Length - 1);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failure ??= ex.Message;
            _stop.Cancel();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = await _channel.ReceiveAsync(_stop.Token);
                if (packet is not { } p)
                    break;
                if (p.Type == VideoPacket.Stop)
                {
                    StopReason = System.Text.Encoding.UTF8.GetString(p.Body);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failure ??= ex.Message;
        }
        _stop.Cancel();
    }

    /// <summary>Stops sending; <paramref name="reason"/> tells the other side why.</summary>
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

/// <summary>
/// Outputs muted by <see cref="AudioSender"/>, kept in a file so they are unmuted at the next start if the app
/// ended without doing it (a crash, a forced close).
/// </summary>
[SupportedOSPlatform("windows")]
public static class MutedDevices
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "muted-outputs.txt");

    public static void Remember(string? id)
    {
        if (id is null)
            return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var ids = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToHashSet() : [];
                if (ids.Add(id))
                    File.WriteAllLines(FilePath, ids);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Forget(string? id)
    {
        lock (Gate)
        {
            try
            {
                if (id is null || !File.Exists(FilePath))
                    return;
                var ids = File.ReadAllLines(FilePath).Where(l => l != id).ToList();
                if (ids.Count == 0)
                    File.Delete(FilePath);
                else
                    File.WriteAllLines(FilePath, ids);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>At start: unmutes whatever an earlier run left muted.</summary>
    public static void RestoreLeftOver()
    {
        string[] ids;
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                ids = File.ReadAllLines(FilePath);
                File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in ids.Where(i => i.Length > 0))
            {
                try
                {
                    using var device = enumerator.GetDevice(id);
                    device.AudioEndpointVolume.Mute = false;
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
