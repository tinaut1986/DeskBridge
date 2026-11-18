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

    /// <summary>
    /// The swap: the desktop's virtual display (1920x1200, below its monitors) is shown full screen on the laptop
    /// panel (2880x1800, scale 1.5), and a laptop window at (288,180) 960x600 on the panel sits over that picture.
    /// </summary>
    private static readonly DisplayRect DesktopVirtual = new(-1920, 1080, 1920, 1200);

    private static InputTarget LaptopWindow(DisplayRect onPanel) =>
        InputLayout.Lent("laptop-oscar",
            new DisplayRect(DesktopVirtual.X + onPanel.X * 2 / 3, DesktopVirtual.Y + onPanel.Y * 2 / 3, onPanel.Width * 2 / 3, onPanel.Height * 2 / 3),
            onPanel, island: true);

    [Fact]
    public void A_laptop_window_over_the_desktop_picture_on_its_panel_takes_the_cursor_and_gives_it_back_past_its_edge()
    {
        var displays = Desktop.Append(DesktopVirtual).ToArray();
        var window = LaptopWindow(new DisplayRect(288, 180, 960, 600)); // on the desktop: (-1728,1200) 640x400
        var h = new CoordinatorHandover(displays, [window]);

        // On the desktop's virtual display, off the window: the desktop keeps the cursor (it is not a display
        // showing another machine, where it would stay only over its own windows).
        Assert.False(h.IsOverLent(-1800, 1150));
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-1800, 1150));

        // Onto the window: the laptop gets it there, at that point of its panel.
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(-1700, 1250));
        Assert.Equal("laptop-oscar", enter.Machine);
        Assert.Equal((330, 255), (enter.X, enter.Y));

        // The window moves while the cursor is on the laptop: the cursor stays there.
        h.Update(displays, [LaptopWindow(new DisplayRect(600, 400, 960, 600))]);
        Assert.Equal("laptop-oscar", h.Remote);

        // Past the window's left edge, still on the panel: back on the desktop's virtual display, through the
        // panel's scale (not through any other display of the laptop).
        var left = ClientHandover.Move(enter.Area!, enter.Peers, 290, 300, -10, 0);
        Assert.True(left.Left);
        Assert.Equal(DesktopVirtual.X + (int)Math.Round(280 * 2 / 3.0), h.Return(0.5, left.X, left.Y).X);
        Assert.Null(h.Remote);
    }

    [Fact]
    public void Into_a_desktop_window_over_the_laptop_picture_the_cursor_comes_back_at_that_spot_even_with_laptop_windows_elsewhere()
    {
        // The middle display shows the laptop's virtual display, a desktop window covers part of it; meanwhile a
        // laptop window sits over the desktop's picture on the laptop panel (an island, somewhere else entirely).
        var middle = Desktop[1];
        var shown = new DisplayRect(2880, 0, 1920, 1080);
        var displays = Desktop.Append(DesktopVirtual).ToArray();
        var h = new CoordinatorHandover(displays,
            [LaptopWindow(new DisplayRect(0, 0, 737, 3)), InputLayout.Lent("laptop-oscar", middle, shown)])
        {
            WindowsOverVideo = () => [new DisplayRect(500, 200, 800, 600)],
        };

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(400, 300));
        Assert.Equal([new DisplayRect(3380, 200, 800, 600)], enter.Holes);

        // Into the window from its left: back on the desktop right there, mapped through the middle display
        // (not through the laptop window over the picture on its panel).
        var into = ClientHandover.Move(enter.Area!, enter.Peers, 3375, 300, 10, 0, enter.Holes);
        Assert.True(into.Left);
        Assert.Equal((505, 300), h.Return(0.5, into.X, into.Y));
    }

    [Fact]
    public void A_laptop_window_dragged_back_here_goes_onto_the_laptop_panel_over_the_desktop_picture_there()
    {
        // The swap: the middle display shows the laptop's virtual display; the laptop panel (2880x1800) shows the
        // desktop's virtual display (1920x1200, below its monitors).
        var shown = new DisplayRect(2880, 0, 1920, 1080);
        var panel = new DisplayRect(0, 0, 2880, 1800);
        var displays = Desktop.Append(DesktopVirtual).ToArray();
        var picture = InputLayout.Lent("laptop-oscar", DesktopVirtual, panel, whileDragging: true);
        var h = new CoordinatorHandover(displays, [picture, InputLayout.Lent("laptop-oscar", Desktop[1], shown)]);

        // No drag: the desktop's virtual display is the desktop's.
        Assert.False(h.IsOverLent(-960, 1680));
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(-960, 1680));

        // A window of the laptop dragged here (its button kept held there): over that picture the laptop gets the
        // cursor at that point of its panel, and keeps it on the panel.
        h.DragHeldOn = "laptop-oscar";
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(-960, 1680));
        Assert.Equal("laptop-oscar", enter.Machine);
        Assert.Equal((1440, 900), (enter.X, enter.Y));
        Assert.Equal([panel], enter.Area);
        Assert.Equal([DesktopVirtual], enter.AreaDesk);

        // Coming back from the panel (let go and moved off the window): at that point of the picture.
        h.DragHeldOn = null;
        Assert.Equal((-1920 + 200, 1080 + 100), h.Return(0.5, 300, 150));
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
    public void Past_the_right_of_a_shown_display_the_cursor_comes_back_right_not_onto_the_machines_next_display()
    {
        // Display 3 (the middle one) shows the laptop's virtual display v1; its v2, right next to v1 on the laptop,
        // is shown on a tablet placed BELOW display 3 on the desk (relayed through the desktop).
        var middle = Desktop[1];
        var v1 = new DisplayRect(2144, -1080, 1920, 1080);
        var v2 = new DisplayRect(4064, -1080, 2000, 1200);
        var lent = InputLayout.Lent("laptop-oscar", middle, v1);
        var tablet = InputLayout.Desk("laptop-oscar", v2, new VirtualPosition(491, 1080), 1332.0 / 2000);
        var h = new CoordinatorHandover(Desktop, [lent, tablet]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(1000, 300));
        // On the laptop, right of v1 is v2 there, but on the desk it is display 1: back to the desktop...
        var right = ClientHandover.Move(enter.Area!, enter.Peers, v1.Right - 1, -800, 20, 0, desk: enter.AreaDesk);
        Assert.True(right.Left);
        // ...and it lands on display 1 at that height, not next to the tablet below display 3.
        var back = Assert.IsType<MouseDecision.Here>(h.Back(0.5, right.X, right.Y));
        Assert.True(back.X >= 1920 && back.Y < 1080, $"landed at {back.X},{back.Y}");
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
    public void Between_displays_of_the_same_machine_the_cursor_moves_over_there_so_dragging_works()
    {
        // A laptop with an external monitor, both placed under the desktop's displays.
        var panel = new DisplayRect(0, 0, 1920, 1080);
        var monitor = new DisplayRect(1920, 0, 1920, 1080);
        var a = InputLayout.Desk("laptop-oscar", panel, new VirtualPosition(-1920, 1080));
        var b = InputLayout.Desk("laptop-oscar", monitor, new VirtualPosition(0, 1080));
        var h = new CoordinatorHandover(Desktop, [a, b]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(-1000, 1079));
        Assert.Equal([panel, monitor], enter.Area); // both: the laptop moves its own cursor (and windows) between them

        var right = ClientHandover.Move(enter.Area!, enter.Peers, 1915, 500, 10, 0);
        Assert.Equal((1925, 500, false), (right.X, right.Y, right.Left)); // on its monitor, without coming back

        // Up from the monitor: display 3 of the desktop, mapped through the monitor's own place on the desk.
        var up = ClientHandover.Move(enter.Area!, enter.Peers, 2500, 2, 0, -5);
        Assert.True(up.Left);
        Assert.Equal(new MouseDecision.Here(580, 1077), h.Back(0.5, up.X, up.Y));
    }

    [Fact]
    public void A_virtual_display_shown_above_the_laptop_panel_is_part_of_the_laptop_area()
    {
        // Display 3 shows the laptop's virtual display, placed on the laptop above its panel; the panel is under display 3.
        var virtualDisplay = new DisplayRect(0, -1080, 1920, 1080);
        var lent = InputLayout.Lent("laptop-oscar", Desktop[1], virtualDisplay);
        var panel = InputLayout.Desk("laptop-oscar", new DisplayRect(0, 0, 2880, 1800), new VirtualPosition(0, 1080), 1920.0 / 2880);
        var h = new CoordinatorHandover(Desktop, [lent, panel]);

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(500, 300));
        Assert.Equal((500, -780), (enter.X, enter.Y));
        Assert.Contains(virtualDisplay, enter.Area!);
        Assert.Contains(new DisplayRect(0, 0, 2880, 1800), enter.Area!);

        // Down from the virtual display onto the panel: still the laptop, a window can be dragged across.
        var down = ClientHandover.Move(enter.Area!, enter.Peers, 500, -2, 0, 10);
        Assert.Equal((500, 8, false), (down.X, down.Y, down.Left));

        // Left from the virtual display: display 2, at the height seen there (its own scale, not the panel's).
        var left = ClientHandover.Move(enter.Area!, enter.Peers, 3, -500, -10, 0);
        Assert.True(left.Left);
        Assert.Equal(new MouseDecision.Here(-7, 580), h.Back(0.5, left.X, left.Y));
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
    public void While_something_is_dragged_here_the_cursor_crosses_a_lent_display_without_leaving()
    {
        var lent = InputLayout.Lent("laptop-oscar", Desktop[1], new DisplayRect(0, -1080, 1920, 1080));
        var h = new CoordinatorHandover(Desktop, [lent]);

        // Dragging a window from display 2 to display 1 across display 3 (which shows the laptop): stays here.
        Assert.IsType<MouseDecision.Pass>(h.OnMouseMove(500, 300, dragging: true));
        Assert.Null(h.Remote);
        // Dropped there, the next move hands the cursor over as usual.
        Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(501, 300));
    }

    [Fact]
    public void A_window_of_this_PC_over_the_video_takes_the_cursor_back_when_reached_from_the_other_machine()
    {
        var shown = new DisplayRect(0, -1080, 1920, 1080);
        var lent = InputLayout.Lent("laptop-oscar", Desktop[1], shown);
        // A window left on display 3, over the laptop's video.
        var h = new CoordinatorHandover(Desktop, [lent]) { WindowsOverVideo = () => [new DisplayRect(1000, 200, 600, 400)] };

        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(100, 100));
        var hole = Assert.Single(enter.Holes!);
        Assert.Equal(new DisplayRect(1000, -880, 600, 400), hole);

        // Moving on the laptop into that window's area hands the cursor back, right there.
        var into = ClientHandover.Move(enter.Area!, enter.Peers, 990, -700, 20, 0, enter.Holes);
        Assert.True(into.Left);
        Assert.Equal(new MouseDecision.Here(1010, 380), h.Back(0.5, into.X, into.Y));
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
    [Fact]
    public void Crossing_between_that_machines_displays_follows_the_desk()
    {
        // The laptop's virtual display (1920x1080, above its panel in its own desktop) shown on display 3, and its
        // 2880x1800 panel placed 1920 wide right below display 3.
        DisplayRect desk3 = new(0, 0, 1920, 1080);
        DisplayRect virtualDisplay = new(0, -1080, 1920, 1080), panel = new(0, 0, 2880, 1800);
        var h = new CoordinatorHandover([new(-1920, 0, 1920, 1080), desk3, new(1920, 0, 1920, 1080)],
        [
            InputLayout.Lent("laptop", desk3, virtualDisplay),
            InputLayout.Desk("laptop", panel, new VirtualPosition(0, 1080), 1920.0 / 2880),
        ]);
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(1900, 500));
        Assert.NotNull(enter.AreaDesk);

        // Down at the right of display 3: at the right of the panel too (not at 1900 of its 2880 pixels).
        var down = ClientHandover.Move(enter.Area!, enter.Peers, 1900, -1, 0, 2, null, enter.AreaDesk);
        Assert.False(down.Left);
        Assert.InRange(down.X, 2845, 2855);
        Assert.InRange(down.Y, 0, 3);

        // And back up from there: the same spot of display 3's picture.
        var up = ClientHandover.Move(enter.Area!, enter.Peers, 2850, 1, 0, -3, null, enter.AreaDesk);
        Assert.InRange(up.X, 1895, 1905);
        Assert.InRange(up.Y, -3, -1);
    }

    [Fact]
    public void A_drag_jumps_into_the_nearest_display_of_that_machine_past_an_empty_gap()
    {
        // The laptop's virtual display shown on display 3, and its panel (1920 wide on the desk) below the right
        // half of display 2 and the left half of display 3.
        DisplayRect desk3 = new(0, 0, 1920, 1080);
        DisplayRect virtualDisplay = new(0, -1080, 1920, 1080), panel = new(0, 0, 2880, 1800);
        var h = new CoordinatorHandover([new(-1920, 0, 1920, 1080), desk3, new(1920, 0, 1920, 1080)],
        [
            InputLayout.Lent("laptop", desk3, virtualDisplay),
            InputLayout.Desk("laptop", panel, new VirtualPosition(-960, 1080), 1920.0 / 2880),
        ]);
        var enter = Assert.IsType<MouseDecision.Enter>(h.OnMouseMove(1500, 500));

        // Down at the right of display 3, past the panel (nothing there): without a button it stops at the edge...
        var free = ClientHandover.Move(enter.Area!, enter.Peers, 1500, -1, 0, 3, null, enter.AreaDesk);
        Assert.False(free.Left);
        Assert.Equal((1500, -1), (free.X, free.Y));
        // ...dragging, it lands on the panel's top edge, at its right end (desk x 959).
        var drag = ClientHandover.Move(enter.Area!, enter.Peers, 1500, -1, 0, 3, null, enter.AreaDesk, dragging: true);
        Assert.False(drag.Left);
        Assert.InRange(drag.X, 2870, 2879);
        Assert.InRange(drag.Y, 0, 3);

        // Up from the left half of the panel, into display 2 (the desktop's own): it goes back to the desktop
        // either way (dragging, the coordinator keeps the drag going and brings it back here).
        Assert.True(ClientHandover.Move(enter.Area!, enter.Peers, 300, 1, 0, -3, null, enter.AreaDesk, dragging: true).Left);
        Assert.True(ClientHandover.Move(enter.Area!, enter.Peers, 300, 1, 0, -3, null, enter.AreaDesk).Left);
    }
}
