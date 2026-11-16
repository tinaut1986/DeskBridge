using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBridge.Core.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// A plain Win32 window that shows decoded video: full screen without borders on one monitor (a lent
/// display), or a normal resizable window (tests). It runs its own message loop on its own thread;
/// frames are drawn from the receiver's thread through a flip-model swap chain.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VideoWindow : IDisposable
{
    private const uint WsOverlappedWindow = 0x00CF0000, WsPopup = 0x80000000, WsVisible = 0x10000000;
    private const uint WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const uint WmDestroy = 0x0002, WmSize = 0x0005, WmClose = 0x0010, WmSetCursor = 0x0020, WmUser = 0x0400;

    private readonly VideoDevice _video;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    /// <summary>
    /// One window class for the whole process, with one window procedure that lives as long as the process
    /// (a per-window delegate could be collected while Windows still calls it) and finds the window by handle.
    /// </summary>
    private static readonly WndProc Proc = StaticWindowProc;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, VideoWindow> Windows = new();
    private static readonly Lazy<string> ClassName = new(RegisterClass);
    private readonly object _drawLock = new();
    private readonly string _title;
    private readonly (int X, int Y, int W, int H)? _monitor;
    private nint _hwnd;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _frameView;

    /// <summary>
    /// The last picture, fitted to the window: the sender sends nothing while its screen is still, so the pointer
    /// is drawn again over this when only it moves.
    /// </summary>
    private ID3D11Texture2D? _picture;

    /// <summary>
    /// A thin border around the picture in this colour (red, green, blue; 0-1), so it is clear which machine
    /// this display shows; null for none.
    /// </summary>
    public (float R, float G, float B)? Frame { get; set; }

    private const int FrameWidth = 3;
    private VideoConverter? _converter;
    private PointerRenderer? _pointer;
    private PointerImage? _pendingPointerImage;
    private int _width, _height;
    private volatile bool _resized;

    /// <summary>
    /// Where the coordinator itself puts the other machine's pointer (see <see cref="SetLocalPointer"/>); Gone: it
    /// just left this display, and no pointer is drawn for a moment.
    /// </summary>
    private sealed record LocalPosition(int X, int Y, long Tick, bool Gone = false);

    /// <summary>
    /// After the pointer leaves, the position the other machine reports is still the old one (a round trip late):
    /// drawing it would show the pointer jump back inside before it disappears. Nothing is drawn this long.
    /// </summary>
    private const int GoneMs = 500;

    private volatile LocalPosition? _local;
    private readonly AutoResetEvent _repaintWanted = new(false);
    private Thread? _pointerThread;
    private volatile bool _closing;
    private long _lastShown; // Stopwatch timestamp of the last picture on screen

    /// <summary>
    /// The pointer alone is drawn again at most this often, and not while pictures keep coming (they draw it): a
    /// mouse reports up to 1000 moves a second, and drawing each one held the window so long that the video
    /// itself fell behind (dragging a window went in jumps).
    /// </summary>
    private const double PointerGapMs = 16;
    private (int X, int Y, bool Visible) _streamPointer;
    private int _hotX, _hotY;

    /// <summary>The mouse rests this long before the real position (from the stream) is trusted again.</summary>
    private const int RestMs = 300;

    /// <summary>
    /// The pointer the coordinator draws itself has drifted from the real one (a program or the other machine's
    /// own touchpad moved it): monitor (X, Y) and where the pointer really is on it (its hot spot, in pixels of the
    /// picture).
    /// </summary>
    public static event Action<int, int, int, int>? PointerCorrected;

    /// <summary>
    /// The other machine's pointer as the coordinator moves it, drawn at once (like Remote Desktop) instead of
    /// waiting for where the other machine reports it a round trip later: on the video window over the monitor
    /// at (<paramref name="monitorX"/>, <paramref name="monitorY"/>), at <paramref name="position"/> (pixels of the
    /// picture); every other window goes back to the reported pointer. Null for none anywhere. Cheap: called from
    /// the mouse hook.
    /// </summary>
    public static void SetLocalPointer(int monitorX, int monitorY, (int X, int Y)? position)
    {
        foreach (var window in Windows.Values)
        {
            bool here = position is not null && window._monitor is { } m && m.X == monitorX && m.Y == monitorY;
            LocalPosition next;
            if (here)
                next = new LocalPosition(position!.Value.X, position.Value.Y, Environment.TickCount64);
            else if (window._local is { Gone: false })
                next = new LocalPosition(0, 0, Environment.TickCount64, Gone: true);
            else
                continue;
            window._local = next;
            window._repaintWanted.Set();
        }
    }

    /// <summary>No video window draws a pointer of its own any more.</summary>
    public static void ClearLocalPointer() => SetLocalPointer(int.MinValue, int.MinValue, null);

    /// <summary>
    /// Whether the window at that screen point is one of these video windows (and not another window of this PC on
    /// top of it, such as one dropped over a display that shows another machine, or the taskbar).
    /// </summary>
    public static bool IsVideoAt(int x, int y)
    {
        nint hwnd = WindowFromPoint(new Point { X = x, Y = y });
        return hwnd != 0 && Windows.ContainsKey(GetAncestor(hwnd, 2 /* GA_ROOT */));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    /// <summary>
    /// Windows of this PC above the video windows (in z-order), clipped to them: screen rectangles where the cursor
    /// belongs to this PC even over a display that shows another machine. Hidden, minimised and cloaked windows,
    /// and windows that let clicks through, do not count.
    /// </summary>
    public static List<(int X, int Y, int Width, int Height)> WindowsAbove()
    {
        var result = new List<(int, int, int, int)>();
        var videos = Windows.Keys.Select(h => GetWindowRect(h, out var r) ? r : default).Where(r => r.Right > r.Left).ToList();
        if (videos.Count == 0)
            return result;
        for (nint hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, 2 /* GW_HWNDNEXT */))
        {
            if (Windows.ContainsKey(hwnd))
                break; // below the video windows nothing is visible over them
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                continue;
            long ex = GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */);
            if ((ex & 0x20 /* WS_EX_TRANSPARENT */) != 0)
                continue;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0)
                continue;
            if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Rect bounds, Marshal.SizeOf<Rect>()) != 0
                && !GetWindowRect(hwnd, out bounds))
                continue;
            foreach (var v in videos)
            {
                int left = Math.Max(bounds.Left, v.Left), top = Math.Max(bounds.Top, v.Top);
                int right = Math.Min(bounds.Right, v.Right), bottom = Math.Min(bounds.Bottom, v.Bottom);
                if (right > left && bottom > top)
                    result.Add((left, top, right - left, bottom - top));
            }
        }
        return result;
    }

    /// <summary>Every video window: where it is, its client size and what it draws (troubleshooting).</summary>
    public static string Report() => Windows.Count == 0
        ? "no video window"
        : string.Join("\n", Windows.Select(w =>
        {
            GetWindowRect(w.Key, out var r);
            GetClientRect(w.Key, out var c);
            var v = w.Value;
            return $"window {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}, client {c.Right - c.Left}x{c.Bottom - c.Top}, "
                   + $"monitor {v._monitor}, swap chain {v._width}x{v._height}, picture {v._converter?.InputWidth}x{v._converter?.InputHeight} "
                   + $"drawn at {v._converter?.DestRect}";
        }).Append("covered by windows of this PC: "
                  + string.Join("; ", WindowsAbove().Select(w => $"{w.X},{w.Y} {w.Width}x{w.Height}"))));

    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);

    /// <summary>The window was closed (by the user, for a test window).</summary>
    public event Action? Closed;

    /// <param name="monitor">Full screen on this monitor rectangle; null for a normal window.</param>
    public VideoWindow(VideoDevice video, string title, (int X, int Y, int W, int H)? monitor)
    {
        _video = video;
        _title = title;
        _monitor = monitor;
        _thread = new Thread(Run) { IsBackground = true, Name = "Video window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_hwnd == 0)
            throw new InvalidOperationException("Could not create the video window.");
        _pointerThread = new Thread(PointerLoop) { IsBackground = true, Name = "Video pointer" };
        _pointerThread.Start();
    }

    private void Run()
    {
        Priority.MultimediaThread();
        var instance = GetModuleHandleW(null);
        string className = ClassName.Value;

        if (_monitor is { } m)
        {
            // Over the lent monitor, never taking the focus (the keyboard stays where the cursor is). Not topmost:
            // any window can still be brought over it (DeskBridge's own, to change the profile).
            _hwnd = CreateWindowExW(WsExToolWindow | WsExNoActivate, className, _title, WsPopup | WsVisible,
                m.X, m.Y, m.W, m.H, 0, 0, instance, 0);
            (_width, _height) = (m.W, m.H);
        }
        else
        {
            _hwnd = CreateWindowExW(0, className, _title, WsOverlappedWindow | WsVisible,
                100, 100, 1280, 760, 0, 0, instance, 0);
            GetClientRect(_hwnd, out var r);
            (_width, _height) = (r.Right - r.Left, r.Bottom - r.Top);
        }

        if (_hwnd != 0)
        {
            Windows[_hwnd] = this;
            using var factory = _video.Adapter.GetParent<IDXGIFactory2>();
            _swapChain = factory.CreateSwapChainForHwnd(_video.Device, _hwnd, new SwapChainDescription1
            {
                Width = (uint)Math.Max(_width, 1),
                Height = (uint)Math.Max(_height, 1),
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
            });
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            using (var dxgiDevice = _video.Device.QueryInterface<IDXGIDevice1>())
                dxgiDevice.MaximumFrameLatency = 1; // never queue pictures ahead of the screen
        }
        _ready.Set();

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static string RegisterClass()
    {
        const string name = "DeskBridgeVideoWindow";
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(0, 32512 /* IDC_ARROW */),
            ClassName = name,
        };
        RegisterClassExW(ref wc);
        return name;
    }

    private static nint StaticWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window))
                return window.WindowProc(hwnd, message, wParam, lParam);
        }
        catch (Exception)
        {
            // never let an exception cross into Windows (it would end the process)
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmSize:
                _resized = true;
                break;
            case WmSetCursor when _monitor is not null:
                SetCursor(0); // the remote pointer is drawn in the picture
                return 1;
            case WmClose:
                DestroyWindow(hwnd);
                return 0;
            case WmDestroy:
                Windows.TryRemove(hwnd, out _);
                Closed?.Invoke();
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>The other machine's pointer picture changed.</summary>
    public void SetPointerImage(PointerImage image)
    {
        lock (_drawLock)
        {
            _pendingPointerImage = image;
            (_hotX, _hotY) = (image.HotX, image.HotY);
        }
    }

    /// <summary>
    /// Takes a decoded picture (slice <paramref name="slice"/> of an NV12 texture), fitted to the window, to be shown
    /// with the other machine's pointer over it when <paramref name="pointer"/> is visible. Only converts it: showing
    /// it waits for the screen, and is done on the window's presenting thread, so the decoder never waits there
    /// (decoding and showing on one thread took most of a frame time while the picture moved, and a hiccup made the
    /// frames pile up and come too late).
    /// </summary>
    public void Present(ID3D11Texture2D texture, uint slice, int width, int height, (int X, int Y, bool Visible) pointer = default)
    {
        lock (_drawLock)
        {
            if (_swapChain is null)
                return;
            if (_converter is null || _converter.InputWidth != width || _converter.InputHeight != height || _converterSize != (_width, _height))
            {
                _converter?.Dispose();
                _converter = new VideoConverter(_video, width, height, _width, _height);
                _converterSize = (_width, _height);
            }
            if (_picture is null || _pictureSize != (_width, _height))
            {
                _picture?.Dispose();
                _picture = _video.CreateTexture(_width, _height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget);
                _pictureSize = (_width, _height);
            }
            _converter.Convert(texture, slice, _picture);
            (_pictureWidth, _pictureHeight) = (width, height);
        }
        _streamPointer = pointer;
        _newPicture = true;
        _repaintWanted.Set();
    }

    private int _pictureWidth, _pictureHeight;
    private (int, int) _pictureSize, _converterSize;
    private volatile bool _newPicture;

    /// <summary>
    /// The other machine reports its pointer somewhere else: drawn again over the last picture (the screen is
    /// still, or a new picture would have drawn it), soon and at most at the screen's pace.
    /// </summary>
    public void RequestRepaint((int X, int Y, bool Visible) pointer)
    {
        _streamPointer = pointer;
        _repaintWanted.Set();
    }

    /// <summary>
    /// Shows the newest picture as soon as there is one; the pointer alone (it moved over a still picture) at most
    /// at the screen's pace. Waiting for the screen happens here, never on the decoder's thread.
    /// </summary>
    private void PointerLoop()
    {
        Priority.MultimediaThread();
        using var delay = new PreciseDelay();
        while (!_closing)
        {
            _repaintWanted.WaitOne();
            // The pointer alone: not right after a picture (the next one, if the screen is moving, draws it anyway).
            while (!_closing && !_newPicture)
            {
                double since = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastShown)).TotalMilliseconds;
                if (since >= PointerGapMs)
                    break;
                delay.Wait(PointerGapMs - since);
            }
            if (_closing)
                break;
            if (!_newPicture)
                Interlocked.Increment(ref _repaints);
            _newPicture = false;
            bool ready;
            lock (_drawLock)
                ready = Compose(_streamPointer);
            if (!ready)
                continue;
            try
            {
                // Without waiting for the vertical blank: waiting there made the window fall behind a stream that
                // runs a hair faster than the monitor. With the flip model a newer picture simply replaces one still
                // waiting for the next refresh (no tearing). With a frame latency of 1 this can still wait while the
                // previous picture is queued: here, not on the decoder.
                _swapChain?.Present(0, PresentFlags.None);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // the window is closing, or the GPU was reset: the next picture tries again
            }
        }
    }

    private int _repaints;

    /// <summary>Times the pointer was drawn again on its own since the last call (for logs).</summary>
    public int TakeRepaints() => Interlocked.Exchange(ref _repaints, 0);

    /// <summary>
    /// Draws the last picture, the pointer and the border into the back buffer (under the draw lock); false when
    /// there is nothing to draw yet.
    /// </summary>
    private bool Compose((int X, int Y, bool Visible) pointer)
    {
        if (_swapChain is null)
            return false;
        if (_resized)
        {
            _resized = false;
            GetClientRect(_hwnd, out var r);
            int rw = Math.Max(r.Right - r.Left, 1), rh = Math.Max(r.Bottom - r.Top, 1);
            if (rw != _width || rh != _height)
            {
                (_width, _height) = (rw, rh);
                _frameView?.Dispose();
                _frameView = null;
                _backBuffer?.Dispose();
                _backBuffer = null;
                _swapChain.ResizeBuffers(2, (uint)rw, (uint)rh, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                return false; // the picture is converted again at the new size with the next frame
            }
        }
        if (_picture is null || _converter is null || _pictureSize != (_width, _height))
            return false;
        int width = _pictureWidth, height = _pictureHeight;
        Interlocked.Exchange(ref _lastShown, Stopwatch.GetTimestamp());
        if (_local is { Gone: true } gone)
        {
            if (Environment.TickCount64 - gone.Tick < GoneMs)
                pointer = pointer with { Visible = false };
            else
                _local = null;
        }
        else if (_local is { } local)
        {
            // Drawn where the coordinator moved it. Once the mouse rests, where the other machine really has it wins
            // (it can differ: a program moved it, or a move was handled a bit differently over there).
            int realX = pointer.X + _hotX, realY = pointer.Y + _hotY;
            if (pointer.Visible && Environment.TickCount64 - local.Tick > RestMs && (realX, realY) != (local.X, local.Y) && _monitor is { } m)
            {
                _local = local with { X = realX, Y = realY };
                PointerCorrected?.Invoke(m.X, m.Y, realX, realY);
            }
            else
            {
                pointer = (local.X - _hotX, local.Y - _hotY, pointer.Visible);
            }
        }
        _backBuffer ??= _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _video.Context.CopyResource(_backBuffer, _picture);
        if (_pendingPointerImage is { } image)
        {
            _pendingPointerImage = null;
            _pointer ??= new PointerRenderer(_video);
            _pointer.SetImage(image);
        }
        if (pointer.Visible)
            _pointer?.Draw(_backBuffer, _width, _height, _converter.DestRect, width, height, pointer.X, pointer.Y);
        if (Frame is { } frame)
            DrawFrame(frame);
        return true;
    }

    /// <summary>The border: four thin rectangles filled on the GPU (ClearView), next to nothing per frame.</summary>
    private void DrawFrame((float R, float G, float B) color)
    {
        try
        {
            _frameView ??= _video.Device.CreateRenderTargetView(_backBuffer!);
            using var context = _video.Context.QueryInterfaceOrNull<ID3D11DeviceContext1>();
            if (context is null)
                return;
            int w = _width, h = _height, f = FrameWidth;
            Vortice.RawRect[] rects =
            [
                new(0, 0, w, f), new(0, h - f, w, h), new(0, 0, f, h), new(w - f, 0, w, h),
            ];
            context.ClearView(_frameView, new Vortice.Mathematics.Color4(color.R, color.G, color.B, 1), rects);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            Frame = null; // not supported here: no border rather than no picture
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
            PostMessageW(_hwnd, WmClose, 0, 0);
        _thread.Join(2000);
        _closing = true;
        _repaintWanted.Set();
        _pointerThread?.Join(1000);
        lock (_drawLock)
        {
            _converter?.Dispose();
            _pointer?.Dispose();
            _picture?.Dispose();
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _swapChain = null;
        }
        _ready.Dispose();
        _repaintWanted.Dispose();
    }

    // Win32.

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
