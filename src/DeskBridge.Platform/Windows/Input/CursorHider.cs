using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Input;

/// <summary>
/// Hides this PC's mouse pointer while the keyboard and mouse are on the other machine (otherwise it stays in
/// sight, parked in the middle of a display or on top of a window), by swapping every system pointer for a
/// blank one; <see cref="Show"/> reloads the user's pointers. Called at start too, in case the app ended while
/// the pointer was hidden.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CursorHider
{
    private static readonly uint[] SystemCursors =
    [
        32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672,
    ];

    private static readonly object Gate = new();
    private static bool _hidden;

    public static bool Hidden
    {
        get
        {
            lock (Gate)
                return _hidden;
        }
    }

    public static void Hide()
    {
        lock (Gate)
        {
            if (_hidden)
                return;
            var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
            var xor = new byte[32 * 32 / 8];
            foreach (var id in SystemCursors)
            {
                // SetSystemCursor takes the cursor over (and destroys it): one blank cursor per system pointer.
                nint blank = CreateCursor(0, 0, 0, 32, 32, and, xor);
                if (blank != 0 && !SetSystemCursor(blank, id))
                    DestroyCursor(blank);
            }
            _hidden = true;
        }
    }

    public static void Show()
    {
        lock (Gate)
        {
            SystemParametersInfoW(0x57 /* SPI_SETCURSORS */, 0, 0, 0);
            _hidden = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint CreateCursor(nint instance, int hotX, int hotY, int width, int height, byte[] andMask, byte[] xorMask);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(nint cursor, uint id);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, nint value, uint winIni);
}
