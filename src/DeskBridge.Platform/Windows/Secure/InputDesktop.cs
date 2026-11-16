using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DeskBridge.Platform.Windows.Secure;

/// <summary>
/// The desktop that gets the keyboard and mouse right now: "Default" (the user's), "Winlogon" (the lock screen,
/// administrator prompts, Ctrl+Alt+Del) or "Screen-saver". Only SYSTEM can open Winlogon; a normal process just
/// fails to (which tells it the secure desktop is up).
/// </summary>
[SupportedOSPlatform("windows")]
public static class InputDesktop
{
    /// <summary>The name of the input desktop, or null when this process may not open it (it is the secure one).</summary>
    public static string? Name()
    {
        nint desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
            return null;
        try
        {
            return NameOf(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>Whether the user's own desktop has the input (false: the lock screen or a prompt is up, or unknown).</summary>
    public static bool IsDefault() => string.Equals(Name(), "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the calling thread onto the input desktop, so what it captures and injects goes there. The thread must
    /// own no windows or hooks. True when it is there.
    /// </summary>
    public static bool Follow()
    {
        nint desktop = OpenInputDesktop(0, false, GenericAll);
        if (desktop == 0)
            return false;
        if (SetThreadDesktop(desktop))
            return true; // kept open: the thread uses it
        CloseDesktop(desktop);
        return false;
    }

    private static string? NameOf(nint desktop)
    {
        var buffer = new byte[256];
        if (!GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, buffer, buffer.Length, out int needed))
            return null;
        return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
    }

    private const uint DesktopReadObjects = 0x0001, GenericAll = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(nint obj, int index, byte[] info, int length, out int needed);
}
