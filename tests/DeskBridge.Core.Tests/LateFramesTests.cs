using DeskBridge.Core.Video;

namespace DeskBridge.Core.Tests;

public class LateFramesTests
{
    private static readonly long Frame = TimeSpan.FromSeconds(1.0 / 60).Ticks;

    [Fact]
    public void Clocks_drifting_apart_for_an_hour_do_not_make_frames_late()
    {
        // The sender's clock runs 3 ms a minute slower than ours: after an hour, 180 ms of drift.
        var late = new LateFrames(TimeSpan.FromMilliseconds(80));
        int lateCount = 0;
        for (long i = 0; i < 60 * 60 * 60; i++)
        {
            long now = i * Frame + TimeSpan.FromMilliseconds(5).Ticks;
            long sent = i * Frame - (long)(i * Frame * 0.00005);
            if (late.IsLate(now, sent))
                lateCount++;
        }
        Assert.Equal(0, lateCount);
    }

    [Fact]
    public void Frames_held_up_by_the_network_are_late_and_the_next_ones_are_not()
    {
        var late = new LateFrames(TimeSpan.FromMilliseconds(80));
        long now = 0;
        for (int i = 0; i < 120; i++)
            Assert.False(late.IsLate(now = i * Frame + 50_000, i * Frame));

        // A 300 ms hiccup: the frames sent meanwhile arrive together, late.
        long burst = 120 * Frame + TimeSpan.FromMilliseconds(300).Ticks;
        for (int i = 120; i < 138; i++)
            Assert.True(late.IsLate(burst, i * Frame) || i * Frame > burst - TimeSpan.FromMilliseconds(85).Ticks);

        // Then on time again.
        for (int i = 140; i < 200; i++)
            Assert.False(late.IsLate(i * Frame + 50_000, i * Frame));
    }

    [Fact]
    public void A_backlog_lasting_several_seconds_stays_late_until_it_is_gone()
    {
        var late = new LateFrames(TimeSpan.FromMilliseconds(80));
        long i = 0;
        for (; i < 600; i++) // 10 s on time, 20 ms behind
            Assert.False(late.IsLate(i * Frame + 200_000, i * Frame));
        // Then 5 seconds of frames arriving 170 ms behind (a backlog that does not drain by itself).
        for (; i < 900; i++)
            Assert.True(late.IsLate(i * Frame + 1_700_000, i * Frame));
    }
}
