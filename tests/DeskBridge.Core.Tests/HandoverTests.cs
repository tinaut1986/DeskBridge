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
    public void A_desktop_display_showing_the_laptop_hands_the_cursor_over_at_the_same_spot()
    {
        // The middle display (3) shows the laptop's virtual display, at (2880,0) right of its own panel.
        var middle = Desktop[1];
        var shown = new DisplayRect(2880, 0, 1920, 1080);
        var lent = InputLayout.Lent("laptop-oscar", middle, shown);
        var h = new CoordinatorHandover(Desktop, [LaptopAt(PlacementSide.Right), lent]);

        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-500, 300)); // display 2: stays here
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(5, 300));
        Assert.Equal((2885, 300), (enter.X, enter.Y));
        Assert.Equal([shown], enter.Area);
        Assert.Equal((960, 540), (enter.ParkX, enter.ParkY)); // hidden behind the video, far from the edges

        // On the laptop the cursor stays on the virtual display: up is the top of everything here...
        var up = ClientHandover.Move(enter.Area!, enter.Peers, 3000, 2, 0, -10);
        Assert.Equal((3000, 0, false), (up.X, up.Y, up.Left));

        // ...and left of it is display 2 of the desktop, where it comes back at that very spot.
        var left = ClientHandover.Move(enter.Area!, enter.Peers, 2885, 300, -20, 0);
        Assert.True(left.Left);
        Assert.Equal((-15, 300), h.Return(0.5, left.X, left.Y));

        // A periodic refresh keeps the cursor with the display target, not the edge one of the same machine.
        h.OnMouseMove(100, 100);
        h.Update(Desktop, [LaptopAt(PlacementSide.Right), InputLayout.Lent("laptop-oscar", middle, shown)]);
        var right = ClientHandover.Move([shown], enter.Peers, 4790, 500, 20, 0);
        Assert.True(right.Left);
        Assert.Equal((1930, 500), h.Return(0.5, right.X, right.Y)); // display 1, right of the middle one
    }

    [Fact]
    public void A_laptop_display_placed_on_the_desk_is_reached_past_the_edge_and_keeps_the_cursor()
    {
        // The laptop panel dragged under the middle display.
        var panel = InputLayout.Desk("laptop-oscar", Laptop[0], new VirtualPosition(0, 1080));
        var h = new CoordinatorHandover(Desktop, [panel]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(100, 1079));
        Assert.Equal((100, 0), (enter.X, enter.Y));
        Assert.Equal(Laptop, enter.Area);

        // Back up into the middle display.
        var up = ClientHandover.Move(enter.Area!, enter.Peers, 100, 3, 0, -10);
        Assert.True(up.Left);
        Assert.Equal(new MouseDecision.Here(100, 1073), h.Back(0.5, up.X, up.Y));
    }

    [Fact]
    public void Two_displays_of_the_same_machine_placed_apart_pass_the_cursor_straight_on()
    {
        // A laptop with an external monitor: the panel under the left display, the monitor under the right one.
        var panel = new DisplayRect(0, 0, 1920, 1080);
        var monitor = new DisplayRect(1920, 0, 1920, 1080);
        var a = InputLayout.Desk("laptop-oscar", panel, new VirtualPosition(-1920, 1080));
        var b = InputLayout.Desk("laptop-oscar", monitor, new VirtualPosition(1920, 1080));
        // Something between them on the desk, so they do not touch.
        var h = new CoordinatorHandover([.. Desktop, new DisplayRect(0, 1080, 1920, 1080)], [a, b]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(-1000, 1079));
        Assert.Equal([panel], enter.Area);

        // Right from the panel: the middle display under the desktop (this PC) — comes back here.
        var right = ClientHandover.Move(enter.Area!, enter.Peers, 1915, 500, 10, 0);
        Assert.True(right.Left);
        Assert.IsType<MouseDecision.Here>(h.Back(0.5, right.X, right.Y));

        // From the middle display right again: the laptop's monitor, with only that one as the area.
        var second = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(1919, 1600));
        Assert.Equal([monitor], second.Area);
        Assert.Equal((1920, 520), (second.X, second.Y));

        // Up from the monitor: display 1 of the desktop, back here at the same spot.
        var up = ClientHandover.Move(second.Area!, second.Peers, 2500, 2, 0, -5);
        Assert.Equal(new MouseDecision.Here(2500, 1077), h.Back(0.5, up.X, up.Y));
    }

    [Fact]
    public void Leaving_one_desk_display_into_another_of_the_same_machine_switches_without_coming_back()
    {
        var panel = new DisplayRect(0, 0, 1920, 1080);
        var monitor = new DisplayRect(1920, 0, 1920, 1080);
        // Both under the desktop, side by side in a different order than on the laptop itself.
        var a = InputLayout.Desk("laptop-oscar", panel, new VirtualPosition(1920, 1080));
        var b = InputLayout.Desk("laptop-oscar", monitor, new VirtualPosition(0, 1080));
        var h = new CoordinatorHandover(Desktop, [a, b]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(2500, 1079)); // onto the panel
        Assert.Equal([panel], enter.Area);
        var left = ClientHandover.Move(enter.Area!, enter.Peers, 3, 500, -10, 0);
        Assert.True(left.Left);
        var on = Assert.IsType<MouseDecision.Enter>(h.Back(0.5, left.X, left.Y));
        Assert.Equal(("laptop-oscar", monitor), (on.Machine, on.Area![0]));
        Assert.Equal((1920 + 1913, 500), (on.X, on.Y)); // right edge of the monitor, same height
        Assert.Equal("laptop-oscar", h.Remote);
    }

    [Fact]
    public void A_bigger_picture_on_a_smaller_display_maps_both_ways_with_the_same_proportions()
    {
        // The laptop's 2880x1800 display shown on the 1920x1080 middle display.
        var lent = InputLayout.Lent("laptop-oscar", Desktop[1], new DisplayRect(0, 0, 2880, 1800));
        var h = new CoordinatorHandover(Desktop, [lent]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(960, 540));
        Assert.Equal((1440, 900), (enter.X, enter.Y));
        Assert.Equal(1.5, enter.SpeedX, 3);       // one pixel here is 1.5 there
        Assert.Equal(1.0 / 0.6, enter.SpeedY, 3);

        // Its left edge leads to display 2, at the same height as seen on the desk.
        var left = ClientHandover.Move(enter.Area!, enter.Peers, 5, 900, -10, 0);
        Assert.True(left.Left);
        Assert.Equal(new MouseDecision.Here(-3, 540), h.Back(0.5, left.X, left.Y));

        // Nothing of the laptop spills over display 1 any more: its right edge is the edge of the middle display.
        h.OnMouseMove(960, 540);
        var right = ClientHandover.Move(enter.Area!, enter.Peers, 2875, 900, 10, 0);
        Assert.True(right.Left);
        Assert.Equal(new MouseDecision.Here(1923, 540), h.Back(0.5, right.X, right.Y));
    }

    [Fact]
    public void A_laptop_panel_at_200_percent_takes_its_visual_size_on_the_desk()
    {
        var size = InputLayout.DeskSize(2880, 1800, 200);
        Assert.Equal((1440, 900), size);
        var panel = InputLayout.Desk("laptop-oscar", Laptop[0], new VirtualPosition(0, 1080), 0.5);
        var h = new CoordinatorHandover(Desktop, [panel]);

        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(1500, 1079)); // past its right end on the desk
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(1000, 1079));
        Assert.Equal((2000, 0), (enter.X, enter.Y));
        Assert.Equal(2, enter.SpeedX);

        var up = ClientHandover.Move(enter.Area!, enter.Peers, 2000, 1, 0, -4);
        Assert.Equal(new MouseDecision.Here(1000, 1078), h.Back(0.5, up.X, up.Y));
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
