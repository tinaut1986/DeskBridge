namespace DeskBridge.Core.Video;

/// <summary>
/// Decides which received frames are too late to show. A frame's delay is measured against the least delayed
/// frame of the last <see cref="Window"/> (the two machines' clocks are never compared directly, only how far
/// apart they are): compared with the best since the stream started instead, the clocks' slow drift (a few
/// milliseconds a minute) added up until, after an hour, every frame looked late and nothing was shown.
/// </summary>
public sealed class LateFrames
{
    private readonly long _windowTicks, _lateTicks;

    // Frames of the window by arrival (ticks), keeping only those that can still be the minimum.
    private readonly LinkedList<(long Arrived, long Behind)> _minimums = new();

    /// <summary>
    /// Long enough that frames held up for a while (a backlog after a hiccup) stay late until they are skipped
    /// and the stream has caught up, instead of becoming the new normal (with a 2-second window, a backlog that
    /// lasted longer stayed for good: 176 ms behind instead of 20); short enough for the clocks' drift (a few
    /// milliseconds a minute) not to matter.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <param name="late">A frame this much more delayed than the window's best is not shown.</param>
    public LateFrames(TimeSpan late)
    {
        _windowTicks = Window.Ticks;
        _lateTicks = late.Ticks;
    }

    /// <summary>A frame sent at <paramref name="sentTicks"/> (sender's clock) arrived at <paramref name="nowTicks"/> (ours).</summary>
    public bool IsLate(long nowTicks, long sentTicks)
    {
        long behind = nowTicks - sentTicks;
        while (_minimums.First is { } first && nowTicks - first.Value.Arrived > _windowTicks)
            _minimums.RemoveFirst();
        while (_minimums.Last is { } last && last.Value.Behind >= behind)
            _minimums.RemoveLast();
        _minimums.AddLast((nowTicks, behind));
        return behind - _minimums.First!.Value.Behind > _lateTicks;
    }
}
