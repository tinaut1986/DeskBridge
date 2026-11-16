using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskBridge.Platform.Windows;

/// <summary>
/// Gets DeskBridge's time-critical work its turn on a busy PC (compilers, containers, an emulator...), the way
/// sound and games get theirs: the process above normal priority, and the threads that capture, encode, show
/// video and watch the mouse in the multimedia class scheduler (MMCSS), which Windows serves first for short,
/// regular bursts of work. Nothing here is required: each step is skipped if Windows refuses it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Priority
{
    /// <summary>The whole process above normal (not High: it must never starve the rest of the PC).</summary>
    public static void RaiseProcess()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass == ProcessPriorityClass.Normal)
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// The calling thread in the multimedia class scheduler, as a "Games" task (short, regular, latency-bound work)
    /// at high priority. Lasts as long as the thread.
    /// </summary>
    public static void MultimediaThread()
    {
        try
        {
            uint index = 0;
            nint handle = AvSetMmThreadCharacteristicsW("Games", ref index);
            if (handle != 0)
                AvSetMmThreadPriority(handle, 1 /* AVRT_PRIORITY_HIGH */);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvSetMmThreadPriority(nint handle, int priority);
}
