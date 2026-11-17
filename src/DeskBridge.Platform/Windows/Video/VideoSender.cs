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

    /// <summary>The capture waits at most this long for a new picture, so key frame requests and stops are seen.</summary>
    private const int MaxWaitMs = 50;

    /// <summary>
    /// Frames still encoded after the screen stops changing: the encoder gives back each frame one later, and a
    /// few more sharpen the picture. Short, or a blinking text cursor (every half second) would keep it busy.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(100);

    /// <summary>A still screen still sends one frame this often.</summary>
    private static readonly TimeSpan IdleFrameEvery = TimeSpan.FromSeconds(1);

    private readonly IVideoLink _channel;
    private readonly bool _followInputDesktop;
    private volatile bool _suspended, _resumed;

    /// <summary>Where the packets go (the network channel, or the pipe to a relay).</summary>
    public IVideoLink Link => _channel;

    public int Fps => _fps;

    public int BitrateMbps => _bitrateMbps;

    /// <summary>
    /// Sends nothing while set: the secure desktop's helper sends this display's picture meanwhile, through a relay
    /// on the same link. When cleared, the format goes again and a key frame follows (the receiver starts over).
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended && !value)
                _resumed = true;
            _suspended = value;
        }
    }
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

    /// <summary>The desktop position of the monitor sent (its top-left corner).</summary>
    public (int X, int Y) Monitor => (_monitorX, _monitorY);

    /// <summary>The size of the picture sent (0 until the capture starts).</summary>
    public (int Width, int Height) Size { get; private set; }

    /// <summary>Why the stream ended (null while it runs or after a normal stop).</summary>
    public string? Failure { get; private set; }

    public Task Completion { get; }

    /// <param name="monitorX">Desktop position of the monitor to send (its top-left corner).</param>
    /// <param name="followInputDesktop">
    /// Capture whichever desktop has the input (the secure desktop's helper, running as SYSTEM, which may open it).
    /// </param>
    public VideoSender(IVideoLink channel, int monitorX, int monitorY, int fps, int bitrateMbps, Action<string> log, bool followInputDesktop = false)
    {
        _channel = channel;
        _followInputDesktop = followInputDesktop;
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
                    if (GraphicsHealth.IsBroken(ex) && GraphicsHealth.Problem(_monitorX, _monitorY) is { } broken)
                    {
                        // The driver stopped working (e.g. it crashed): trying again every second only fills the log.
                        _log($"[video] {broken}");
                        Fail(broken);
                        break;
                    }
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
            // The capture on a device of its own (see DesktopCapture), on the same GPU.
            var again = DesktopCapture.FindOutput(_monitorX, _monitorY)
                ?? throw new InvalidOperationException($"No monitor at ({_monitorX}, {_monitorY}).");
            found.Output.Dispose();
            using var captureDevice = VideoDevice.Create(again.Adapter);
            // The thread moves to the desktop with the input first (it captures only the desktop it is on).
            if (_followInputDesktop)
                DeskBridge.Platform.Windows.Secure.InputDesktop.Follow();
            using var capture = DesktopCapture.Open(captureDevice, again.Output);
            if (_followInputDesktop)
                capture.BeforeDuplicate = () => DeskBridge.Platform.Windows.Secure.InputDesktop.Follow();
            var (shared, sharedLock) = capture.OpenFrame(video);
            using var sharedFrame = shared;
            using var sharedFrameLock = sharedLock;
            int w = capture.Width & ~1, h = capture.Height & ~1;
            Size = (capture.Width, capture.Height);
            using var toNv12 = new VideoConverter(video, capture.Width, capture.Height, w, h, _fps);
            using var encoder = new H264Encoder(video, w, h, _fps, _bitrateMbps, Enqueue);
            _encoder = encoder;
            _log($"[video] sending {w}x{h}@{_fps} at {_bitrateMbps} Mbps, {video.GpuName}, {encoder.Name} ({encoder.Settings})");

            var format = new VideoFormat(w, h, _fps, _bitrateMbps, video.GpuName, encoder.Name);
            _channel.SendAsync(VideoPacket.Format, JsonSerializer.SerializeToUtf8Bytes(format), default, _stop.Token).GetAwaiter().GetResult();

            // Woken by each new picture (or pointer move); at most one picture per frame time (a little less, for
            // timing jitter).
            var frameTime = TimeSpan.FromSeconds(1.0 / Math.Max(1, _fps));
            var frameGap = frameTime * 0.75;
            bool fresh = false;
            var lastPointer = TimeSpan.Zero;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _clock = clock;
            (int, int, bool) pointer = default;
            int pointerVersion = 0;
            string? paused = null;
            var nextReport = TimeSpan.FromSeconds(3);
            // The first picture after the capture starts always goes (the duplication delivers the whole screen).
            var lastChange = clock.Elapsed;
            var lastEncoded = TimeSpan.Zero;
            int encoded = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (clock.Elapsed >= nextReport)
                {
                    var latency = TakeLatency();
                    _log($"[video] encoder: {encoder.Stats}; {encoded / 10.0:F1} frames/s encoded (only when the screen changes)"
                         + (_encodeDelayCount > 0 ? $"; encoder holds a picture {_encodeDelaySum / _encodeDelayCount:F1} ms (worst {_encodeDelayMax:F0})" : "")
                         + (_dropped > 0 ? $"; {_dropped} frames dropped (network behind)" : "")
                         + (latency is { } l ? $"; capture to screen and back {l.Average:F0} ms (worst {l.Max:F0}, {l.Samples} samples)" : ""));
                    nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);
                    encoded = 0;
                    (_encodeDelaySum, _encodeDelayMax, _encodeDelayCount, _dropped) = (0, 0, 0, 0);
                }
                // Until the next picture, or until something is due: the next frame time for a picture that came too
                // soon or for sharpening, a frame to keep the stream alive, the next report.
                var t = clock.Elapsed;
                var wake = new[]
                {
                    fresh ? lastEncoded + frameGap : TimeSpan.MaxValue,
                    t - lastChange <= SettleTime ? lastEncoded + frameTime : TimeSpan.MaxValue,
                    lastEncoded + IdleFrameEvery,
                    nextReport,
                }.Min();
                int waitMs = _keyFrameWanted ? 0 : (int)Math.Clamp((wake - t).TotalMilliseconds, 0, MaxWaitMs);
                bool got = capture.TryAcquire(waitMs); // on the capture's own device: waiting here blocks nothing else
                if (!got && capture.Paused is not null)
                    _stop.Token.WaitHandle.WaitOne(Math.Max(waitMs, 10)); // lost (lock screen, UAC...): no busy loop
                if (got)
                {
                    lastChange = clock.Elapsed;
                    fresh = true;
                }
                if (_suspended)
                {
                    _stop.Token.WaitHandle.WaitOne(50);
                    continue;
                }
                if (_resumed)
                {
                    // Back from the helper's stream: the receiver starts over with this one.
                    _resumed = false;
                    _channel.SendAsync(VideoPacket.Format, JsonSerializer.SerializeToUtf8Bytes(format), default, _stop.Token).GetAwaiter().GetResult();
                    _keyFrameWanted = true;
                    pointerVersion = -1;
                    pointer = default;
                }
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
                // At most once per frame time (the screen is looked at more often than that): a stream of small pointer
                // packets made the receiver take each picture for one with the next already waiting, and skip it.
                if (capture.Pointer != pointer
                    && (capture.Pointer.Visible != pointer.Item3 || clock.Elapsed - lastPointer >= frameGap))
                {
                    lastPointer = clock.Elapsed;
                    pointer = capture.Pointer;
                    var (px, py, visible) = capture.Pointer;
                    _ = _channel.SendAsync(VideoPacket.Pointer, VideoChannel.PointerBody(px, py, visible), default, _stop.Token);
                }
                var now = clock.Elapsed;
                bool keyFrame = _keyFrameWanted;
                bool due = now - lastEncoded >= frameGap;
                bool wanted = keyFrame
                              || (fresh && due) // a new picture
                              || (now - lastEncoded >= frameTime && now - lastChange <= SettleTime) // sharpening the last one
                              || now - lastEncoded >= IdleFrameEvery; // keeping the stream alive
                if (!wanted)
                    continue;
                fresh = false;
                if (keyFrame)
                {
                    _keyFrameWanted = false;
                    encoder.RequestKeyFrame();
                }
                try
                {
                    sharedFrameLock.AcquireSync(0, 100);
                }
                catch (SharpGen.Runtime.SharpGenException)
                {
                    continue;
                }
                try
                {
                    toNv12.Convert(sharedFrame, 0, encoder.NextInput());
                }
                finally
                {
                    sharedFrameLock.ReleaseSync(0);
                }
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

    private double _encodeDelaySum, _encodeDelayMax;
    private int _encodeDelayCount, _dropped;

    private void Enqueue(EncodedFrame frame)
    {
        if (_clock is { } clock)
        {
            double ms = TimeSpan.FromTicks(clock.Elapsed.Ticks - frame.Time).TotalMilliseconds;
            _encodeDelaySum += ms;
            _encodeDelayMax = Math.Max(_encodeDelayMax, ms);
            _encodeDelayCount++;
        }
        if (_queue.Count >= MaxQueued && !frame.KeyFrame)
        {
            // The network is behind: drop what is waiting and start again from a key frame.
            while (_queue.TryTake(out _))
                _dropped++;
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
                if (!_suspended) // frames queued before the helper took over are not sent after its own
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
