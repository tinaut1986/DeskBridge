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
/// <param name="ScaleX">
/// Desk units (the coordinator's pixels) per pixel of that machine: below 1 for a display scaled up by Windows
/// (a 2880-pixel panel at 200% takes 1440 on the desk, like its visual size), or for a bigger picture shown on a
/// smaller display here. Coordinate on the desk = coordinate there × scale + offset.
/// </param>
/// <param name="WhileDragging">
/// An island that only counts while a drag of that machine is held (the cursor came back here mid-drag): the whole
/// picture of this PC that machine shows, so a window of it can be dragged onto that display.
/// </param>
public sealed record InputTarget(string Machine, IReadOnlyList<DisplayRect> Displays, int OffsetX, int OffsetY, DisplayRect? Lent = null,
    bool Confine = false, double ScaleX = 1, double ScaleY = 1, bool Island = false, bool WhileDragging = false)
{
    /// <summary>The same target after a refresh (lists are rebuilt, so records do not compare equal).</summary>
    public bool SameAs(InputTarget other) =>
        Machine == other.Machine && Lent == other.Lent && Confine == other.Confine && Island == other.Island && WhileDragging == other.WhileDragging && OffsetX == other.OffsetX
        && OffsetY == other.OffsetY && ScaleX == other.ScaleX && ScaleY == other.ScaleY && Displays.SequenceEqual(other.Displays);

    /// <summary>A point of that machine on the coordinator's desk.</summary>
    public (int X, int Y) ToDesk(int x, int y) => ((int)Math.Round(x * ScaleX) + OffsetX, (int)Math.Round(y * ScaleY) + OffsetY);

    /// <summary>A point of the coordinator's desk in that machine's coordinates.</summary>
    public (int X, int Y) ToClient(int x, int y) => ((int)Math.Floor((x - OffsetX) / ScaleX), (int)Math.Floor((y - OffsetY) / ScaleY));

    public DisplayRect ToDesk(DisplayRect r) => new((int)Math.Round(r.X * ScaleX) + OffsetX, (int)Math.Round(r.Y * ScaleY) + OffsetY,
        (int)Math.Round(r.Width * ScaleX), (int)Math.Round(r.Height * ScaleY));

    public DisplayRect ToClient(DisplayRect r) => new((int)Math.Floor((r.X - OffsetX) / ScaleX), (int)Math.Floor((r.Y - OffsetY) / ScaleY),
        (int)Math.Round(r.Width / ScaleX), (int)Math.Round(r.Height / ScaleY));

    /// <summary>A point of the coordinator's desktop is over one of this machine's displays.</summary>
    public bool Contains(int x, int y)
    {
        var (cx, cy) = ToClient(x, y);
        return InputLayout.Contains(Displays, cx, cy);
    }

    /// <summary>This machine's displays in the coordinator's desktop coordinates.</summary>
    public IReadOnlyList<DisplayRect> Placed => Displays.Select(ToDesk).ToList();
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
    /// <param name="scale">Desk units per pixel of that display: 100 / its Windows scale in percent.</param>
    public static InputTarget Desk(string machine, DisplayRect display, VirtualPosition position, double scale = 1) =>
        new(machine, [display], position.X - (int)Math.Round(display.X * scale), position.Y - (int)Math.Round(display.Y * scale),
            Confine: true, ScaleX: scale, ScaleY: scale);

    /// <summary>
    /// How big a display of another machine is on the coordinator's desk: the width chosen for it (the height in
    /// proportion), or else its size as Windows scales it.
    /// </summary>
    public static (int Width, int Height) DeskSize(int width, int height, int scalePercent, int? deskWidth = null)
    {
        if (deskWidth is > 0 and var w && width > 0)
            return (w, (int)Math.Round((double)height * w / width));
        return scalePercent is > 0 and not 100
            ? ((int)Math.Round(width * 100.0 / scalePercent), (int)Math.Round(height * 100.0 / scalePercent))
            : (width, height);
    }

    /// <summary>Desk units per pixel of that display (the scale of its input target).</summary>
    public static double DeskScale(int width, int height, int scalePercent, int? deskWidth = null) =>
        width > 0 ? (double)DeskSize(width, height, scalePercent, deskWidth).Width / width : 1;

    /// <summary>
    /// A display of the coordinator (<paramref name="lent"/>) showing <paramref name="shown"/>, a display of
    /// <paramref name="machine"/>: entering it moves the cursor there.
    /// </summary>
    /// <param name="island">
    /// Not a display showing that machine but one of its windows seen here over a display of this PC (on that
    /// machine's own display that shows this PC): the cursor goes to it there, and comes back past its edges.
    /// </param>
    /// <param name="whileDragging">An island over the whole picture that only counts while a drag of that machine is held.</param>
    public static InputTarget Lent(string machine, DisplayRect lent, DisplayRect shown, bool island = false, bool whileDragging = false)
    {
        // The picture fills the display here (scaled as needed): the same proportions both ways.
        double sx = (double)lent.Width / shown.Width, sy = (double)lent.Height / shown.Height;
        return new(machine, [shown], lent.X - (int)Math.Round(shown.X * sx), lent.Y - (int)Math.Round(shown.Y * sy), lent,
            ScaleX: sx, ScaleY: sy, Island: island || whileDragging, WhileDragging: whileDragging);
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
