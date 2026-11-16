using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class PrimaryChoiceTests
{
    // The owner's desktop: 2 on the left, 3 (primary) in the middle, 1 on the right.
    private static readonly Dictionary<string, DisplayRect> Desk = new()
    {
        ["2"] = new(-1920, 0, 1920, 1080),
        ["3"] = new(0, 0, 1920, 1080),
        ["1"] = new(1920, 0, 1920, 1080),
    };

    [Fact]
    public void A_covered_primary_display_moves_to_the_nearest_free_one()
    {
        // 2 and 1 are equally near: the one on the left, so the choice does not flip between runs.
        Assert.Equal("2", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "3" }, null));
        Assert.Equal("1", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "3", "2" }, null));
    }

    [Fact]
    public void The_display_chosen_in_the_profile_wins_unless_it_is_covered()
    {
        Assert.Equal("2", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "3" }, "2"));
        Assert.Equal("1", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "3", "2" }, "2"));
    }

    [Fact]
    public void Without_a_covered_primary_the_original_stays_or_comes_back()
    {
        Assert.Equal("3", PrimaryChoice.Decide(Desk, "3", new HashSet<string>(), null));
        Assert.Equal("3", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "1" }, null));
        Assert.Equal("3", PrimaryChoice.Decide(Desk, "3", new HashSet<string> { "1", "2", "3" }, null)); // nowhere else
    }
}
