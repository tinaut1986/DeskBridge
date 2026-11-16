using System.Runtime.InteropServices;
using System.Windows.Media;
using DeskBridge.Core.Planning;

namespace DeskBridge.Gui.Sharing;

/// <summary>
/// A thin border in a machine's colour around one display, so it is clear which machine each display belongs
/// to (like Teams marks a shared screen). Four 3-pixel strips, each a bare Win32 window filled with a solid colour
/// by Windows itself: a full-screen transparent window instead made Windows recompose every monitor on every
/// refresh, and the video on the other display fell to 30 frames per second. The strips let clicks through,
/// never take the focus, stay on top, and are left out of screen captures.
/// </summary>
internal sealed class ScreenFrame : IDisposable
{
    private const int Width = 3;

    private readonly nint[] _strips = new nint[4];
    private nint _brush;
    private Color _color;
    private DisplayRect _display;

    /// <summary>Around that display (physical pixels), in that colour; kept on top.</summary>
    public void Place(DisplayRect display, Color color)
    {
        EnsureClass();
        if (_strips[0] == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                _strips[i] = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered,
                    ClassName, "DeskBridge frame", WsPopup, 0, 0, 1, 1, 0, 0, Instance, 0);
                SetLayeredWindowAttributes(_strips[i], 0, 255, LwaAlpha); // opaque: no per-pixel blending
                SetWindowDisplayAffinity(_strips[i], WdaExcludeFromCapture);
            }
        }
        if (color != _color || _brush == 0)
        {
            var old = _brush;
            _brush = CreateSolidBrush((uint)(color.R | color.G << 8 | color.B << 16));
            foreach (var strip in _strips)
            {
                SetClassLongPtrW(strip, GclpHbrBackground, _brush);
                InvalidateRect(strip, 0, true);
            }
            if (old != 0)
                DeleteObject(old);
            _color = color;
        }
        _display = display;
        var (x, y, w, h) = (display.X, display.Y, display.Width, display.Height);
        Put(_strips[0], x, y, w, Width);              // top
        Put(_strips[1], x, y + h - Width, w, Width);  // bottom
        Put(_strips[2], x, y, Width, h);              // left
        Put(_strips[3], x + w - Width, y, Width, h);  // right
    }

    private static void Put(nint strip, int x, int y, int w, int h) =>
        SetWindowPos(strip, HwndTopmost, x, y, w, h, SwpNoActivate | SwpShowWindow);

    public void Dispose()
    {
        foreach (var strip in _strips)
            if (strip != 0)
                DestroyWindow(strip);
        Array.Clear(_strips);
        if (_brush != 0)
            DeleteObject(_brush);
        _brush = 0;
    }

    // ------------------------------------------------------------------ one window class for all the strips

    private const string ClassName = "DeskBridgeFrameStrip";
    private static bool _registered;
    private static readonly WndProc Procedure = DefWindowProcW; // kept alive: Windows calls it for the whole process' life
    private static nint Instance => GetModuleHandleW(null);

    private static void EnsureClass()
    {
        if (_registered)
            return;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = Instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);
        _registered = true;
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x2, WdaExcludeFromCapture = 0x11;
    private const int GclpHbrBackground = -10;
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern nint SetClassLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);
}
