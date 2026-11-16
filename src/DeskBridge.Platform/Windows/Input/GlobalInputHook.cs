using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static DeskBridge.Platform.Windows.Input.NativeInput;

namespace DeskBridge.Platform.Windows.Input;

/// <summary>A mouse event seen by the low-level hook, in physical screen pixels.</summary>
public readonly record struct HookedMouse(int Message, int X, int Y, int Data, bool Injected);

/// <summary>A key event seen by the low-level hook.</summary>
public readonly record struct HookedKey(int VirtualKey, int ScanCode, bool Down, bool Extended, bool Injected);

/// <summary>
/// Global low-level keyboard and mouse hooks on a dedicated thread with its own message loop (Windows
/// calls the hooks on the thread that installed them, and drops them if it does not answer quickly).
/// The callbacks return true to swallow the event, so it never reaches this PC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalInputHook : IDisposable
{
    private readonly Func<HookedMouse, bool> _onMouse;
    private readonly Func<HookedKey, bool> _onKey;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private HookProc? _mouseProc, _keyProc; // kept alive: the delegates are called from native code
    private IntPtr _mouseHook, _keyHook;
    private uint _threadId;
    private Exception? _startError;

    public GlobalInputHook(Func<HookedMouse, bool> onMouse, Func<HookedKey, bool> onKey)
    {
        _onMouse = onMouse;
        _onKey = onKey;
        _thread = new Thread(Run) { IsBackground = true, Name = "DeskBridge input hook" };
        _thread.Start();
        _started.Wait();
        if (_startError is not null)
            throw _startError;
    }

    private void Run()
    {
        Priority.MultimediaThread(); // a busy PC must not make the mouse late (or Windows skip the hook)
        _threadId = GetCurrentThreadId();
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            _startError = new InvalidOperationException($"Could not install the input hooks (error {Marshal.GetLastWin32Error()}).");
        _started.Set();
        if (_startError is not null)
            return;

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // Nothing to dispatch: the loop only lets Windows call the hooks on this thread.
        }
        UnhookWindowsHookEx(_mouseHook);
        UnhookWindowsHookEx(_keyHook);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var e = new HookedMouse((int)wParam, info.pt.X, info.pt.Y, unchecked((int)info.mouseData), (info.flags & LLMHF_INJECTED) != 0);
            try
            {
                if (_onMouse(e))
                    return 1;
            }
            catch
            {
                // a failing callback must never block the user's mouse
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            var e = new HookedKey((int)info.vkCode, (int)info.scanCode, msg is WM_KEYDOWN or WM_SYSKEYDOWN,
                (info.flags & LLKHF_EXTENDED) != 0, (info.flags & LLKHF_INJECTED) != 0);
            try
            {
                if (_onKey(e))
                    return 1;
            }
            catch
            {
                // a failing callback must never block the user's keyboard
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _started.Dispose();
    }
}
