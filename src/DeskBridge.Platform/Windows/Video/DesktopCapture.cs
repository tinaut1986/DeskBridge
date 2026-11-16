using System.Runtime.Versioning;
using DeskBridge.Core.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Captures one monitor with DXGI desktop duplication: each new frame is copied into a texture of our own
/// (BGRA, on the GPU), with no copy to the CPU. Works for physical and virtual (IddCx) displays alike.
/// The mouse pointer is not part of the image (it is sent separately).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class DesktopCapture : IDisposable
{
    private readonly VideoDevice _video;
    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;
    private bool _holding;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The latest frame (valid until the next <see cref="TryAcquire"/>).</summary>
    public ID3D11Texture2D Frame { get; }

    /// <summary>
    /// Where the pointer's picture goes on this monitor (its top-left corner), and whether it is visible
    /// (from the last frame that moved it).
    /// </summary>
    public (int X, int Y, bool Visible) Pointer { get; private set; }

    /// <summary>The pointer's picture; <see cref="PointerVersion"/> changes with it.</summary>
    public PointerImage? PointerImage { get; private set; }

    public int PointerVersion { get; private set; }

    private byte[] _shapeBuffer = new byte[64 * 1024];

    private DesktopCapture(VideoDevice video, IDXGIOutput1 output, int width, int height)
    {
        _video = video;
        _output = output;
        Width = width;
        Height = height;
        Frame = video.CreateTexture(width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
    }

    /// <summary>The GPU and output of the monitor whose desktop rectangle starts at (x, y).</summary>
    public static (IDXGIAdapter1 Adapter, IDXGIOutput1 Output)? FindOutput(int x, int y)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var r = output.Description.DesktopCoordinates;
                if (r.Left == x && r.Top == y)
                    return (adapter, output.QueryInterface<IDXGIOutput1>());
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }

    /// <summary>Starts capturing the monitor at (x, y) of the desktop, on the device of the GPU that drives it.</summary>
    public static DesktopCapture Open(VideoDevice video, IDXGIOutput1 output)
    {
        var r = output.Description.DesktopCoordinates;
        var capture = new DesktopCapture(video, output, r.Right - r.Left, r.Bottom - r.Top);
        capture.Duplicate();
        return capture;
    }

    private void Duplicate()
    {
        _duplication?.Dispose();
        _duplication = null;
        _duplication = _output.DuplicateOutput(_video.Device);
    }

    private long _retryAt;

    /// <summary>
    /// The monitor changed size (or went away) since the capture started: the whole pipeline has to be built
    /// again for the new picture.
    /// </summary>
    public bool Changed { get; private set; }

    /// <summary>Why the capture is paused (null while it works), for logs.</summary>
    public string? Paused { get; private set; }

    /// <summary>Drops the duplication after an error; it is created again shortly (a lock screen, a mode change...).</summary>
    private void Lose(string why)
    {
        _holding = false;
        try
        {
            _duplication?.Dispose();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }
        _duplication = null;
        Paused = why;
        _retryAt = Environment.TickCount64 + 250;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the screen to change and copies it into <see cref="Frame"/>.
    /// False when nothing changed (the previous frame is still valid).
    /// </summary>
    public bool TryAcquire(int timeoutMs)
    {
        if (_duplication is null)
        {
            // Lost (mode change, full-screen app, desktop switch such as UAC or the lock screen): try again now and then.
            if (Environment.TickCount64 < _retryAt)
                return false;
            try
            {
                var r = _output.Description.DesktopCoordinates;
                if (r.Right - r.Left != Width || r.Bottom - r.Top != Height)
                {
                    Changed = true;
                    return false;
                }
                Duplicate();
                Paused = null;
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                Lose(ex.ResultCode.ApiCode ?? ex.Message);
                _retryAt = Environment.TickCount64 + 1000;
                return false;
            }
        }

        if (_holding)
        {
            _holding = false;
            if (_duplication!.ReleaseFrame().Failure)
            {
                Lose("release failed");
                return false;
            }
        }

        var result = _duplication!.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        if (result.Failure)
        {
            resource?.Dispose();
            Lose(result.ApiCode ?? result.ToString());
            return false;
        }
        _holding = true;

        if (info.LastMouseUpdateTime != 0)
            Pointer = (info.PointerPosition.Position.X, info.PointerPosition.Position.Y, info.PointerPosition.Visible);
        if (info.PointerShapeBufferSize > 0)
            ReadPointerShape(info.PointerShapeBufferSize);
        bool image = info.LastPresentTime != 0;
        using (resource)
        {
            if (image)
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                _video.Context.CopyResource(Frame, texture);
            }
        }
        return image;
    }

    private unsafe void ReadPointerShape(uint size)
    {
        if (_shapeBuffer.Length < size)
            _shapeBuffer = new byte[size];
        fixed (byte* buffer = _shapeBuffer)
        {
            if (_duplication!.GetFramePointerShape((uint)_shapeBuffer.Length, (nint)buffer, out uint used, out var shape).Failure)
                return;
            var image = PointerShapes.Convert((int)shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch, _shapeBuffer.AsSpan(0, (int)used));
            if (image is not null)
            {
                PointerImage = image;
                PointerVersion++;
            }
        }
    }

    public void Dispose()
    {
        if (_holding)
            _duplication?.ReleaseFrame();
        _duplication?.Dispose();
        Frame.Dispose();
        _output.Dispose();
    }
}
