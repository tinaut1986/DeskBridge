using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Input;

/// <summary>
/// A machine that can get the cursor, as the coordinator sees it: its displays in its own desktop
/// coordinates and where they sit in the coordinator's (its coordinate + offset = coordinator coordinate).
/// </summary>
public sealed record InputTarget(string Machine, IReadOnlyList<DisplayRect> Displays, int OffsetX, int OffsetY)
{
    /// <summary>A point of the coordinator's desktop is over one of this machine's displays.</summary>
    public bool Contains(int x, int y) => InputLayout.Contains(Displays, x - OffsetX, y - OffsetY);

    /// <summary>This machine's displays in the coordinator's desktop coordinates.</summary>
    public IReadOnlyList<DisplayRect> Placed => Displays.Select(d => d with { X = d.X + OffsetX, Y = d.Y + OffsetY }).ToList();
}

/// <summary>
/// Where each machine's displays go next to the coordinator's for keyboard and mouse sharing: placed by hand
/// anywhere (say, below the desktop's displays), or on one side, aligned to the top/left.
/// </summary>
public static class InputLayout
{
    public static bool Contains(IReadOnlyList<DisplayRect> displays, int x, int y) =>
        displays.Any(d => x >= d.X && x < d.Right && y >= d.Y && y < d.Bottom);

    /// <summary>The rectangle around all the displays.</summary>
    public static DisplayRect Bounds(IReadOnlyList<DisplayRect> displays)
    {
        int minX = displays.Min(d => d.X), minY = displays.Min(d => d.Y);
        return new DisplayRect(minX, minY, displays.Max(d => d.Right) - minX, displays.Max(d => d.Bottom) - minY);
    }

    /// <summary>Top-left corner of a machine's displays next to the coordinator's on that side.</summary>
    public static (int X, int Y) DefaultPosition(IReadOnlyList<DisplayRect> coordinator, IReadOnlyList<DisplayRect> machine, PlacementSide side)
    {
        var c = Bounds(coordinator);
        var m = Bounds(machine);
        return side switch
        {
            PlacementSide.Left => (c.X - m.Width, c.Y),
            PlacementSide.Above => (c.X, c.Y - m.Height),
            PlacementSide.Below => (c.X, c.Bottom),
            _ => (c.Right, c.Y),
        };
    }

    /// <summary>
    /// The target for a machine: its displays' top-left corner at <paramref name="position"/> in the
    /// coordinator's desktop (when placed by hand), otherwise on <paramref name="side"/>.
    /// </summary>
    public static InputTarget Target(string machine, IReadOnlyList<DisplayRect> coordinator, IReadOnlyList<DisplayRect> displays,
        PlacementSide side, VirtualPosition? position)
    {
        var (x, y) = position is { } p ? (p.X, p.Y) : DefaultPosition(coordinator, displays, side);
        var bounds = Bounds(displays);
        return new InputTarget(machine, displays, x - bounds.X, y - bounds.Y);
    }
}
