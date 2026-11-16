using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Input;

/// <summary>What the coordinator does with a mouse move seen by its hook.</summary>
public abstract record MouseDecision
{
    /// <summary>Let it through: the cursor stays on this PC.</summary>
    public sealed record Pass : MouseDecision;

    /// <summary>
    /// The cursor crossed to <paramref name="Machine"/> at (<paramref name="X"/>, <paramref name="Y"/>) in that
    /// machine's own coordinates; <paramref name="Peers"/> are this PC's displays in those coordinates (where
    /// the cursor comes back). Swallow, and keep the local cursor parked at (ParkX, ParkY).
    /// </summary>
    /// <param name="Side">The direction it crossed in, and <paramref name="Fraction"/> along that edge (for older versions).</param>
    /// <param name="Area">Where the cursor may go over there; null: all that machine's displays.</param>
    /// <param name="SpeedX">Pixels there per pixel moved here (a display scaled up by Windows moves faster in pixels).</param>
    /// <param name="Holes">Parts of the area covered here by this PC's windows on top of the video (that machine's coordinates).</param>
    /// <param name="AreaDesk">Where each display of <paramref name="Area"/> is on this PC's desk (same order).</param>
    public sealed record Enter(string Machine, int X, int Y, IReadOnlyList<DisplayRect> Peers,
        PlacementSide Side, double Fraction, int ParkX, int ParkY, IReadOnlyList<DisplayRect>? Area = null,
        double SpeedX = 1, double SpeedY = 1, IReadOnlyList<DisplayRect>? Holes = null,
        IReadOnlyList<DisplayRect>? AreaDesk = null) : MouseDecision;

    /// <summary>The cursor is on another machine: swallow and send this relative move.</summary>
    public sealed record Forward(string Machine, int Dx, int Dy) : MouseDecision;

    /// <summary>The cursor came back to this PC: put it at (<paramref name="X"/>, <paramref name="Y"/>).</summary>
    public sealed record Here(int X, int Y) : MouseDecision;
}

/// <summary>
/// The coordinator's side of keyboard and mouse sharing, without any OS calls. Every machine's displays are
/// placed somewhere around this PC's (<see cref="InputLayout"/>); the cursor leaves this PC when it is pushed
/// past the edge of a display into another machine's, turns into relative moves while it is away, and comes
/// back at the point where it crosses into this PC's displays over there.
/// </summary>
public sealed class CoordinatorHandover
{
    private IReadOnlyList<DisplayRect> _displays;
    private IReadOnlyList<InputTarget> _targets;
    private InputTarget? _remote;
    private int _parkX, _parkY;

    /// <param name="displays">This PC's displays (physical pixels).</param>
    /// <param name="targets">Machines that can get the cursor, and where their displays are around this PC's.</param>
    public CoordinatorHandover(IReadOnlyList<DisplayRect> displays, IReadOnlyList<InputTarget> targets)
    {
        _displays = displays;
        _targets = targets;
    }

    /// <summary>The machine that has the cursor, or null while it is on this PC.</summary>
    public string? Remote => _remote?.Machine;

    public PlacementSide RemoteSide { get; private set; }

    public void Update(IReadOnlyList<DisplayRect> displays, IReadOnlyList<InputTarget> targets)
    {
        _displays = displays;
        _targets = targets;
        if (_remote is not null)
            // The same target (a machine can be reachable past an edge and through displays showing it); gone: back here.
            _remote = targets.FirstOrDefault(t => t.SameAs(_remote));
    }

    /// <summary>
    /// Where this PC's own windows cover the displays that show other machines (asked for when the cursor goes
    /// over, so it can come back to them); null when nothing does.
    /// </summary>
    public Func<IReadOnlyList<DisplayRect>>? WindowsOverVideo { get; set; }

    /// <summary>Those covered parts, in that machine's coordinates (through each display showing it).</summary>
    private List<DisplayRect>? HolesOf(string machine)
    {
        if (WindowsOverVideo?.Invoke() is not { Count: > 0 } covered)
            return null;
        var holes = new List<DisplayRect>();
        foreach (var t in _targets.Where(t => t.Machine == machine && t.Lent is not null))
        {
            var l = t.Lent!.Value;
            foreach (var c in covered)
            {
                int left = Math.Max(c.X, l.X), top = Math.Max(c.Y, l.Y), right = Math.Min(c.Right, l.Right), bottom = Math.Min(c.Bottom, l.Bottom);
                if (right > left && bottom > top)
                    holes.Add(t.ToClient(new DisplayRect(left, top, right - left, bottom - top)));
            }
        }
        return holes.Count > 0 ? holes : null;
    }

    /// <summary>The point is on a display of this PC that shows another machine.</summary>
    public bool IsOverLent(int x, int y) =>
        _targets.Any(t => t.Lent is { } l && x >= l.X && x < l.Right && y >= l.Y && y < l.Bottom);

    /// <param name="dragging">
    /// A mouse button is held on this PC (something is being dragged here): the cursor does not leave, so a window
    /// can be dragged across, or dropped on, a display that shows another machine.
    /// </param>
    public MouseDecision OnMouseMove(int x, int y, bool dragging = false)
    {
        if (_remote is { } remote)
        {
            int dx = x - _parkX, dy = y - _parkY;
            return dx == 0 && dy == 0 ? new MouseDecision.Pass() : new MouseDecision.Forward(remote.Machine, dx, dy);
        }
        if (_displays.Count == 0 || _targets.Count == 0 || dragging)
            return new MouseDecision.Pass();

        // A display that shows another machine's screen: that machine gets the cursor at the same spot.
        foreach (var target in _targets)
        {
            if (target.Lent is not { } lent || !(x >= lent.X && x < lent.Right && y >= lent.Y && y < lent.Bottom))
                continue;
            var shown = target.Displays[0];
            var (ex, ey) = EdgeGeometry.ClampToDisplays(target.Displays, target.ToClient(x, y).X, target.ToClient(x, y).Y);
            // Parked in the middle of that display (hidden behind the video), far from any edge.
            (_parkX, _parkY) = (lent.X + lent.Width / 2, lent.Y + lent.Height / 2);
            _remote = target;
            RemoteSide = PlacementSide.Right;
            return new MouseDecision.Enter(target.Machine, ex, ey, PeersOf(target), PlacementSide.Right, 0.5, _parkX, _parkY,
                AreaOf(target.Machine) ?? [shown], 1 / target.ScaleX, 1 / target.ScaleY, HolesOf(target.Machine),
                AreaDeskOf(target.Machine));
        }

        foreach (var (cx, cy) in Beyond(x, y))
        {
            if (InputLayout.Contains(_displays, cx, cy) || _targets.FirstOrDefault(t => t.Contains(cx, cy)) is not { } target)
                continue;

            // Where the cursor is on this PC, and the direction it is going out in.
            var (px, py) = EdgeGeometry.ClampToDisplays(_displays, x, y);
            int sx = Math.Sign(cx - px), sy = Math.Sign(cy - py);
            var side = sx > 0 ? PlacementSide.Right : sx < 0 ? PlacementSide.Left : sy < 0 ? PlacementSide.Above : PlacementSide.Below;
            // Parked just inside the edge, so the next moves outwards are still seen as moves.
            (_parkX, _parkY) = EdgeGeometry.ClampToDisplays(_displays, px - sx, py - sy);
            _remote = target;
            RemoteSide = side;

            var (tx, ty) = target.ToClient(cx, cy);
            var (ex, ey) = EdgeGeometry.ClampToDisplays(target.Displays, tx, ty);
            double fraction = EdgeGeometry.AtEdge(_displays, side, px, py) ? EdgeGeometry.Fraction(_displays, side, px, py) : 0.5;
            return new MouseDecision.Enter(target.Machine, ex, ey, PeersOf(target), side, fraction, _parkX, _parkY,
                target.Confine ? AreaOf(target.Machine) : null, 1 / target.ScaleX, 1 / target.ScaleY, HolesOf(target.Machine),
                target.Confine ? AreaDeskOf(target.Machine) : null);
        }
        return new MouseDecision.Pass();
    }

    /// <summary>
    /// The points just past the cursor that are not on this PC's displays: the cursor itself if the hook saw it
    /// off them, otherwise the next pixel out of each display edge it is touching.
    /// </summary>
    private IEnumerable<(int X, int Y)> Beyond(int x, int y)
    {
        var display = _displays.FirstOrDefault(d => x >= d.X && x < d.Right && y >= d.Y && y < d.Bottom);
        if (display == default)
        {
            yield return (x, y);
            yield break;
        }
        if (x == display.Right - 1) yield return (x + 1, y);
        if (x == display.X) yield return (x - 1, y);
        if (y == display.Bottom - 1) yield return (x, y + 1);
        if (y == display.Y) yield return (x, y - 1);
    }

    /// <summary>
    /// Where the cursor may leave to from <paramref name="target"/>, in that machine's coordinates: this PC's
    /// displays and every other display placed on the desk (the machine's own other displays included).
    /// </summary>
    private List<DisplayRect> PeersOf(InputTarget target) =>
        _displays
            .Concat(_targets.Where(t => t.Lent is null && t.Machine != target.Machine).SelectMany(t => t.Placed))
            .Select(target.ToClient)
            .ToList();

    /// <summary>
    /// Every display of that machine used from here (placed on the desk or shown on a display here): over there
    /// the cursor moves freely between them, as that machine's own desktop has them (dragging windows across too).
    /// </summary>
    private List<DisplayRect>? AreaOf(string machine)
    {
        var area = AreaPairs(machine).Select(p => p.Display).ToList();
        return area.Count > 0 ? area : null;
    }

    /// <summary>
    /// Where each display of <see cref="AreaOf"/> is on this PC's desk (same order): over there the cursor goes
    /// from one to another as they touch on the desk, not as that machine's own desktop has them (its displays
    /// can have very different pixel sizes there).
    /// </summary>
    private List<DisplayRect>? AreaDeskOf(string machine)
    {
        var desk = AreaPairs(machine).Select(p => p.Desk).ToList();
        return desk.Count > 0 ? desk : null;
    }

    private IEnumerable<(DisplayRect Display, DisplayRect Desk)> AreaPairs(string machine) =>
        _targets.Where(t => t.Machine == machine && (t.Confine || t.Lent is not null))
            .SelectMany(t => t.Displays.Select(d => (Display: d, Desk: t.Lent ?? t.ToDesk(d))))
            .DistinctBy(p => p.Display);

    /// <summary>
    /// The other machine sent the cursor back at (<paramref name="x"/>, <paramref name="y"/>) in its own
    /// coordinates (or, from older versions, at <paramref name="fraction"/> along the edge): it lands on this PC
    /// (<see cref="MouseDecision.Here"/>) or, when that point is on another display placed on the desk, goes
    /// straight on to it (<see cref="MouseDecision.Enter"/>).
    /// </summary>
    public MouseDecision Back(double fraction, int? x = null, int? y = null)
    {
        var remote = _remote;
        _remote = null;
        if (remote is null || x is not { } rx || y is not { } ry)
        {
            var (ex, ey) = EdgeGeometry.PointOnEdge(_displays, RemoteSide, fraction, inset: 2);
            return new MouseDecision.Here(ex, ey);
        }

        // Mapped back through the display of that machine it left from (each has its own place and scale here).
        var from = _targets
            .Where(t => t.Machine == remote.Machine && (t.Confine || t.Lent is not null))
            .OrderBy(t => t.Displays.Min(d => Distance(d, rx, ry)))
            .ThenBy(t => t.Lent is null) // on a display shown here (a window over the video there): through that one
            .FirstOrDefault() ?? remote;
        var (dx, dy) = from.ToDesk(rx, ry);
        if (!InputLayout.Contains(_displays, dx, dy)
            && _targets.FirstOrDefault(t => t.Lent is null && t.Machine != remote.Machine && t.Contains(dx, dy)) is { } next)
        {
            _remote = next;
            var (cx, cy) = next.ToClient(dx, dy);
            var (nx, ny) = EdgeGeometry.ClampToDisplays(next.Displays, cx, cy);
            return new MouseDecision.Enter(next.Machine, nx, ny, PeersOf(next), RemoteSide, 0.5, _parkX, _parkY,
                next.Confine ? AreaOf(next.Machine) : null, 1 / next.ScaleX, 1 / next.ScaleY, HolesOf(next.Machine),
                next.Confine ? AreaDeskOf(next.Machine) : null);
        }
        var (hx, hy) = EdgeGeometry.ClampToDisplays(_displays, dx, dy);
        return new MouseDecision.Here(hx, hy);
    }

    /// <summary>Where the cursor lands here when it comes back (tests and older callers; never goes on elsewhere).</summary>
    public (int X, int Y) Return(double fraction, int? x = null, int? y = null) =>
        Back(fraction, x, y) switch
        {
            MouseDecision.Here here => (here.X, here.Y),
            MouseDecision.Enter enter => (enter.X, enter.Y),
            _ => (0, 0),
        };

    private static long Distance(DisplayRect d, int x, int y)
    {
        long dx = x < d.X ? d.X - x : x >= d.Right ? x - d.Right + 1 : 0;
        long dy = y < d.Y ? d.Y - y : y >= d.Bottom ? y - d.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>Takes the cursor back by hand (hotkey): it reappears where it left.</summary>
    public (int X, int Y) ReturnHere()
    {
        _remote = null;
        return (_parkX, _parkY);
    }

    /// <summary>Takes the cursor back without the other machine (connection lost).</summary>
    public void ForceLocal() => _remote = null;
}

/// <summary>What the machine that gets the cursor does with a relative move.</summary>
/// <param name="X">Where the cursor goes (or, when it <paramref name="Left"/>, where it crossed, in this machine's coordinates).</param>
public readonly record struct ClientMove(int X, int Y, bool Left, double Fraction);

/// <summary>
/// The other side: where the cursor enters, where each relative move takes it (kept on the displays), and
/// when it leaves back into the coordinator's displays.
/// </summary>
public static class ClientHandover
{
    /// <summary>Older coordinators: enters through the edge facing the coordinator.</summary>
    /// <param name="side">Where this machine is relative to the coordinator; the cursor enters through the opposite edge.</param>
    public static (int X, int Y) Enter(IReadOnlyList<DisplayRect> displays, PlacementSide side, double fraction) =>
        EdgeGeometry.PointOnEdge(displays, EdgeGeometry.Opposite(side), fraction, inset: 1);

    /// <summary>
    /// A relative move when the coordinator's displays are known in this machine's coordinates
    /// (<paramref name="peers"/>): the cursor leaves when it is pushed into one of them.
    /// </summary>
    /// <param name="holes">Parts of the displays the coordinator keeps (its windows over the video): entering one leaves.</param>
    /// <param name="desk">
    /// Where each of <paramref name="displays"/> is on the coordinator's desk (same order): going from one to
    /// another follows the desk, so the cursor comes out where it went in as seen on the desk.
    /// </param>
    public static ClientMove Move(IReadOnlyList<DisplayRect> displays, IReadOnlyList<DisplayRect> peers, int fromX, int fromY, int dx, int dy,
        IReadOnlyList<DisplayRect>? holes = null, IReadOnlyList<DisplayRect>? desk = null)
    {
        int x = fromX + dx, y = fromY + dy;
        if (holes is not null && InputLayout.Contains(holes, x, y) && !InputLayout.Contains(holes, fromX, fromY))
            return new ClientMove(x, y, true, 0.5);

        if (desk is not null && desk.Count == displays.Count)
        {
            int from = IndexAt(displays, fromX, fromY);
            if (from >= 0 && !Inside(displays[from], x, y))
            {
                // Out of the display it was on: where that point is on the desk, and the display there.
                var (a, ad) = (displays[from], desk[from]);
                double qx = ad.X + (x - a.X) * (double)ad.Width / a.Width, qy = ad.Y + (y - a.Y) * (double)ad.Height / a.Height;
                int to = IndexAt(desk, (int)Math.Floor(qx), (int)Math.Floor(qy));
                if (to >= 0 && to != from)
                {
                    var (b, bd) = (displays[to], desk[to]);
                    int bx = b.X + (int)Math.Floor((qx - bd.X) * b.Width / bd.Width), by = b.Y + (int)Math.Floor((qy - bd.Y) * b.Height / bd.Height);
                    return new ClientMove(Math.Clamp(bx, b.X, b.Right - 1), Math.Clamp(by, b.Y, b.Bottom - 1), false, 0);
                }
                // Nothing of this machine there on the desk: not onto its other displays as its own desktop has them.
                displays = [a];
            }
        }

        if (InputLayout.Contains(displays, x, y))
            return new ClientMove(x, y, false, 0);
        if (InputLayout.Contains(peers, x, y))
            return new ClientMove(x, y, true, 0.5);

        // Off both: stopped at the edge here, unless one direction of the move alone reaches the coordinator
        // (going diagonally past a corner).
        var (nx, ny) = EdgeGeometry.ClampToDisplays(displays, x, y);
        if (nx != x && InputLayout.Contains(peers, x, ny))
            return new ClientMove(x, ny, true, 0.5);
        if (ny != y && InputLayout.Contains(peers, nx, y))
            return new ClientMove(nx, y, true, 0.5);
        return new ClientMove(nx, ny, false, 0);
    }

    private static bool Inside(DisplayRect d, int x, int y) => x >= d.X && x < d.Right && y >= d.Y && y < d.Bottom;

    private static int IndexAt(IReadOnlyList<DisplayRect> displays, int x, int y)
    {
        for (int i = 0; i < displays.Count; i++)
            if (Inside(displays[i], x, y))
                return i;
        return -1;
    }

    /// <summary>Older coordinators: leaves back through the edge facing the coordinator.</summary>
    public static ClientMove Move(IReadOnlyList<DisplayRect> displays, PlacementSide side, int fromX, int fromY, int dx, int dy)
    {
        int x = fromX + dx, y = fromY + dy;
        var back = EdgeGeometry.Opposite(side);
        int edge = EdgeGeometry.OuterEdge(displays, back);
        bool beyond = back switch
        {
            PlacementSide.Left => x < edge,
            PlacementSide.Right => x > edge,
            PlacementSide.Above => y < edge,
            _ => y > edge,
        };
        if (beyond)
        {
            // Measured where it crossed, kept within the edge's own span.
            var (cx, cy) = back is PlacementSide.Left or PlacementSide.Right ? (edge, y) : (x, edge);
            return new ClientMove(cx, cy, true, EdgeGeometry.Fraction(displays, back, cx, cy));
        }
        var (nx, ny) = EdgeGeometry.ClampToDisplays(displays, x, y);
        return new ClientMove(nx, ny, false, 0);
    }
}
