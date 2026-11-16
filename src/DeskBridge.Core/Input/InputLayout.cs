using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Input;

/// <summary>
/// A machine that can get the cursor, as the coordinator sees it: its displays in its own desktop
/// coordinates and where they sit in the coordinator's (its coordinate + offset = coordinator coordinate).
/// </summary>
/// <param name="Lent">
/// Set for a display of the coordinator that shows this machine's screen: the cursor goes to that machine as
/// soon as it enters it, at the same spot of <paramref name="Displays"/> (its one display shown there), and
/// stays within it.
/// </param>
/// <param name="Confine">Over there the cursor stays within <paramref name="Displays"/> (one display placed on its own).</param>
public sealed record InputTarget(string Machine, IReadOnlyList<DisplayRect> Displays, int OffsetX, int OffsetY, DisplayRect? Lent = null,
    bool Confine = false)
{
    /// <summary>The same target after a refresh (lists are rebuilt, so records do not compare equal).</summary>
    public bool SameAs(InputTarget other) =>
        Machine == other.Machine && Lent == other.Lent && Confine == other.Confine && OffsetX == other.OffsetX
        && OffsetY == other.OffsetY && Displays.SequenceEqual(other.Displays);

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
    /// Where a machine's displays are on the coordinator's desk: as saved, or, from older versions that placed
    /// the whole machine for the mouse (by hand or on a side), each display at its spot in that placement.
    /// None for a machine that was never placed.
    /// </summary>
    public static Dictionary<string, VirtualPosition> DeskPositions(MachineConfig machine, IReadOnlyList<DisplayRect> coordinator)
    {
        if (machine.DeskPositions is { } saved)
            return new Dictionary<string, VirtualPosition>(saved);
        if ((machine.InputPosition is null && machine.InputSide is null) || machine.Displays.Count == 0 || coordinator.Count == 0)
            return [];
        var rects = machine.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList();
        var target = Target(machine.Id, coordinator, rects, machine.InputSide ?? PlacementSide.Right, machine.InputPosition);
        return machine.Displays.ToDictionary(d => d.Id, d => new VirtualPosition(d.X + target.OffsetX, d.Y + target.OffsetY));
    }

    /// <summary>
    /// One display of another machine placed on the coordinator's desk with its top-left corner at
    /// <paramref name="position"/>: the cursor reaches it past the edge where it touches the coordinator's
    /// displays and stays on it until it is pushed back out.
    /// </summary>
    public static InputTarget Desk(string machine, DisplayRect display, VirtualPosition position) =>
        new(machine, [display], position.X - display.X, position.Y - display.Y, Confine: true);

    /// <summary>
    /// A display of the coordinator (<paramref name="lent"/>) showing <paramref name="shown"/>, a display of
    /// <paramref name="machine"/>: entering it moves the cursor there.
    /// </summary>
    public static InputTarget Lent(string machine, DisplayRect lent, DisplayRect shown) =>
        new(machine, [shown], lent.X - shown.X, lent.Y - shown.Y, lent);

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
