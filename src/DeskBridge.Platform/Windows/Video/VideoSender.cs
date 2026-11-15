using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using DeskBridge.Core.Video;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Sends one monitor over a video connection: captures it at a steady frame rate, encodes it on the GPU and
/// sends every frame (the last picture again when nothing changed, so the stream never stalls; such frames
/// cost almost nothing). If the network falls behind, queued frames are dropped and a key frame follows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoSender : IAsyncDisposable
{
    private const int MaxQueued = 6;

    private readonly VideoChannel _channel;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly BlockingCollection<EncodedFrame> _queue = new(boundedCapacity: 64);
    private readonly Thread _captureThread;
    private readonly Task _sendLoop, _receiveLoop;
    private readonly int _monitorX, _monitorY, _fps, _bitrateMbps;
    private H264Encoder? _encoder;
    private volatile bool _keyFrameWanted;

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
            (int, int, bool) pointer = default;
            while (!_stop.IsCancellationRequested)
            {
                pacer.Wait();
                capture.TryAcquire(0); // never wait inside it: it holds the device lock
                if (capture.Pointer != pointer)
                {
                    pointer = capture.Pointer;
                    var (px, py, visible) = capture.Pointer;
                    _ = _channel.SendAsync(VideoPacket.Pointer, VideoChannel.PointerBody(px, py, visible), default, _stop.Token);
                }
                if (_keyFrameWanted)
                {
                    _keyFrameWanted = false;
                    encoder.RequestKeyFrame();
                }
                toNv12.Convert(capture.Frame, 0, encoder.NextInput());
                encoder.Encode(clock.Elapsed.Ticks, TimeSpan.FromMilliseconds(200));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[video] capture/encode failed: {ex.Message}");
            Fail(ex.Message);
        }
        finally
        {
            _encoder = null;
            _queue.CompleteAdding();
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
