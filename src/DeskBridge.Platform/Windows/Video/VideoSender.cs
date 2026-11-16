using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using DeskBridge.Core.Video;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Sends one monitor over a video connection: captures it at a steady frame rate, encodes it on the GPU and
/// sends the frames in which the screen changed. A still screen sends nothing (the pointer goes on its own),
/// apart from a few frames after the last change, which let the encoder sharpen the picture it left (and push
/// out any frame it still holds), and one per second to keep the stream alive. If the network falls behind,
/// queued frames are dropped and a key frame follows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoSender : IAsyncDisposable
{
    private const int MaxQueued = 3;

    /// <summary>
    /// Frames still encoded after the screen stops changing: the encoder gives back each frame one later, and a
    /// few more sharpen the picture. Short, or a blinking text cursor (every half second) would keep it busy.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(100);

    /// <summary>A still screen still sends one frame this often.</summary>
    private static readonly TimeSpan IdleFrameEvery = TimeSpan.FromSeconds(1);

    private readonly VideoChannel _channel;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly BlockingCollection<EncodedFrame> _queue = new(boundedCapacity: 64);
    private readonly Thread _captureThread;
    private readonly Task _sendLoop, _receiveLoop;
    private readonly int _monitorX, _monitorY, _fps, _bitrateMbps;
    private H264Encoder? _encoder;
    private volatile bool _keyFrameWanted;
    private System.Diagnostics.Stopwatch? _clock;
    private readonly object _latencyLock = new();
    private double _latencySum, _latencyMax;
    private int _latencyCount;

    /// <summary>Capture to display and back, average and worst since the last call (ms), or null without data.</summary>
    public (double Average, double Max, int Samples)? TakeLatency()
    {
        lock (_latencyLock)
        {
            if (_latencyCount == 0)
                return null;
            var result = (_latencySum / _latencyCount, _latencyMax, _latencyCount);
            (_latencySum, _latencyMax, _latencyCount) = (0, 0, 0);
            return result;
        }
    }

    /// <summary>Why the stream ended (null while it runs or after a normal stop).</summary>
    public string? Failure { get; private set; }

    public Task Completion { get; }

    /// <param name="monitorX">Desktop position of the monitor to send (its top-left corner).</param>
    public VideoSender(VideoChannel channel, int monitorX, int monitorY, int fps, int bitrateMbps, Action<string> log)
    {
        _channel = channel;
        _monitorX = monitorX;
        _monitorY = monitorY;
        _fps = fps;
        _bitrateMbps = bitrateMbps;
        _log = log;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Video capture", Priority = ThreadPriority.AboveNormal };
        _captureThread.Start();
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        Completion = Task.WhenAll(_sendLoop, _receiveLoop);
    }

    private void Fail(string reason)
    {
        Failure ??= reason;
        _stop.Cancel();
    }

    private void CaptureLoop()
    {
        Priority.MultimediaThread();
        // The pipeline is built again when the monitor changes size or the GPU is reset; a stream only ends when
        // that keeps failing.
        var failures = new Queue<DateTime>();
        try
        {
            Loop();
        }
        finally
        {
            _queue.CompleteAdding();
        }

        void Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    RunPipeline();
                    return; // stopped
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    failures.Enqueue(DateTime.UtcNow);
                    while (failures.Count > 0 && DateTime.UtcNow - failures.Peek() > TimeSpan.FromMinutes(1))
                        failures.Dequeue();
                    if (ex is not CaptureChangedException)
                        _log($"[video] capture/encode failed, starting again: {ex.Message}" + (_encoder is { } e ? $" ({e.Stats})" : ""));
                    if (failures.Count >= 5)
                    {
                        Fail(ex.Message);
                        break;
                    }
                    _stop.Token.WaitHandle.WaitOne(1000);
                }
        }
        }
    }

    private sealed class CaptureChangedException(string message) : Exception(message);

    private void RunPipeline()
    {
        try
        {
            var found = DesktopCapture.FindOutput(_monitorX, _monitorY)
                ?? throw new InvalidOperationException($"No monitor at ({_monitorX}, {_monitorY}).");
            using var video = VideoDevice.Create(found.Adapter);
            using var capture = DesktopCapture.Open(video, found.Output);
            int w = capture.Width & ~1, h = capture.Height & ~1;
            using var toNv12 = new VideoConverter(video, capture.Width, capture.Height, w, h, _fps);
            using var encoder = new H264Encoder(video, w, h, _fps, _bitrateMbps, Enqueue);
            _encoder = encoder;
            _log($"[video] sending {w}x{h}@{_fps} at {_bitrateMbps} Mbps, {video.GpuName}, {encoder.Name} ({encoder.Settings})");

            var format = new VideoFormat(w, h, _fps, _bitrateMbps, video.GpuName, encoder.Name);
            _channel.SendAsync(VideoPacket.Format, JsonSerializer.SerializeToUtf8Bytes(format), default, _stop.Token).GetAwaiter().GetResult();

            using var pacer = new FramePacer(_fps);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _clock = clock;
            (int, int, bool) pointer = default;
            int pointerVersion = 0;
            string? paused = null;
            var nextReport = TimeSpan.FromSeconds(3);
            // The first picture after the capture starts always goes (the duplication delivers the whole screen).
            var lastChange = clock.Elapsed;
            var lastEncoded = TimeSpan.Zero;
            int encoded = 0, ticks = 0;
            while (!_stop.IsCancellationRequested)
            {
                pacer.Wait();
                if (clock.Elapsed >= nextReport)
                {
                    var latency = TakeLatency();
                    _log($"[video] encoder: {encoder.Stats}; {encoded} of {ticks} frames encoded (the rest unchanged)"
                         + (latency is { } l ? $"; capture to screen and back {l.Average:F0} ms (worst {l.Max:F0}, {l.Samples} samples)" : ""));
                    nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);
                    (encoded, ticks) = (0, 0);
                }
                ticks++;
                if (capture.TryAcquire(0)) // never wait inside it: it holds the device lock
                    lastChange = clock.Elapsed;
                if (capture.Changed)
                {
                    _log("[video] the display changed size: starting the stream again");
                    throw new CaptureChangedException("display changed");
                }
                if (capture.Paused != paused)
                {
                    paused = capture.Paused;
                    _log(paused is null ? "[video] capture resumed" : $"[video] capture paused ({paused}): the last picture is sent meanwhile");
                }
                if (capture.PointerVersion != pointerVersion && capture.PointerImage is { } image)
                {
                    pointerVersion = capture.PointerVersion;
                    _ = _channel.SendAsync(VideoPacket.PointerShape, image.ToBody(), default, _stop.Token);
                }
                if (capture.Pointer != pointer)
                {
                    pointer = capture.Pointer;
                    var (px, py, visible) = capture.Pointer;
                    _ = _channel.SendAsync(VideoPacket.Pointer, VideoChannel.PointerBody(px, py, visible), default, _stop.Token);
                }
                var now = clock.Elapsed;
                bool keyFrame = _keyFrameWanted;
                if (!keyFrame && now - lastChange > SettleTime && now - lastEncoded < IdleFrameEvery)
                    continue; // nothing new on the screen
                if (keyFrame)
                {
                    _keyFrameWanted = false;
                    encoder.RequestKeyFrame();
                }
                toNv12.Convert(capture.Frame, 0, encoder.NextInput());
                if (encoder.Encode(now.Ticks, TimeSpan.FromMilliseconds(200)))
                {
                    lastEncoded = now;
                    encoded++;
                }
            }
        }
        finally
        {
            _encoder = null;
        }
    }

    private void Enqueue(EncodedFrame frame)
    {
        if (_queue.Count >= MaxQueued && !frame.KeyFrame)
        {
            // The network is behind: drop what is waiting and start again from a key frame.
            while (_queue.TryTake(out _)) { }
            _keyFrameWanted = true;
            return;
        }
        _queue.TryAdd(frame);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            foreach (var frame in _queue.GetConsumingEnumerable(_stop.Token))
                await _channel.SendAsync(VideoPacket.Frame, VideoChannel.FrameHeader(frame.Time, frame.KeyFrame), frame.Data, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Fail(ex.Message);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && await _channel.ReceiveAsync(_stop.Token) is { } packet)
            {
                if (packet.Type == VideoPacket.KeyFrameRequest)
                    _keyFrameWanted = true;
                else if (packet.Type == VideoPacket.Shown && packet.Body.Length >= 8 && _clock is { } clock)
                {
                    double ms = TimeSpan.FromTicks(clock.Elapsed.Ticks - BitConverter.ToInt64(packet.Body)).TotalMilliseconds;
                    lock (_latencyLock)
                    {
                        _latencySum += ms;
                        _latencyCount++;
                        _latencyMax = Math.Max(_latencyMax, ms);
                    }
                }
                else if (packet.Type == VideoPacket.Stop)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Fail(ex.Message);
        }
        _stop.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _captureThread.Join(2000);
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
        await _channel.DisposeAsync();
        _queue.Dispose();
        _stop.Dispose();
    }
}
