using DeskBridge.Core.Model;

namespace DeskBridge.Core.Planning;

/// <summary>
/// Where a machine's desktop ends on one side, for moving the cursor between machines: the outer edge on
/// that side, the stretch of it covered by displays, the position along it as a fraction, and a point just
/// inside it for a given fraction. Pure geometry on display rectangles (physical pixels).
/// </summary>
public static class EdgeGeometry
{
    public static PlacementSide Opposite(PlacementSide side) => side switch
    {
        PlacementSide.Left => PlacementSide.Right,
        PlacementSide.Right => PlacementSide.Left,
        PlacementSide.Above => PlacementSide.Below,
        _ => PlacementSide.Above,
    };

    private static bool Horizontal(PlacementSide side) => side is PlacementSide.Left or PlacementSide.Right;

    /// <summary>The outer coordinate on that side: the last column/row of pixels the cursor can reach.</summary>
    public static int OuterEdge(IReadOnlyList<DisplayRect> displays, PlacementSide side) => side switch
    {
        PlacementSide.Right => displays.Max(d => d.Right) - 1,
        PlacementSide.Left => displays.Min(d => d.X),
        PlacementSide.Below => displays.Max(d => d.Bottom) - 1,
        _ => displays.Min(d => d.Y),
    };

    /// <summary>The displays that reach the outer edge on that side.</summary>
    private static List<DisplayRect> EdgeDisplays(IReadOnlyList<DisplayRect> displays, PlacementSide side)
    {
        int edge = OuterEdge(displays, side);
        return displays.Where(d => side switch
        {
            PlacementSide.Right => d.Right - 1 == edge,
            PlacementSide.Left => d.X == edge,
            PlacementSide.Below => d.Bottom - 1 == edge,
            _ => d.Y == edge,
        }).ToList();
    }

    /// <summary>The stretch of the outer edge covered by displays, along it (y for left/right, x for above/below).</summary>
    public static (int Start, int End) Span(IReadOnlyList<DisplayRect> displays, PlacementSide side)
    {
        var edge = EdgeDisplays(displays, side);
        return Horizontal(side) ? (edge.Min(d => d.Y), edge.Max(d => d.Bottom)) : (edge.Min(d => d.X), edge.Max(d => d.Right));
    }

    /// <summary>The cursor is on the outer edge on that side, over a display that reaches it.</summary>
    public static bool AtEdge(IReadOnlyList<DisplayRect> displays, PlacementSide side, int x, int y)
    {
        if (displays.Count == 0)
            return false;
        int edge = OuterEdge(displays, side);
        bool onEdge = side switch
        {
            PlacementSide.Right => x >= edge,
            PlacementSide.Left => x <= edge,
            PlacementSide.Below => y >= edge,
            _ => y <= edge,
        };
        if (!onEdge)
            return false;
        return EdgeDisplays(displays, side).Any(d => Horizontal(side) ? y >= d.Y && y < d.Bottom : x >= d.X && x < d.Right);
    }

    /// <summary>Position along the edge on that side, 0 (top/left of the covered stretch) to 1.</summary>
    public static double Fraction(IReadOnlyList<DisplayRect> displays, PlacementSide side, int x, int y)
    {
        var (start, end) = Span(displays, side);
        double along = Horizontal(side) ? y : x;
        return end - start <= 1 ? 0.5 : Math.Clamp((along - start) / (end - start - 1), 0, 1);
    }

    /// <summary>
    /// A point <paramref name="inset"/> pixels inside the edge on that side, at that fraction along it,
    /// moved onto the nearest display if the edge has gaps.
    /// </summary>
    public static (int X, int Y) PointOnEdge(IReadOnlyList<DisplayRect> displays, PlacementSide side, double fraction, int inset = 1)
    {
        var (start, end) = Span(displays, side);
        int along = (int)Math.Round(start + Math.Clamp(fraction, 0, 1) * (end - start - 1));
        int edge = OuterEdge(displays, side);
        int across = side switch
        {
            PlacementSide.Right or PlacementSide.Below => edge - inset,
            _ => edge + inset,
        };
        var (x, y) = Horizontal(side) ? (across, along) : (along, across);

        // On a display? Otherwise the closest point of the closest display that reaches the edge.
        var edgeDisplays = EdgeDisplays(displays, side);
        if (edgeDisplays.Any(d => x >= d.X && x < d.Right && y >= d.Y && y < d.Bottom))
            return (x, y);
        var best = edgeDisplays.OrderBy(d => Distance(d, x, y)).First();
        return (Math.Clamp(x, best.X, best.Right - 1), Math.Clamp(y, best.Y, best.Bottom - 1));
    }

    /// <summary>The point itself if it is on a display, otherwise the closest point of the closest display.</summary>
    public static (int X, int Y) ClampToDisplays(IReadOnlyList<DisplayRect> displays, int x, int y)
    {
        if (displays.Count == 0 || displays.Any(d => x >= d.X && x < d.Right && y >= d.Y && y < d.Bottom))
            return (x, y);
        var best = displays.OrderBy(d => Distance(d, x, y)).First();
        return (Math.Clamp(x, best.X, best.Right - 1), Math.Clamp(y, best.Y, best.Bottom - 1));
    }

    private static long Distance(DisplayRect d, int x, int y)
    {
        long dx = x < d.X ? d.X - x : x >= d.Right ? x - d.Right + 1 : 0;
        long dy = y < d.Y ? d.Y - y : y >= d.Bottom ? y - d.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }
}
