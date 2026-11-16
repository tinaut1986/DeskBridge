using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DeskBridge.Core.Planning;

namespace DeskBridge.Gui.Sharing;

/// <summary>
/// A thin border in a machine's colour around one display, so it is clear which machine each display belongs
/// to (like Teams marks a shared screen). It lets every click through, never takes the focus, stays on top, and
/// is left out of screen captures (DeskBridge's own video, screenshots, screen sharing).
/// </summary>
internal sealed class ScreenFrame : Window
{
    private readonly Border _border = new() { BorderThickness = new Thickness(3) };
    private nint _hwnd;

    public ScreenFrame()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "DeskBridge frame";
        Content = _border;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtrW(_hwnd, GwlExStyle);
        SetWindowLongPtrW(_hwnd, GwlExStyle, (nint)(style | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered));
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
    }

    /// <summary>Around that display (physical pixels), in that colour; kept on top.</summary>
    public void Place(DisplayRect display, Color color)
    {
        if (_border.BorderBrush is not SolidColorBrush { Color: var current } || current != color)
            _border.BorderBrush = new SolidColorBrush(color);
        if (!IsVisible)
            Show();
        SetWindowPos(_hwnd, HwndTopmost, display.X, display.Y, display.Width, display.Height, SwpNoActivate | SwpShowWindow);
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;
    private const uint WdaExcludeFromCapture = 0x11;
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
