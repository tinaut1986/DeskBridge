using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Checks the whole video path on one PC without the network: captures a monitor, encodes it on the GPU,
/// decodes it again and saves the last picture, measuring frame rate, size and latency.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LoopbackTest
{
    public sealed record Result(string Gpu, string Encoder, int Width, int Height, int Captured, int Encoded, int Decoded,
        double Seconds, double AverageKiB, double Mbps, double EncodeMs, double DecodeMs, int KeyFrames, string? Picture);

    public static Result Run(int monitorX, int monitorY, double seconds, int fps, int bitrateMbps, string? picturePath, Action<string>? log = null)
    {
        var found = DesktopCapture.FindOutput(monitorX, monitorY)
            ?? throw new InvalidOperationException($"No monitor starts at ({monitorX}, {monitorY}).");
        using var video = VideoDevice.Create(found.Adapter);
        using var capture = DesktopCapture.Open(video, found.Output);
        int w = capture.Width & ~1, h = capture.Height & ~1;
        log?.Invoke($"GPU: {video.GpuName}, monitor {capture.Width}x{capture.Height}");

        var encoded = new BlockingCollection<(EncodedFrame Frame, long OutTicks)>();
        var started = new ConcurrentDictionary<long, long>();
        using var toNv12 = new VideoConverter(video, capture.Width, capture.Height, w, h, fps);
        using var encoder = new H264Encoder(video, w, h, fps, bitrateMbps, f => encoded.Add((f, Stopwatch.GetTimestamp())));
        log?.Invoke($"Encoder: {encoder.Name} ({encoder.Settings})");

        using var decoder = new H264Decoder(video);
        ID3D11Texture2D? picture = null;
        VideoConverter? toBgra = null;
        int decodedCount = 0, keyFrames = 0;
        long bytes = 0;
        double encodeMs = 0, decodeMs = 0;
        var decodeThread = new Thread(() =>
        {
            foreach (var (frame, outTicks) in encoded.GetConsumingEnumerable())
            {
                bytes += frame.Data.Length;
                if (frame.KeyFrame) keyFrames++;
                if (started.TryRemove(frame.Time, out long inTicks))
                    encodeMs += Stopwatch.GetElapsedTime(inTicks, outTicks).TotalMilliseconds;
                long t0 = Stopwatch.GetTimestamp();
                decoder.Decode(frame.Data, frame.Time, (texture, slice, width, height) =>
                {
                    decodedCount++;
                    if (picturePath is null)
                        return;
                    toBgra ??= new VideoConverter(video, width, height, width, height, fps);
                    picture ??= video.CreateTexture(width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget);
                    toBgra.Convert(texture, slice, picture);
                });
                decodeMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
        }) { IsBackground = true };
        decodeThread.Start();

        int captured = 0, sent = 0;
        double waitMs = 0;
        var clock = Stopwatch.StartNew();
        var frameTime = TimeSpan.FromSeconds(1.0 / fps);
        var next = TimeSpan.Zero;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            // Constant frame rate: a new picture when the screen changed, the last one again otherwise.
            // Never wait inside AcquireNextFrame: it holds the shared device's lock, stalling the encoder.
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.FromMilliseconds(1.5))
            {
                Thread.Sleep(1);
                continue;
            }
            if (capture.TryAcquire(0))
                captured++;
            while (clock.Elapsed < next)
                Thread.SpinWait(50);
            next += frameTime;
            if (clock.Elapsed > next)
                next = clock.Elapsed + frameTime;
            toNv12.Convert(capture.Frame, 0, encoder.NextInput());
            long time = clock.Elapsed.Ticks;
            long before = Stopwatch.GetTimestamp();
            bool ok = encoder.Encode(time, TimeSpan.FromMilliseconds(100));
            started[time] = Stopwatch.GetTimestamp();
            waitMs += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            if (ok)
                sent++;
        }
        Thread.Sleep(200);
        log?.Invoke($"Waiting for the encoder to take a frame: {waitMs / Math.Max(1, sent):F1} ms");
        encoded.CompleteAdding();
        decodeThread.Join(5000);

        string? saved = null;
        if (picturePath is not null && picture is not null)
        {
            SaveBmp(video, picture, picturePath);
            saved = picturePath;
        }
        picture?.Dispose();
        toBgra?.Dispose();

        int count = Math.Max(1, sent);
        return new Result(video.GpuName, encoder.Name, w, h, captured, sent, decodedCount, clock.Elapsed.TotalSeconds,
            bytes / 1024.0 / count, bytes * 8 / 1e6 / clock.Elapsed.TotalSeconds, encodeMs / count, decodeMs / Math.Max(1, decodedCount),
            keyFrames, saved);
    }

    /// <summary>Saves a BGRA texture as a 32-bit BMP (a check picture, no imaging library needed).</summary>
    public static void SaveBmp(VideoDevice video, ID3D11Texture2D texture, string path)
    {
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = video.Device.CreateTexture2D(desc);
        video.Context.CopyResource(staging, texture);
        var map = video.Context.Map(staging, 0, MapMode.Read);
        try
        {
            int w = (int)desc.Width, h = (int)desc.Height;
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            writer.Write((ushort)0x4D42);
            writer.Write(54 + w * h * 4);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(w);
            writer.Write(-h); // top-down
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(w * h * 4);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally
        {
            video.Context.Unmap(staging, 0);
        }
    }
}
