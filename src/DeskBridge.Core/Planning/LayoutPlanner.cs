using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Planning;

public sealed class ConfigException(IReadOnlyList<string> errors)
    : Exception(L.T("Invalid configuration:") + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Turns a profile into a <see cref="LayoutPlan"/>. Pure logic: no I/O, no Windows APIs,
/// so every agent computes the same plan from the same config.
/// </summary>
public static class LayoutPlanner
{
    public const int MinWidth = 640, MaxWidth = 7680;
    public const int MinHeight = 480, MaxHeight = 4320;
    public const int MinRefreshHz = 24, MaxRefreshHz = 240;
    public const int MinBitrateMbps = 1, MaxBitrateMbps = 500;

    /// <summary>
    /// Default bitrate: about 0.16 bits per pixel per frame, which gives ~20 Mbps for 1080p60
    /// (sharp text for desktop work with HEVC/H.264).
    /// </summary>
    public static int DefaultBitrateMbps(int width, int height, int refreshHz) =>
        Math.Clamp((int)Math.Round(width * (double)height * refreshHz * 0.16 / 1_000_000), 5, 150);

    public static IReadOnlyList<string> Validate(DeskBridgeConfig config)
    {
        var errors = new List<string>();

        if (config.Machines.Count == 0)
            errors.Add(L.T("No machines are defined."));

        foreach (var dup in config.Machines.GroupBy(m => m.Id).Where(g => g.Count() > 1))
            errors.Add(L.T("Machine '{machine}' is defined more than once.", ("machine", dup.Key)));

        foreach (var machine in config.Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.Id))
                errors.Add(L.T("A machine has no id."));
            if (string.IsNullOrWhiteSpace(machine.Address))
                errors.Add(L.T("Machine '{machine}' has no address.", ("machine", machine.Id)));
            foreach (var dup in machine.Displays.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                errors.Add(L.T("Display '{display}' of '{machine}' is defined more than once.", ("display", dup.Key), ("machine", machine.Id)));
            foreach (var d in machine.Displays.Where(d => d.Width <= 0 || d.Height <= 0 || d.RefreshHz <= 0))
                errors.Add(L.T("Display '{display}' of '{machine}' has an invalid resolution or refresh rate.", ("display", d.Id), ("machine", machine.Id)));
        }

        int coordinators = config.Machines.Count(m => m.HasInput);
        if (coordinators != 1)
            errors.Add(L.T("Exactly one machine must have the keyboard and mouse (found {count}).", ("count", coordinators)));

        foreach (var dup in config.Profiles.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add(L.T("Profile '{profile}' is defined more than once.", ("profile", dup.Key)));

        var machines = config.Machines.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add(L.T("A profile has no name."));

            foreach (var a in profile.Assignments)
            {
                if (!machines.TryGetValue(a.Machine, out var owner))
                    errors.Add(L.T("Profile '{profile}': machine '{machine}' does not exist.", ("profile", profile.Name), ("machine", a.Machine)));
                else if (owner.Displays.All(d => d.Id != a.Display))
                    errors.Add(L.T("Profile '{profile}': '{machine}' has no display '{display}'.", ("profile", profile.Name), ("machine", a.Machine), ("display", a.Display)));
                if (!machines.ContainsKey(a.UsedBy))
                    errors.Add(L.T("Profile '{profile}': machine '{machine}' (used by) does not exist.", ("profile", profile.Name), ("machine", a.UsedBy)));
                if (a.Stream is { } s)
                    ValidateStream(errors, L.T("Profile '{profile}', display '{display}' of '{machine}'", ("profile", profile.Name), ("display", a.Display), ("machine", a.Machine)), s);
            }
            foreach (var dup in profile.Assignments.GroupBy(a => (a.Machine, a.Display)).Where(g => g.Count() > 1))
                errors.Add(L.T("Profile '{profile}': display '{display}' of '{machine}' is assigned more than once.", ("profile", profile.Name), ("display", dup.Key.Display), ("machine", dup.Key.Machine)));
            foreach (var (machine, side) in profile.Placement ?? new Dictionary<string, PlacementSide>())
            {
                if (!machines.ContainsKey(machine))
                    errors.Add(L.T("Profile '{profile}': machine '{machine}' does not exist.", ("profile", profile.Name), ("machine", machine)));
                if (!Enum.IsDefined(side))
                    errors.Add(L.T("Profile '{profile}': invalid placement for '{machine}'.", ("profile", profile.Name), ("machine", machine)));
            }
        }

        return errors;
    }

    private static void ValidateStream(List<string> errors, string where, StreamSettings s)
    {
        if ((s.Width is null) != (s.Height is null))
            errors.Add(L.T("{where}: set both width and height, or neither.", ("where", where)));
        if (s.Width is < MinWidth or > MaxWidth)
            errors.Add(L.T("{where}: width must be between {min} and {max}.", ("where", where), ("min", MinWidth), ("max", MaxWidth)));
        if (s.Height is < MinHeight or > MaxHeight)
            errors.Add(L.T("{where}: height must be between {min} and {max}.", ("where", where), ("min", MinHeight), ("max", MaxHeight)));
        if (s.RefreshHz is < MinRefreshHz or > MaxRefreshHz)
            errors.Add(L.T("{where}: refresh rate must be between {min} and {max} Hz.", ("where", where), ("min", MinRefreshHz), ("max", MaxRefreshHz)));
        if (s.BitrateMbps is < MinBitrateMbps or > MaxBitrateMbps)
            errors.Add(L.T("{where}: bitrate must be between {min} and {max} Mbps.", ("where", where), ("min", MinBitrateMbps), ("max", MaxBitrateMbps)));
    }

    public static LayoutPlan Plan(DeskBridgeConfig config, string profileName)
    {
        var errors = Validate(config).ToList();
        var profile = config.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile is null)
            errors.Add(L.T("Profile '{profile}' does not exist.", ("profile", profileName)));
        if (errors.Count > 0)
            throw new ConfigException(errors);

        var coordinator = config.Machines.Single(m => m.HasInput);
        var assignments = profile!.Assignments.ToDictionary(a => (a.Machine, a.Display));

        var virtualDisplays = new List<VirtualDisplaySpec>();
        var streams = new List<StreamSpec>();
        var inputRoutes = new List<InputRoute>();
        var nextVirtualIndex = new Dictionary<string, int>();
        var warnings = new List<string>();

        foreach (var machine in config.Machines)
        {
            foreach (var display in machine.Displays)
            {
                var assignment = assignments.GetValueOrDefault((machine.Id, display.Id));
                var user = assignment?.UsedBy ?? machine.Id;

                if (machine.Id == coordinator.Id)
                    inputRoutes.Add(new InputRoute(display.Id, user));

                if (user == machine.Id)
                    continue;

                // By default the borrowing machine creates a virtual monitor matching the physical
                // one that will show it, so the image is pixel-exact. Overrides trade that for
                // e.g. more desktop space (scaled down) or less bandwidth.
                var s = assignment!.Stream ?? StreamSettings.Default;
                int width = s.Width ?? display.Width;
                int height = s.Height ?? display.Height;
                int refresh = s.RefreshHz ?? display.RefreshHz;
                int bitrate = s.BitrateMbps ?? DefaultBitrateMbps(width, height, refresh);

                if (width * display.Height != height * display.Width)
                    warnings.Add(L.T("Display '{display}' of '{machine}' is {native} but receives {stream}: the aspect ratio differs, there will be black bars.",
                        ("display", display.Id), ("machine", machine.Id), ("native", $"{display.Width}x{display.Height}"), ("stream", $"{width}x{height}")));
                if (refresh > display.RefreshHz)
                    warnings.Add(L.T("Display '{display}' of '{machine}' runs at {native} Hz; streaming at {stream} Hz wastes bandwidth.",
                        ("display", display.Id), ("machine", machine.Id), ("native", display.RefreshHz), ("stream", refresh)));

                int index = nextVirtualIndex.GetValueOrDefault(user) + 1;
                nextVirtualIndex[user] = index;
                var vd = new VirtualDisplaySpec($"{user}/v{index}", user, width, height, refresh, ForMachine: machine.Id, ForDisplay: display.Id);
                virtualDisplays.Add(vd);
                streams.Add(new StreamSpec(
                    $"{vd.Key}->{machine.Id}/{display.Id}",
                    user, vd.Key, machine.Id, display.Id,
                    width, height, refresh, bitrate));
            }
        }

        foreach (var machine in config.Machines)
        {
            bool keepsOwn = machine.Displays.Any(d =>
                (assignments.GetValueOrDefault((machine.Id, d.Id))?.UsedBy ?? machine.Id) == machine.Id);
            bool borrows = virtualDisplays.Any(v => v.Machine == machine.Id);
            if (!keepsOwn && !borrows)
                warnings.Add(L.T("'{machine}' has no visible display in profile '{profile}'.", ("machine", machine.Id), ("profile", profile.Name)));
        }

        var placed = Place(config, profile, assignments, virtualDisplays);
        warnings.AddRange(ArrangementWarnings(config, placed));
        return new LayoutPlan(profile.Name, coordinator.Id, placed, streams, inputRoutes, warnings);
    }

    /// <summary>Windows needs every display to touch another one, and none may overlap.</summary>
    private static IEnumerable<string> ArrangementWarnings(DeskBridgeConfig config, List<VirtualDisplaySpec> placed)
    {
        foreach (var machine in config.Machines)
        {
            var physical = machine.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList();
            var virtuals = placed.Where(v => v.Machine == machine.Id).ToList();
            foreach (var v in virtuals)
            {
                var rect = new DisplayRect(v.X, v.Y, v.Width, v.Height);
                var others = physical.Concat(virtuals.Where(o => o.Key != v.Key).Select(o => new DisplayRect(o.X, o.Y, o.Width, o.Height))).ToList();
                if (others.Any(rect.Overlaps))
                    yield return L.T("The virtual display of '{machine}' for display '{display}' of '{lender}' overlaps another display.",
                        ("machine", machine.Id), ("display", v.ForDisplay), ("lender", v.ForMachine));
                else if (others.Count > 0 && !others.Any(rect.Touches))
                    yield return L.T("The virtual display of '{machine}' for display '{display}' of '{lender}' does not touch any other display; Windows will move it.",
                        ("machine", machine.Id), ("display", v.ForDisplay), ("lender", v.ForMachine));
            }
        }
    }

    /// <summary>
    /// Positions every virtual display in its machine's desktop: displays lent by the same machine keep
    /// the arrangement they have there (so moving the mouse from one to the next feels natural), and each
    /// such group goes next to the borrower's physical displays, on the side chosen in the profile.
    /// All physical displays count, lent ones too: they stay in Windows' desktop (they show the stream).
    /// </summary>
    private static List<VirtualDisplaySpec> Place(
        DeskBridgeConfig config,
        ProfileConfig profile,
        IReadOnlyDictionary<(string, string), DisplayAssignment> assignments,
        List<VirtualDisplaySpec> virtualDisplays)
    {
        var result = new List<VirtualDisplaySpec>();
        foreach (var borrower in config.Machines)
        {
            var mine = virtualDisplays.Where(v => v.Machine == borrower.Id).ToList();

            // Shown on the coordinator's desk next to this machine's own displays: placed in this machine's desktop
            // the same way round, so Windows moves the cursor (and dragged windows) between them as seen there.
            var fromDesk = DeskPlaced(config, borrower, mine);
            result.AddRange(fromDesk);
            mine = mine.Where(v => fromDesk.All(f => f.Key != v.Key)).ToList();

            // Placed by hand: exactly where the user dragged them.
            var manual = mine
                .Select(v => (Spec: v, Position: assignments[(v.ForMachine, v.ForDisplay)].Position))
                .Where(p => p.Position is not null)
                .Select(p => p.Spec with { X = p.Position!.X, Y = p.Position.Y })
                .ToList();
            result.AddRange(manual);

            var occupied = borrower.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height))
                .Concat(manual.Concat(fromDesk).Select(v => new DisplayRect(v.X, v.Y, v.Width, v.Height)))
                .ToList();
            bool empty = occupied.Count == 0;
            var bounds = empty
                ? (Left: 0, Top: 0, Right: 0, Bottom: 0)
                : (Left: occupied.Min(r => r.X), Top: occupied.Min(r => r.Y), Right: occupied.Max(r => r.Right), Bottom: occupied.Max(r => r.Bottom));
            var side = profile.Placement?.GetValueOrDefault(borrower.Id) ?? PlacementSide.Right;

            foreach (var lender in config.Machines)
            {
                var group = mine.Where(v => v.ForMachine == lender.Id && manual.All(m => m.Key != v.Key)).ToList();
                if (group.Count == 0)
                    continue;

                // Arrangement of the lent displays on the lender, scaled to the virtual sizes.
                var physical = group.Select(v => lender.Displays.Single(d => d.Id == v.ForDisplay)).ToList();
                int minX = physical.Min(d => d.X), minY = physical.Min(d => d.Y);
                var offsets = group.Zip(physical, (v, d) => (
                    Spec: v,
                    X: (int)Math.Round((d.X - minX) * (double)v.Width / d.Width),
                    Y: (int)Math.Round((d.Y - minY) * (double)v.Height / d.Height))).ToList();
                int width = offsets.Max(o => o.X + o.Spec.Width), height = offsets.Max(o => o.Y + o.Spec.Height);

                var (originX, originY) = empty ? (0, 0) : side switch
                {
                    PlacementSide.Left => (bounds.Left - width, bounds.Top),
                    PlacementSide.Above => (bounds.Left, bounds.Top - height),
                    PlacementSide.Below => (bounds.Left, bounds.Bottom),
                    _ => (bounds.Right, bounds.Top),
                };
                foreach (var o in offsets)
                    result.Add(o.Spec with { X = originX + o.X, Y = originY + o.Y });

                bounds = empty
                    ? (originX, originY, originX + width, originY + height)
                    : (Math.Min(bounds.Left, originX), Math.Min(bounds.Top, originY),
                       Math.Max(bounds.Right, originX + width), Math.Max(bounds.Bottom, originY + height));
                empty = false;
            }
        }
        // Keep the plan's original order (by lent display).
        return virtualDisplays.Select(v => result.Single(r => r.Key == v.Key)).ToList();
    }

    /// <summary>
    /// Virtual displays of <paramref name="borrower"/> shown on displays of the coordinator, placed in the
    /// borrower's desktop the way the coordinator's desk has them around the borrower's own displays: e.g. shown
    /// on the monitor above the laptop panel, the virtual display goes above the panel on the laptop. The group
    /// keeps its arrangement on the desk; the one closest to a display of the borrower anchors it, touching it.
    /// </summary>
    private static List<VirtualDisplaySpec> DeskPlaced(DeskBridgeConfig config, MachineConfig borrower, List<VirtualDisplaySpec> mine)
    {
        var coordinator = config.Machines.Single(m => m.HasInput);
        if (borrower.HasInput || borrower.DeskPositions is not { Count: > 0 } desk)
            return [];
        var shown = mine
            .Where(v => v.ForMachine == coordinator.Id)
            .Select(v => (Spec: v, Display: coordinator.Displays.FirstOrDefault(d => d.Id == v.ForDisplay)))
            .Where(x => x.Display is not null)
            .Select(x => (x.Spec, Here: new DisplayRect(x.Display!.X, x.Display.Y, x.Display.Width, x.Display.Height)))
            .ToList();
        var own = borrower.Displays
            .Where(d => desk.ContainsKey(d.Id))
            .Select(d =>
            {
                int? chosen = borrower.DeskWidths is { } widths && widths.TryGetValue(d.Id, out var w) && w > 0 ? w : null;
                var (dw, dh) = Input.InputLayout.DeskSize(d.Width, d.Height, d.Scale, chosen);
                var p = desk[d.Id];
                return (Own: new DisplayRect(d.X, d.Y, d.Width, d.Height), Desk: new DisplayRect(p.X, p.Y, dw, dh));
            })
            .ToList();
        if (shown.Count == 0 || own.Count == 0)
            return [];

        var (anchor, display) = shown
            .SelectMany(s => own.Select(o => (Shown: s, Own: o)))
            .OrderBy(x => Gap(x.Shown.Here, x.Own.Desk))
            .First();
        var (spec, here) = anchor;
        var (mineRect, onDesk) = display;
        double scale = (double)onDesk.Width / mineRect.Width;
        int x, y;
        if (here.Bottom <= onDesk.Y || here.Y >= onDesk.Bottom)
        {
            // Above or below it: same edge, along it in proportion, still overlapping it so they touch.
            y = here.Bottom <= onDesk.Y ? mineRect.Y - spec.Height : mineRect.Bottom;
            x = Math.Clamp(mineRect.X + (int)Math.Round((here.X - onDesk.X) / scale), mineRect.X - spec.Width + 1, mineRect.Right - 1);
        }
        else
        {
            x = here.Right <= onDesk.X ? mineRect.X - spec.Width : mineRect.Right;
            y = Math.Clamp(mineRect.Y + (int)Math.Round((here.Y - onDesk.Y) / scale), mineRect.Y - spec.Height + 1, mineRect.Bottom - 1);
        }
        int dx = x - here.X, dy = y - here.Y;
        return shown.Select(s => s.Spec with { X = s.Here.X + dx, Y = s.Here.Y + dy }).ToList();
    }

    /// <summary>Distance between two rectangles (0 when they touch or overlap).</summary>
    private static long Gap(DisplayRect a, DisplayRect b)
    {
        long gx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
        long gy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
        return gx + gy;
    }

}
