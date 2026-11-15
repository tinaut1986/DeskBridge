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

    [Fact]
    public void Touching_the_right_edge_hands_the_cursor_to_the_laptop_and_moves_become_relative()
    {
        var h = new CoordinatorHandover(Desktop, [("laptop-oscar", PlacementSide.Right)]);

        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3000, 540));
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-1920, 540)); // left edge: nobody there

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(3839, 540));
        Assert.Equal("laptop-oscar", enter.Machine);
        Assert.Equal(0.5, enter.Fraction, 2);
        Assert.Equal("laptop-oscar", h.Remote);

        // While away, the local cursor stays parked: each move is the offset from the park point.
        var forward = Assert.IsType<MouseDecision.Forward>(h.OnMouseMove(enter.ParkX + 7, enter.ParkY - 3));
        Assert.Equal((7, -3), (forward.Dx, forward.Dy));

        var (x, y) = h.Return(0.25);
        Assert.Null(h.Remote);
        Assert.Equal(3837, x);
        Assert.Equal(270, y);
    }

    [Fact]
    public void The_laptop_takes_the_cursor_at_its_left_edge_and_gives_it_back_there()
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
    public void Losing_the_target_brings_the_cursor_back()
    {
        var h = new CoordinatorHandover(Desktop, [("laptop-oscar", PlacementSide.Right)]);
        h.OnMouseMove(3839, 10);
        Assert.NotNull(h.Remote);

        h.Update(Desktop, []);
        Assert.Null(h.Remote);
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(3000, 10));
    }
}
