using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using DeskBridge.Core.Video;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Shows a stream from a video connection: decodes each frame on the GPU and draws it in a
/// <see cref="VideoWindow"/> (full screen on a monitor, or a test window). Asks for a key frame whenever
/// the decoder starts or fails.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoReceiver : IAsyncDisposable
{
    private readonly VideoChannel _channel;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly (int X, int Y, int W, int H)? _monitor;
    private readonly string? _title;

    public Task Completion => _done.Task;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Frames shown and bytes received, for the status line.</summary>
    public (int Frames, long Bytes, VideoFormat? Format) Stats => (_frames, _bytes, _format);

    private int _frames;
    private long _bytes;
    private VideoFormat? _format;

    /// <param name="title">Window title; null decodes without showing anything (tests).</param>
    /// <param name="monitor">Full screen on this monitor rectangle; null for a normal window.</param>
    public VideoReceiver(VideoChannel channel, string? title, (int X, int Y, int W, int H)? monitor, Action<string> log)
    {
        _channel = channel;
        _title = title;
        _monitor = monitor;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Video receiver", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        VideoDevice? video = null;
        VideoWindow? window = null;
        H264Decoder? decoder = null;
        var report = Stopwatch.StartNew();
        int reportFrames = 0;
        long reportBytes = 0;
        try
        {
            // Displayed on the GPU that drives the target monitor (or the first one for a test window).
            var adapter = _monitor is { } m ? DesktopCapture.FindOutput(m.X, m.Y) is { } o ? Take(o) : null : null;
            video = VideoDevice.Create(adapter);
            if (_title is not null)
            {
                window = new VideoWindow(video, _title, _monitor);
                window.Closed += () => _stop.Cancel();
            }
            _log($"[video] receiving from {_channel.RemoteMachine} on {video.GpuName}");

            while (!_stop.IsCancellationRequested)
            {
                var packet = _channel.ReceiveAsync(_stop.Token).GetAwaiter().GetResult();
                if (packet is not { } p || p.Type == VideoPacket.Stop)
                    break;
                switch (p.Type)
                {
                    case VideoPacket.Format:
                        _format = JsonSerializer.Deserialize<VideoFormat>(p.Body);
                        _log($"[video] {_channel.RemoteMachine}: {_format?.Width}x{_format?.Height}@{_format?.Fps}, {_format?.Gpu}, {_format?.Encoder}");
                        decoder?.Dispose();
                        decoder = new H264Decoder(video);
                        break;

                    case VideoPacket.Frame when decoder is not null:
                        var (time, _, data) = VideoChannel.ParseFrame(p.Body);
                        _bytes += data.Length;
                        reportBytes += data.Length;
                        try
                        {
                            decoder.Decode(data.Span, time, (texture, slice, w, h) =>
                            {
                                window?.Present(texture, slice, w, h);
                                _frames++;
                                reportFrames++;
                            });
                        }
                        catch (SharpGen.Runtime.SharpGenException ex)
                        {
                            _log($"[video] decoder error, restarting: {ex.Message}");
                            decoder.Dispose();
                            decoder = new H264Decoder(video);
                            _ = _channel.SendAsync(VideoPacket.KeyFrameRequest);
                        }
                        break;
                }

                if (report.Elapsed.TotalSeconds >= 10)
                {
                    _log($"[video] {_channel.RemoteMachine}: {reportFrames / report.Elapsed.TotalSeconds:F1} fps, {reportBytes * 8 / 1e6 / report.Elapsed.TotalSeconds:F1} Mbps");
                    report.Restart();
                    reportFrames = 0;
                    reportBytes = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[video] receiving from {_channel.RemoteMachine} failed: {ex.Message}");
        }
        finally
        {
            decoder?.Dispose();
            window?.Dispose();
            video?.Dispose();
            _done.TrySetResult();
        }
    }

    private static Vortice.DXGI.IDXGIAdapter1 Take((Vortice.DXGI.IDXGIAdapter1 Adapter, Vortice.DXGI.IDXGIOutput1 Output) found)
    {
        found.Output.Dispose();
        return found.Adapter;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.SendAsync(VideoPacket.Stop);
        }
        catch (Exception)
        {
        }
        _stop.Cancel();
        await _channel.DisposeAsync(); // unblocks the receive
        _thread.Join(3000);
        _stop.Dispose();
    }
}
