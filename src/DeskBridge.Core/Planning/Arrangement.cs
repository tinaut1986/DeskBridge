namespace DeskBridge.Core.Planning;

/// <summary>A display's rectangle in a machine's desktop coordinates.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Overlaps(DisplayRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    /// <summary>Shares a stretch of edge (not just a corner), as Windows needs between displays.</summary>
    public bool Touches(DisplayRect o) =>
        ((Right == o.X || o.Right == X) && Y < o.Bottom && o.Y < Bottom)
        || ((Bottom == o.Y || o.Bottom == Y) && X < o.Right && o.X < Right);
}

/// <summary>Display arrangement rules: snapping a dragged display against the others, as Windows settings does.</summary>
public static class Arrangement
{
    /// <summary>
    /// Where a display dropped at <paramref name="dropped"/> ends up: against the nearest edge of another display,
    /// aligned with its top/bottom (or left/right) when close, never overlapping any. If no spot is free, the drop
    /// position is kept (the plan will warn).
    /// </summary>
    /// <param name="threshold">Distance (desktop pixels) within which edges snap into alignment.</param>
    public static DisplayRect Snap(DisplayRect dropped, IReadOnlyList<DisplayRect> others, int threshold)
    {
        if (others.Count == 0)
            return dropped;

        DisplayRect? best = null;
        long bestDistance = long.MaxValue;
        foreach (var o in others)
        {
            int y = AlongEdge(dropped.Y, dropped.Height, o.Y, o.Bottom, threshold);
            int x = AlongEdge(dropped.X, dropped.Width, o.X, o.Right, threshold);
            Consider(dropped with { X = o.Right, Y = y });               // to its right
            Consider(dropped with { X = o.X - dropped.Width, Y = y });   // to its left
            Consider(dropped with { X = x, Y = o.Bottom });              // below
            Consider(dropped with { X = x, Y = o.Y - dropped.Height });  // above
        }
        return best ?? dropped;

        void Consider(DisplayRect candidate)
        {
            if (others.Any(candidate.Overlaps))
                return;
            long distance = Math.Abs((long)candidate.X - dropped.X) + Math.Abs((long)candidate.Y - dropped.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
    }

    /// <summary>
    /// Position along another display's edge: kept touching it, snapped to its ends or to its centre (both
    /// centres in line) when close.
    /// </summary>
    private static int AlongEdge(int position, int size, int start, int end, int threshold)
    {
        int v = Math.Clamp(position, start - size + 1, end - 1);
        int centred = (start + end) / 2 - size / 2;
        int best = v, bestDistance = threshold + 1;
        foreach (var candidate in new[] { start, end - size, centred })
        {
            int distance = Math.Abs(v - candidate);
            if (distance < bestDistance)
                (best, bestDistance) = (candidate, distance);
        }
        return best;
    }
}
