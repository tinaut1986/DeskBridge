using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Secure;

/// <summary>
/// Starts a process in the session of whoever is at the console, with the calling process's own token (SYSTEM,
/// from the service): the only kind of process that can see and use the secure desktop there (the lock screen,
/// administrator prompts), which belongs to Winlogon.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionProcess
{
    /// <summary>The session at the physical console, or null when nobody is there (no session yet).</summary>
    public static uint? ConsoleSession()
    {
        uint id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? null : id;
    }

    /// <summary>Starts <paramref name="exe"/> with <paramref name="arguments"/> in session <paramref name="session"/>, hidden.</summary>
    /// <returns>The process id.</returns>
    public static int Start(uint session, string exe, string arguments)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAllAccess, out var token))
            throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(token, MaximumAllowed, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var primary))
                throw new Win32Exception();
            try
            {
                if (!SetTokenInformation(primary, 12 /* TokenSessionId */, ref session, sizeof(uint)))
                    throw new Win32Exception();
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfo>(),
                    Desktop = @"winsta0\default",
                };
                string commandLine = $"\"{exe}\" {arguments}";
                if (!CreateProcessAsUserW(primary, exe, commandLine, 0, 0, false, CreateNoWindow, 0,
                        Path.GetDirectoryName(exe), ref startup, out var info))
                    throw new Win32Exception();
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
                return info.ProcessId;
            }
            finally
            {
                CloseHandle(primary);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenAllAccess = 0xF01FF, MaximumAllowed = 0x02000000, CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonation, int type, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(nint token, string application, string commandLine, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup,
        out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
