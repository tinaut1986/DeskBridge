using DeskBridge.Core.Input;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class HandoverTests
{
    /// <summary>The owner's desktop: display 2 at the left, 3 in the middle, 1 at the right, all 1080p.</summary>
    private static readonly DisplayRect[] Desktop =
    [
        new(-1920, 0, 1920, 1080),
        new(0, 0, 1920, 1080),
        new(1920, 0, 1920, 1080),
    ];

    /// <summary>The laptop: one 2880x1800 panel.</summary>
    private static readonly DisplayRect[] Laptop = [new(0, 0, 2880, 1800)];

    [Fact]
    public void Edges_and_fractions_of_the_desktop()
    {
        Assert.Equal(3839, EdgeGeometry.OuterEdge(Desktop, PlacementSide.Right));
        Assert.Equal(-1920, EdgeGeometry.OuterEdge(Desktop, PlacementSide.Left));
        Assert.True(EdgeGeometry.AtEdge(Desktop, PlacementSide.Right, 3839, 500));
        Assert.False(EdgeGeometry.AtEdge(Desktop, PlacementSide.Right, 3838, 500));
        Assert.Equal(0.5, EdgeGeometry.Fraction(Desktop, PlacementSide.Right, 3839, 540), 2);
        Assert.Equal((3838, 0), EdgeGeometry.PointOnEdge(Desktop, PlacementSide.Right, 0));
        // Above: the whole top edge, from -1920 to 3840.
        Assert.Equal((-1920, 3840), EdgeGeometry.Span(Desktop, PlacementSide.Above));
    }

    private static InputTarget LaptopAt(PlacementSide side, VirtualPosition? position = null) =>
        InputLayout.Target("laptop-oscar", Desktop, Laptop, side, position);

    [Fact]
    public void Touching_the_right_edge_hands_the_cursor_to_the_laptop_and_moves_become_relative()
    {
        var h = new CoordinatorHandover(Desktop, [LaptopAt(PlacementSide.Right)]);

        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3000, 540));
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-1920, 540)); // left edge: nobody there
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(1919, 540));  // between two displays of the desktop

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(3839, 540));
        Assert.Equal("laptop-oscar", enter.Machine);
        Assert.Equal((0, 540), (enter.X, enter.Y));
        Assert.Equal(PlacementSide.Right, enter.Side);
        Assert.Equal(0.5, enter.Fraction, 2);
        Assert.Equal((3838, 540), (enter.ParkX, enter.ParkY));
        Assert.Equal(new DisplayRect(-1920, 0, 1920, 1080), enter.Peers[2]); // display 1 seen from the laptop
        Assert.Equal("laptop-oscar", h.Remote);

        // While away, the local cursor stays parked: each move is the offset from the park point.
        var forward = Assert.IsType<MouseDecision.Forward>(h.OnMouseMove(enter.ParkX + 7, enter.ParkY - 3));
        Assert.Equal((7, -3), (forward.Dx, forward.Dy));

        // Back at the point where it crossed into the desktop's displays on the laptop.
        Assert.Equal((3830, 450), h.Return(0.5, -10, 450));
        Assert.Null(h.Remote);
    }

    [Fact]
    public void The_laptop_can_be_placed_below_the_desktop()
    {
        var below = LaptopAt(PlacementSide.Right, new VirtualPosition(0, 1080)); // under the middle display
        var h = new CoordinatorHandover(Desktop, [below]);

        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3839, 540));  // nothing on the right any more
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-500, 1079)); // under display 2: nothing there
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3000, 1079)); // past the laptop's right end

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(2000, 1079)); // the laptop reaches under display 1 too
        Assert.Equal((2000, 0), (enter.X, enter.Y));
        Assert.Equal(PlacementSide.Below, enter.Side);
        Assert.Equal((2000, 1078), (enter.ParkX, enter.ParkY));

        // On the laptop, going up past its top edge goes back into the desktop.
        var up = ClientHandover.Move(Laptop, enter.Peers, 100, 5, 0, -10);
        Assert.True(up.Left);
        Assert.Equal((100, 1075), h.Return(0.5, up.X, up.Y));

        // Going left from the laptop leads nowhere: the cursor stops at the edge.
        var left = ClientHandover.Move(Laptop, enter.Peers, 3, 900, -20, 0);
        Assert.Equal((0, 900, false), (left.X, left.Y, left.Left));

        // Diagonally past the top-left corner: straight into display 2, above and to the left.
        h.OnMouseMove(500, 1079);
        var corner = ClientHandover.Move(Laptop, enter.Peers, 2, 2, -10, -10);
        Assert.True(corner.Left);
        Assert.Equal((-8, 1072), h.Return(0.5, corner.X, corner.Y));

        // Up and to the right from the top-right corner: under display 1, so into it.
        h.OnMouseMove(500, 1079);
        var right = ClientHandover.Move(Laptop, enter.Peers, 2878, 3, 10, -10);
        Assert.True(right.Left);
        Assert.Equal((2888, 1073), h.Return(0.5, right.X, right.Y));
    }

    [Fact]
    public void Older_coordinators_still_work_by_side()
    {
        var (x, y) = ClientHandover.Enter(Laptop, PlacementSide.Right, 0.5);
        Assert.Equal((1, 900), (x, y));

        var inside = ClientHandover.Move(Laptop, PlacementSide.Right, x, y, 500, 100);
        Assert.Equal((501, 1000, false), (inside.X, inside.Y, inside.Left));

        var clamped = ClientHandover.Move(Laptop, PlacementSide.Right, 2800, 1700, 500, 500);
        Assert.Equal((2879, 1799, false), (clamped.X, clamped.Y, clamped.Left));

        var back = ClientHandover.Move(Laptop, PlacementSide.Right, 10, 450, -20, 0);
        Assert.True(back.Left);
        Assert.Equal(0.25, back.Fraction, 2);
    }

    [Fact]
    public void Default_positions_are_on_each_side_aligned_to_the_top_or_left()
    {
        Assert.Equal((3840, 0), InputLayout.DefaultPosition(Desktop, Laptop, PlacementSide.Right));
        Assert.Equal((-4800, 0), InputLayout.DefaultPosition(Desktop, Laptop, PlacementSide.Left));
        Assert.Equal((-1920, -1800), InputLayout.DefaultPosition(Desktop, Laptop, PlacementSide.Above));
        Assert.Equal((-1920, 1080), InputLayout.DefaultPosition(Desktop, Laptop, PlacementSide.Below));
    }

    [Fact]
    public void Losing_the_target_brings_the_cursor_back()
    {
        var h = new CoordinatorHandover(Desktop, [LaptopAt(PlacementSide.Right)]);
        h.OnMouseMove(3839, 10);
        Assert.NotNull(h.Remote);

        h.Update(Desktop, []);
        Assert.Null(h.Remote);
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3000, 10));
    }
}
