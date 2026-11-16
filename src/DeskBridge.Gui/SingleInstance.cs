using System.Threading;

namespace DeskBridge.Gui;

/// <summary>
/// One DeskBridge per user session: a second copy would be a second agent with the same name, and the
/// coordinator would keep dropping one for the other. Starting it again brings the running one forward.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DeskBridge-instance";
    private const string ShowEventName = @"Local\DeskBridge-show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show, Action onShowRequested)
    {
        _mutex = mutex;
        _show = show;
        _wait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// The instance lock, or null if DeskBridge is already running (which has then been asked to show itself).
    /// </summary>
    /// <param name="onShowRequested">Called (on a thread-pool thread) when another start asks this one to show up.</param>
    public static SingleInstance? Acquire(Action onShowRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            show.Set();
            show.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, show, onShowRequested);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned by this thread any more: released when the process ends
        }
        _mutex.Dispose();
    }
}
