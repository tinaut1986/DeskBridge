using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows.Video;

/// <summary>
/// Wakes up at a steady frame rate with a high-resolution waitable timer (Thread.Sleep is only as precise
/// as the system tick, up to 15.6 ms), skipping ahead instead of bursting when it falls behind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    private readonly nint _timer;
    private readonly long _interval;
    private long _next;

    public FramePacer(int fps)
    {
        _interval = Stopwatch.Frequency / Math.Max(1, fps);
        _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_timer == 0)
            _timer = CreateWaitableTimerExW(0, null, 0, TimerAllAccess); // older Windows: still better than Sleep
        _next = Stopwatch.GetTimestamp() + _interval;
    }

    /// <summary>Waits for the next frame time.</summary>
    public void Wait()
    {
        long now = Stopwatch.GetTimestamp();
        long remaining = _next - now;
        if (remaining > 0)
        {
            long due = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency); // relative, in 100 ns
            if (_timer != 0 && SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
                WaitForSingleObject(_timer, 1000);
            else
                Thread.Sleep(TimeSpan.FromTicks(-due));
        }
        _next += _interval;
        now = Stopwatch.GetTimestamp();
        if (now - _next > _interval)
            _next = now + _interval; // fell behind (machine busy): skip the missed frames
    }

    public void Dispose()
    {
        if (_timer != 0)
            CloseHandle(_timer);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completion, nint arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
