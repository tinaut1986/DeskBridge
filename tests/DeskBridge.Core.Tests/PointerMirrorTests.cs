using DeskBridge.Core.Input;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class PointerMirrorTests
{
    private static readonly DisplayRect Shown = new(0, 0, 1920, 1080);
    private static readonly DisplayRect Coordinator = new(-1920, 0, 1920, 1080);

    [Fact]
    public void Follows_the_moves_like_the_other_machine()
    {
        var mirror = new PointerMirror();
        mirror.Enter(100, 100, [Shown], [Coordinator], 1, 1, null, null);
        Assert.True(mirror.Move(10, -5));
        Assert.Equal((110, 95), mirror.Position);
    }

    [Fact]
    public void Scaled_moves_keep_the_fractions()
    {
        var mirror = new PointerMirror();
        mirror.Enter(100, 100, [Shown], [Coordinator], 1.5, 1.5, null, null);
        mirror.Move(1, 0);
        mirror.Move(1, 0);
        Assert.Equal((103, 100), mirror.Position);
    }

    [Fact]
    public void Stops_at_an_edge_and_leaves_into_the_coordinator()
    {
        var mirror = new PointerMirror();
        mirror.Enter(5, 1075, [Shown], [Coordinator], 1, 1, null, null);
        mirror.Move(0, 50);
        Assert.Equal((5, 1079), mirror.Position);
        Assert.False(mirror.Move(-20, 0));
        Assert.Null(mirror.Position);
    }

    [Fact]
    public void A_correction_moves_the_copy()
    {
        var mirror = new PointerMirror();
        mirror.Enter(100, 100, [Shown], [Coordinator], 1, 1, null, null);
        mirror.Correct(500, 600);
        mirror.Move(1, 1);
        Assert.Equal((501, 601), mirror.Position);
    }
}
