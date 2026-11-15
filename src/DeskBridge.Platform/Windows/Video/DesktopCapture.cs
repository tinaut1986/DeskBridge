using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Captures one monitor with DXGI desktop duplication: each new frame is copied into a texture of our own
/// (BGRA, on the GPU), with no copy to the CPU. Works for physical and virtual (IddCx) displays alike.
/// The mouse pointer is not part of the image (it is sent separately).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopCapture : IDisposable
{
    private readonly VideoDevice _video;
    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;
    private bool _holding;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The latest frame (valid until the next <see cref="TryAcquire"/>).</summary>
    public ID3D11Texture2D Frame { get; }

    /// <summary>Where the pointer is on this monitor, and whether it is visible (from the last frame that moved it).</summary>
    public (int X, int Y, bool Visible) Pointer { get; private set; }

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
        _duplication = _output.DuplicateOutput(_video.Device);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the screen to change and copies it into <see cref="Frame"/>.
    /// False when nothing changed (the previous frame is still valid).
    /// </summary>
    public bool TryAcquire(int timeoutMs)
    {
        if (_holding)
        {
            _duplication!.ReleaseFrame();
            _holding = false;
        }

        var result = _duplication!.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            // Mode change, full-screen app, desktop switch (UAC, lock screen): start again.
            Thread.Sleep(50);
            try
            {
                Duplicate();
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
            }
            return false;
        }
        result.CheckError();
        _holding = true;

        if (info.LastMouseUpdateTime != 0)
            Pointer = (info.PointerPosition.Position.X, info.PointerPosition.Position.Y, info.PointerPosition.Visible);
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

    public void Dispose()
    {
        if (_holding)
            _duplication?.ReleaseFrame();
        _duplication?.Dispose();
        Frame.Dispose();
        _output.Dispose();
    }
}
