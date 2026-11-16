using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskBridge.Core.Control;
using static DeskBridge.Platform.Windows.Input.NativeInput;

namespace DeskBridge.Platform.Windows.Input;

/// <summary>
/// Plays keyboard and mouse events on this PC (SendInput), and keeps track of what is held down so it can
/// all be released when the cursor goes back to the coordinator (no keys stuck down).
/// Note: Windows does not let it type into elevated windows or the secure desktop (UAC, Ctrl+Alt+Del).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputInjector
{
    private readonly HashSet<(int Scan, bool Extended)> _keysDown = [];
    private readonly HashSet<int> _buttonsDown = [];

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void SetCursor(int x, int y) => SetCursorPos(x, y);

    /// <summary>
    /// Moves the cursor to an absolute point (physical pixels), as a real mouse move. While a window of an app
    /// running as administrator has the focus (and this one does not), Windows drops injected input: the cursor
    /// is then placed directly, which it does allow, so it can still be moved off this PC.
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1, 1), height = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1, 1);
        uint sent = SendInput(1, [Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            (int)Math.Round((x - left) * 65535.0 / width), (int)Math.Round((y - top) * 65535.0 / height))], Marshal.SizeOf<INPUT>());
        GetCursorPos(out var now);
        if (sent == 0 || Math.Abs(now.X - x) > 1 || Math.Abs(now.Y - y) > 1)
            SetCursorPos(x, y);
    }

    public void Play(InputEvent e)
    {
        switch (e.Kind)
        {
            case InputKind.MouseButton:
                Button(e.Code, e.Down);
                break;
            case InputKind.Wheel:
                Send(Mouse(e.Extended ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)e.X)));
                break;
            case InputKind.Key:
                Key(e.Code, e.Extended, e.Down);
                break;
        }
    }

    private void Button(int button, bool down)
    {
        uint flags = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
        };
        uint data = button switch { 3 => 1u, 4 => 2u, _ => 0u };
        Send(Mouse(flags, 0, 0, data));
        if (down)
            _buttonsDown.Add(button);
        else
            _buttonsDown.Remove(button);
    }

    private void Key(int scan, bool extended, bool down)
    {
        // Right Shift (0x36) and Left Shift (0x2A) are never extended keys: E0 36 / E0 2A are the "fake shifts"
        // keyboards send around some keys, and Windows ignores them. Some keyboards report Right Shift as
        // extended to the hook, so it is corrected here.
        if (scan is 0x36 or 0x2A)
            extended = false;
        uint flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        Send(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)scan, dwFlags = flags } } });
        if (down)
            _keysDown.Add((scan, extended));
        else
            _keysDown.Remove((scan, extended));
    }

    /// <summary>Releases every key and button this injector pressed and did not release.</summary>
    public void ReleaseAll()
    {
        foreach (var (scan, extended) in _keysDown.ToList())
            Key(scan, extended, down: false);
        foreach (var button in _buttonsDown.ToList())
            Button(button, down: false);
    }

    private static INPUT Mouse(uint flags, int dx, int dy, uint data = 0) =>
        new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    private static void Send(INPUT input) => SendInput(1, [input], Marshal.SizeOf<INPUT>());
}
