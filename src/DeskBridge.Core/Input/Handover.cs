using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Input;

/// <summary>What the coordinator does with a mouse move seen by its hook.</summary>
public abstract record MouseDecision
{
    /// <summary>Let it through: the cursor stays on this PC.</summary>
    public sealed record Pass : MouseDecision;

    /// <summary>The cursor crossed to <paramref name="Machine"/>: swallow, keep the local cursor parked there.</summary>
    public sealed record Enter(string Machine, PlacementSide Side, double Fraction, int ParkX, int ParkY) : MouseDecision;

    /// <summary>The cursor is on another machine: swallow and send this relative move.</summary>
    public sealed record Forward(string Machine, int Dx, int Dy) : MouseDecision;
}

/// <summary>
/// The coordinator's side of keyboard and mouse sharing, without any OS calls: decides when the cursor
/// leaves this PC (touching the edge on the side where another machine is), turns mouse moves into
/// relative moves while it is away, and where it comes back.
/// </summary>
public sealed class CoordinatorHandover
{
    private IReadOnlyList<DisplayRect> _displays;
    private IReadOnlyList<(string Machine, PlacementSide Side)> _targets;
    private int _parkX, _parkY;

    /// <param name="displays">This PC's displays (physical pixels).</param>
    /// <param name="targets">Machines that can get the cursor, and on which side of this PC each one is.</param>
    public CoordinatorHandover(IReadOnlyList<DisplayRect> displays, IReadOnlyList<(string Machine, PlacementSide Side)> targets)
    {
        _displays = displays;
        _targets = targets;
    }

    /// <summary>The machine that has the cursor, or null while it is on this PC.</summary>
    public string? Remote { get; private set; }

    public PlacementSide RemoteSide { get; private set; }

    public void Update(IReadOnlyList<DisplayRect> displays, IReadOnlyList<(string Machine, PlacementSide Side)> targets)
    {
        _displays = displays;
        _targets = targets;
        if (Remote is not null && targets.All(t => t.Machine != Remote))
            Remote = null; // that machine is gone: the cursor is back here
    }

    public MouseDecision OnMouseMove(int x, int y)
    {
        if (Remote is { } remote)
        {
            int dx = x - _parkX, dy = y - _parkY;
            return dx == 0 && dy == 0 ? new MouseDecision.Pass() : new MouseDecision.Forward(remote, dx, dy);
        }
        if (_displays.Count == 0)
            return new MouseDecision.Pass();

        foreach (var (machine, side) in _targets)
        {
            if (!EdgeGeometry.AtEdge(_displays, side, x, y))
                continue;
            var fraction = EdgeGeometry.Fraction(_displays, side, x, y);
            // Parked just inside the edge, so the next moves outwards are still seen as moves.
            (_parkX, _parkY) = EdgeGeometry.PointOnEdge(_displays, side, fraction, inset: 1);
            Remote = machine;
            RemoteSide = side;
            return new MouseDecision.Enter(machine, side, fraction, _parkX, _parkY);
        }
        return new MouseDecision.Pass();
    }

    /// <summary>The other machine sent the cursor back at <paramref name="fraction"/> along the edge: where to put it here.</summary>
    public (int X, int Y) Return(double fraction)
    {
        var side = RemoteSide;
        Remote = null;
        return EdgeGeometry.PointOnEdge(_displays, side, fraction, inset: 2);
    }

    /// <summary>Takes the cursor back without the other machine (hotkey, connection lost).</summary>
    public void ForceLocal() => Remote = null;
}

/// <summary>What the machine that gets the cursor does with a relative move.</summary>
public readonly record struct ClientMove(int X, int Y, bool Left, double Fraction);

/// <summary>
/// The other side: where the cursor enters (the edge facing the coordinator), where each relative move takes
/// it (kept on the displays), and when it leaves back through that edge.
/// </summary>
public static class ClientHandover
{
    /// <param name="side">Where this machine is relative to the coordinator; the cursor enters through the opposite edge.</param>
    public static (int X, int Y) Enter(IReadOnlyList<DisplayRect> displays, PlacementSide side, double fraction) =>
        EdgeGeometry.PointOnEdge(displays, EdgeGeometry.Opposite(side), fraction, inset: 1);

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
