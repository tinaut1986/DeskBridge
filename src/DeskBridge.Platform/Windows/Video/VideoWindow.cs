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
    private VideoConverter? _converter;
    private PointerRenderer? _pointer;
    private PointerImage? _pendingPointerImage;
    private int _width, _height;
    private volatile bool _resized;

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
    }

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        string className = ClassName.Value;

        if (_monitor is { } m)
        {
            // Over the lent monitor, never taking the focus (the keyboard stays where the cursor is).
            _hwnd = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate, className, _title, WsPopup | WsVisible,
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
            _pendingPointerImage = image;
    }

    /// <summary>
    /// Draws a decoded picture (slice <paramref name="slice"/> of an NV12 texture), fitted to the window, with the
    /// other machine's pointer over it when <paramref name="pointer"/> is visible.
    /// </summary>
    public void Present(ID3D11Texture2D texture, uint slice, int width, int height, (int X, int Y, bool Visible) pointer = default)
    {
        lock (_drawLock)
        {
            if (_swapChain is null)
                return;
            if (_resized)
            {
                _resized = false;
                GetClientRect(_hwnd, out var r);
                int w = Math.Max(r.Right - r.Left, 1), h = Math.Max(r.Bottom - r.Top, 1);
                if (w != _width || h != _height)
                {
                    (_width, _height) = (w, h);
                    _converter?.Dispose();
                    _converter = null;
                    _backBuffer?.Dispose();
                    _backBuffer = null;
                    _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                }
            }
            if (_converter is null || _converter.InputWidth != width || _converter.InputHeight != height)
            {
                _converter?.Dispose();
                _converter = new VideoConverter(_video, width, height, _width, _height);
            }
            _backBuffer ??= _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _converter.Convert(texture, slice, _backBuffer);
            if (_pendingPointerImage is { } image)
            {
                _pendingPointerImage = null;
                _pointer ??= new PointerRenderer(_video);
                _pointer.SetImage(image);
            }
            if (pointer.Visible)
                _pointer?.Draw(_backBuffer, _width, _height, _converter.DestRect, width, height, pointer.X, pointer.Y);
            // Without waiting for the vertical blank: waiting there made the window fall behind a stream that
            // runs a hair faster than the monitor, and the delay grew for as long as it was open. With the flip
            // model a newer picture simply replaces one still waiting for the next refresh (no tearing).
            _swapChain.Present(0, PresentFlags.None);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
            PostMessageW(_hwnd, WmClose, 0, 0);
        _thread.Join(2000);
        lock (_drawLock)
        {
            _converter?.Dispose();
            _pointer?.Dispose();
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _swapChain = null;
        }
        _ready.Dispose();
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
