using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class ArrangementTests
{
    private static readonly DisplayRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Dropped_near_the_right_edge_snaps_against_it_and_aligns_the_tops()
    {
        var snapped = Arrangement.Snap(new DisplayRect(1950, 40, 1920, 1080), [Screen], threshold: 100);
        Assert.Equal(new DisplayRect(1920, 0, 1920, 1080), snapped);
    }

    [Fact]
    public void Dropped_below_snaps_below_and_aligns_the_left_edges()
    {
        var snapped = Arrangement.Snap(new DisplayRect(60, 1150, 1920, 1080), [Screen], threshold: 100);
        Assert.Equal(new DisplayRect(0, 1080, 1920, 1080), snapped);
    }

    [Fact]
    public void Far_from_the_ends_it_keeps_its_offset_along_the_edge()
    {
        var snapped = Arrangement.Snap(new DisplayRect(1900, 500, 1280, 720), [Screen], threshold: 50);
        Assert.Equal(new DisplayRect(1920, 500, 1280, 720), snapped);
    }

    [Fact]
    public void Dropped_on_top_of_a_display_moves_to_the_nearest_free_edge()
    {
        var other = new DisplayRect(1920, 0, 1920, 1080);
        var snapped = Arrangement.Snap(new DisplayRect(1700, 0, 1920, 1080), [Screen, other], threshold: 50);

        Assert.False(snapped.Overlaps(Screen) || snapped.Overlaps(other));
        Assert.True(snapped.Touches(Screen) || snapped.Touches(other));
    }

    [Fact]
    public void Touching_means_sharing_an_edge_not_a_corner()
    {
        Assert.True(Screen.Touches(new DisplayRect(1920, 500, 100, 100)));
        Assert.False(Screen.Touches(new DisplayRect(1920, 1080, 100, 100)));
        Assert.False(Screen.Touches(new DisplayRect(2000, 0, 100, 100)));
    }

    [Fact]
    public void A_hand_placed_virtual_display_goes_exactly_there_and_the_automatic_ones_go_around_it()
    {
        var config = LayoutPlannerTests.Example();
        var work = config.Profiles.Single(p => p.Name == "work");
        // Desktop display 3 shown on the laptop, placed by hand below the laptop's own screen.
        config = config with
        {
            Profiles =
            [
                work with
                {
                    Assignments = work.Assignments
                        .Select(a => a.Display == "3" && a.Machine == "desktop" ? a with { Position = new VirtualPosition(0, 1080) } : a)
                        .ToList(),
                },
            ],
        };

        var plan = LayoutPlanner.Plan(config, "work");
        var laptop = plan.VirtualDisplaysOf("laptop").ToDictionary(v => v.ForDisplay);

        Assert.Equal((0, 1080), (laptop["3"].X, laptop["3"].Y));
        // Display 2 is still automatic: to the right of everything there (screen + hand-placed one).
        Assert.Equal((1920, 0), (laptop["2"].X, laptop["2"].Y));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("overlaps") || w.Contains("does not touch"));
    }

    [Fact]
    public void Overlapping_or_floating_virtual_displays_are_warned()
    {
        var config = LayoutPlannerTests.Example();
        config = config with
        {
            Profiles =
            [
                new("p", [new DisplayAssignment("laptop", "1", "desktop", Position: new VirtualPosition(100, 100))]),
                new("q", [new DisplayAssignment("laptop", "1", "desktop", Position: new VirtualPosition(9000, 0))]),
            ],
        };

        Assert.Contains(LayoutPlanner.Plan(config, "p").Warnings, w => w.Contains("overlaps"));
        Assert.Contains(LayoutPlanner.Plan(config, "q").Warnings, w => w.Contains("does not touch"));
    }
}
