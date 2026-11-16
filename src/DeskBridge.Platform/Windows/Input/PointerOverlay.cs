using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Input;

/// <summary>
/// A pointer drawn by DeskBridge itself while another machine controls this one, for sessions where the
/// real one is not visible: in a Remote Desktop session the client draws the pointer only where its own
/// mouse is, so a pointer moved by DeskBridge would be invisible. This is a small topmost, click-through,
/// per-pixel-alpha window that follows the cursor with its current shape; being a window, it is part of
/// the picture the session sends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PointerOverlay : IDisposable
{
    private const int Size = 64;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x80000, WsExTransparent = 0x20, WsExTopmost = 0x8, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpShowWindow = 0x40, SwpHideWindow = 0x80, SwpAsync = 0x4000;
    private const uint WmUser = 0x0400, WmRedraw = WmUser + 1, WmClose = 0x0010, WmDestroy = 0x0002;

    private static readonly WndProc Proc = StaticProc;
    private static readonly Lazy<string> ClassName = new(Register);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hwnd;
    private nint _shownCursor;
    private (int X, int Y) _hotSpot;
    private bool _visible;

    /// <summary>True in a Remote Desktop session, where a pointer moved by DeskBridge is not drawn.</summary>
    public static bool Needed => GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0;

    public PointerOverlay()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Pointer overlay" };
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        _hwnd = CreateWindowExW(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
            ClassName.Value, "DeskBridge pointer", WsPopup, 0, 0, Size, Size, 0, 0, GetModuleHandleW(null), 0);
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.Message == WmRedraw && msg.Hwnd == _hwnd)
                Redraw(msg.WParam);
            DispatchMessageW(ref msg);
        }
    }

    private static string Register()
    {
        const string name = "DeskBridgePointerOverlay";
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            Instance = GetModuleHandleW(null),
            ClassName = name,
        };
        RegisterClassExW(ref wc);
        return name;
    }

    private static nint StaticProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmClose)
        {
            DestroyWindow(hwnd);
            return 0;
        }
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Shows the pointer at the cursor position (call after every move), with the cursor's current shape.</summary>
    public void MoveTo(int x, int y)
    {
        if (_hwnd == 0)
            return;
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        nint cursor = GetCursorInfo(ref info) && info.Cursor != 0 ? info.Cursor : LoadCursorW(0, 32512 /* IDC_ARROW */);
        if (cursor != _shownCursor)
        {
            _shownCursor = cursor;
            if (GetIconInfo(cursor, out var icon))
            {
                _hotSpot = ((int)icon.HotspotX, (int)icon.HotspotY);
                if (icon.Mask != 0) DeleteObject(icon.Mask);
                if (icon.Color != 0) DeleteObject(icon.Color);
            }
            PostMessageW(_hwnd, WmRedraw, cursor, 0);
        }
        SetWindowPos(_hwnd, -1 /* HWND_TOPMOST */, x - _hotSpot.X, y - _hotSpot.Y, 0, 0,
            SwpNoSize | SwpNoActivate | SwpAsync | (_visible ? 0 : SwpShowWindow));
        _visible = true;
    }

    public void Hide()
    {
        if (_hwnd != 0 && _visible)
            SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsync | SwpHideWindow);
        _visible = false;
    }

    /// <summary>Draws the cursor into a 32-bit DIB and gives it to the layered window (per-pixel alpha).</summary>
    private void Redraw(nint cursor)
    {
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = Size,
            Height = -Size,
            Planes = 1,
            BitCount = 32,
        };
        nint bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        nint old = SelectObject(dc, bitmap);
        try
        {
            // DrawIconEx does not write alpha for every cursor: draw it on black and on white, and work out
            // each pixel's alpha and colour from the two (works for colour, alpha and monochrome cursors).
            var onBlack = Render(dc, bits, cursor, 0x000000);
            var onWhite = Render(dc, bits, cursor, 0xFFFFFF);
            var pixels = new byte[Size * Size * 4];
            for (int i = 0; i < Size * Size; i++)
            {
                int a = 255 - (onWhite[i * 4 + 1] - onBlack[i * 4 + 1]); // green channel: same for every grey
                a = Math.Clamp(a, 0, 255);
                pixels[i * 4] = onBlack[i * 4];
                pixels[i * 4 + 1] = onBlack[i * 4 + 1];
                pixels[i * 4 + 2] = onBlack[i * 4 + 2];
                pixels[i * 4 + 3] = (byte)a; // premultiplied: the colour on black already is
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            var size = new NativeSize { Cx = Size, Cy = Size };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(_hwnd, screen, 0, ref size, dc, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    private static byte[] Render(nint dc, nint bits, nint cursor, uint background)
    {
        var fill = new byte[Size * Size * 4];
        for (int i = 0; i < Size * Size; i++)
        {
            fill[i * 4] = (byte)(background >> 16);
            fill[i * 4 + 1] = (byte)(background >> 8);
            fill[i * 4 + 2] = (byte)background;
        }
        Marshal.Copy(fill, 0, bits, fill.Length);
        DrawIconEx(dc, 0, 0, cursor, 0, 0, 0, 0, 3 /* DI_NORMAL */);
        var result = new byte[fill.Length];
        Marshal.Copy(bits, result, 0, result.Length);
        return result;
    }

    public void Dispose()
    {
        if (_hwnd != 0)
            PostMessageW(_hwnd, WmClose, 0, 0);
        _thread.Join(1000);
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
    private struct CursorInfo
    {
        public uint Size, Flags;
        public nint Cursor;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(nint hwnd, nint dst, nint pointDst, ref NativeSize size, nint src, ref NativePoint pointSrc, uint key, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
