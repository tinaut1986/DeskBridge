namespace DeskBridge.Core.Planning;

/// <summary>
/// Which of the coordinator's displays should be Windows' primary one (Start menu, taskbar, the Windows key)
/// while a profile is applied: a display that shows another machine's screen is covered by its video, so the
/// primary display moves elsewhere while it does, and comes back afterwards.
/// </summary>
public static class PrimaryChoice
{
    /// <param name="displays">The coordinator's displays by id (any coordinates, as long as they are all in the same ones).</param>
    /// <param name="original">The primary display the user had before DeskBridge moved it.</param>
    /// <param name="lent">Displays of the coordinator showing another machine in this profile.</param>
    /// <param name="chosen">The display chosen in the profile, if any.</param>
    public static string Decide(IReadOnlyDictionary<string, DisplayRect> displays, string original, IReadOnlySet<string> lent, string? chosen)
    {
        if (chosen is not null && displays.ContainsKey(chosen) && !lent.Contains(chosen))
            return chosen;
        if (!lent.Contains(original) || !displays.TryGetValue(original, out var from))
            return original;
        // The nearest display that is not covered (centre to centre).
        var free = displays.Where(d => !lent.Contains(d.Key)).ToList();
        if (free.Count == 0)
            return original;
        double cx = from.X + from.Width / 2.0, cy = from.Y + from.Height / 2.0;
        return free.OrderBy(d => Math.Pow(d.Value.X + d.Value.Width / 2.0 - cx, 2) + Math.Pow(d.Value.Y + d.Value.Height / 2.0 - cy, 2))
            .ThenBy(d => d.Value.X).ThenBy(d => d.Value.Y) // a tie: the one on the left (then above)
            .First().Key;
    }
}
