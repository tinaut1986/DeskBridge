using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// On a machine whose display shows a screen of the coordinator (the laptop panel showing the desktop's fourth
/// monitor), that display belongs to the coordinator for the mouse: a window of this machine that opens there (a
/// dialog, settings, a prompt of an app) can be neither seen behind the video nor reached with the coordinator's
/// mouse. Such windows, behind the video, are moved onto a display of this machine that is not covered (e.g. its
/// virtual display, shown on one of the coordinator's monitors), where the coordinator's mouse reaches them. Those on
/// top of the video stay: the coordinator sends the cursor here over them (see WindowsOverVideoMessage).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowRescue
{
    /// <param name="free">This machine's displays not covered by a video window, where windows may go.</param>
    /// <returns>How many windows were moved.</returns>
    public static int Run(IReadOnlyList<(int X, int Y, int Width, int Height)> free, Action<string> log)
    {
        var covered = VideoWindow.Areas();
        if (covered.Count == 0 || free.Count == 0)
            return 0;
        // A button held: a window being dragged (maybe across that display on its way): not fought with.
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0)
            return 0;
        var target = free[0];
        // Those on top of the video can be seen, and used from the coordinator (the cursor comes here over them):
        // they stay. Only those behind it, out of sight, are moved.
        var above = VideoWindow.HandlesAbove();
        int moved = 0;
        var windows = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, 0);
        foreach (var hwnd in windows)
        {
            // (DeskBridge's own window too: only its video windows stay where they are.)
            if (covered.Any(c => c.Hwnd == hwnd) || above.Contains(hwnd) || !IsMovable(hwnd))
                continue;
            if (!GetWindowRect(hwnd, out var r))
                continue;
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            if (!covered.Any(c => cx >= c.X && cx < c.X + c.Width && cy >= c.Y && cy < c.Y + c.Height))
                continue; // not over a display that shows the coordinator
            bool maximized = IsZoomed(hwnd);
            if (maximized)
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
            GetWindowRect(hwnd, out r);
            int w = Math.Min(r.Right - r.Left, target.Width), h = Math.Min(r.Bottom - r.Top, target.Height);
            int x = target.X + (target.Width - w) / 2, y = target.Y + (target.Height - h) / 2;
            SetWindowPos(hwnd, 0, x, y, w, h, 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */);
            if (maximized)
                ShowWindow(hwnd, 3 /* SW_MAXIMIZE */); // maximised again, on its new display
            moved++;
            log($"[video] '{Title(hwnd)}' opened on a display that shows the coordinator: moved to the display at ({target.X},{target.Y})");
        }
        return moved;
    }

    /// <summary>
    /// A window someone works with: visible, not minimised or cloaked, with a title bar or a dialog's frame, not a
    /// tool window, menu or one that lets clicks through.
    /// </summary>
    private static bool IsMovable(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0)
            return false;
        long style = GetWindowLongPtrW(hwnd, -16 /* GWL_STYLE */);
        long ex = GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */);
        if ((ex & (0x80 /* WS_EX_TOOLWINDOW */ | 0x20 /* WS_EX_TRANSPARENT */ | 0x08000000 /* WS_EX_NOACTIVATE */)) != 0)
            return false;
        bool caption = (style & 0x00C00000 /* WS_CAPTION */) == 0x00C00000;
        bool dialogFrame = (style & 0x00400000 /* WS_DLGFRAME */) != 0 || (ex & 0x1 /* WS_EX_DLGMODALFRAME */) != 0;
        return caption || dialogFrame;
    }

    private static string Title(nint hwnd)
    {
        var text = new char[256];
        int n = GetWindowTextW(hwnd, text, text.Length);
        return n > 0 ? new string(text, 0, n) : "?";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumProc(nint hwnd, nint param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, char[] text, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
