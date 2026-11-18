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

    /// <summary>
    /// The configuration without the displays marked disconnected (and what profiles do with them): they are not
    /// there to show or be shown. A machine keeps at least one display, even if all of them are marked.
    /// </summary>
    public static DeskBridgeConfig WithoutDisconnected(DeskBridgeConfig config, string? profileName = null, List<string>? warnings = null)
    {
        var gone = new HashSet<(string Machine, string Display)>();
        var machines = config.Machines.Select(m =>
        {
            if (m.Disconnected is not { Count: > 0 } off)
                return m;
            var left = m.Displays.Where(d => !off.Contains(d.Id)).ToList();
            if (left.Count == 0)
                return m; // at least one: nothing is left out
            foreach (var d in m.Displays.Where(d => off.Contains(d.Id)))
                gone.Add((m.Id, d.Id));
            return m with { Displays = left };
        }).ToList();
        if (gone.Count == 0)
            return config;

        if (warnings is not null && config.Profiles.FirstOrDefault(p => p.Name == profileName) is { } profile)
            foreach (var a in profile.Assignments.Where(a => gone.Contains((a.Machine, a.Display))))
                warnings.Add(L.T("Display '{display}' of '{machine}' is disconnected: it does not show {other} until it is back.",
                    ("display", a.Display), ("machine", a.Machine), ("other", a.UsedBy)));
        // A primary display that is gone: Windows' own choice instead ("shows:N" is display N of the coordinator).
        string coordinator = config.Machines.FirstOrDefault(m => m.HasInput)?.Id ?? "";
        bool Gone(KeyValuePair<string, string> primary) =>
            primary.Value.StartsWith(ProfileConfig.ShowsPrefix, StringComparison.Ordinal)
                ? gone.Contains((coordinator, primary.Value[ProfileConfig.ShowsPrefix.Length..]))
                : gone.Contains((primary.Key, primary.Value));
        var profiles = config.Profiles.Select(p => p with
        {
            Assignments = p.Assignments.Where(a => !gone.Contains((a.Machine, a.Display))).ToList(),
            PrimaryDisplays = p.PrimaryDisplays?.Where(kv => !Gone(kv)).ToDictionary(kv => kv.Key, kv => kv.Value),
        }).ToList();
        return config with { Machines = machines, Profiles = profiles };
    }

    public static LayoutPlan Plan(DeskBridgeConfig config, string profileName)
    {
        var errors = Validate(config).ToList();
        var profile = config.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile is null)
            errors.Add(L.T("Profile '{profile}' does not exist.", ("profile", profileName)));
        if (errors.Count > 0)
            throw new ConfigException(errors);

        var warnings = new List<string>();
        config = WithoutDisconnected(config, profile!.Name, warnings);
        profile = config.Profiles.First(p => p.Name == profileName);
        var coordinator = config.Machines.Single(m => m.HasInput);
        var assignments = profile.Assignments.ToDictionary(a => (a.Machine, a.Display));

        var virtualDisplays = new List<VirtualDisplaySpec>();
        var streams = new List<StreamSpec>();
        var inputRoutes = new List<InputRoute>();
        var nextVirtualIndex = new Dictionary<string, int>();

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
                // The coordinator's screen shown on a display that is on its desk (the laptop under the monitors):
                // the size it has on the desk, so its edges meet the coordinator's displays exactly as the Layout map
                // shows (Windows joins displays by pixels, whatever their scale). The display scales it to fit.
                if (s.Width is null && s.Height is null && config.Machines.FirstOrDefault(m => m.Id == user)?.HasInput == true
                    && machine.DeskPositions?.ContainsKey(display.Id) == true)
                {
                    int? chosen = machine.DeskWidths is { } widths && widths.TryGetValue(display.Id, out var w) && w > 0 ? w : null;
                    (width, height) = Input.InputLayout.DeskSize(display.Width, display.Height, display.Scale, chosen);
                    (width, height) = (width & ~1, height & ~1); // the video encoder works in whole pairs of pixels
                }
                int refresh = s.RefreshHz ?? display.RefreshHz;
                int bitrate = s.BitrateMbps ?? DefaultBitrateMbps(width, height, refresh);

                // (Within a pixel: sizes are rounded to whole pairs of pixels for the encoder.)
                if (Math.Abs((double)width / height - (double)display.Width / display.Height) > 2.0 / height)
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
            // Its displays all show the coordinator (a tablet as one more monitor): it is in use, as intended.
            bool showsCoordinator = machine.Id != coordinator.Id && machine.Displays.Count > 0 && machine.Displays.All(d =>
                assignments.GetValueOrDefault((machine.Id, d.Id))?.UsedBy == coordinator.Id);
            if (!keepsOwn && !borrows && !showsCoordinator)
                warnings.Add(L.T("'{machine}' has no visible display in profile '{profile}'.", ("machine", machine.Id), ("profile", profile.Name)));
        }

        var placed = Place(config, profile, assignments, virtualDisplays);
        warnings.AddRange(ArrangementWarnings(config, placed));
        return new LayoutPlan(profile.Name, coordinator.Id, placed, streams, inputRoutes, warnings, PrimaryDisplays(profile, coordinator.Id, placed));
    }

    /// <summary>
    /// The profile's primary displays, with "shows:N" turned into the key of the virtual display shown on the
    /// coordinator's display N (dropped when the profile has none there any more).
    /// </summary>
    private static Dictionary<string, string>? PrimaryDisplays(ProfileConfig profile, string coordinator, List<VirtualDisplaySpec> placed)
    {
        if (profile.PrimaryDisplays is not { Count: > 0 } chosen)
            return null;
        var result = new Dictionary<string, string>();
        foreach (var (machine, display) in chosen)
        {
            if (!display.StartsWith(ProfileConfig.ShowsPrefix, StringComparison.Ordinal))
            {
                result[machine] = display;
                continue;
            }
            string shownOn = display[ProfileConfig.ShowsPrefix.Length..];
            if (placed.FirstOrDefault(v => v.Machine == machine && v.ForMachine == coordinator && v.ForDisplay == shownOn) is { } virtualDisplay)
                result[machine] = virtualDisplay.Key;
        }
        return result.Count > 0 ? result : null;
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
            var fromDesk = DeskPlaced(config, borrower, mine, assignments);
            // The coordinator's own virtual displays shown on a display of another machine that is on its desk (e.g.
            // the laptop below the monitors, used as one more monitor): exactly where that display is on the desk,
            // unless the profile places them by hand.
            if (borrower.HasInput)
            {
                fromDesk.AddRange(mine
                    .Where(v => assignments[(v.ForMachine, v.ForDisplay)].Position is null)
                    .Select(v => (Spec: v, Lender: config.Machines.FirstOrDefault(m => m.Id == v.ForMachine)))
                    .Where(x => x.Lender?.DeskPositions?.ContainsKey(x.Spec.ForDisplay) == true)
                    .Select(x => x.Spec with { X = x.Lender!.DeskPositions![x.Spec.ForDisplay].X, Y = x.Lender.DeskPositions[x.Spec.ForDisplay].Y }));
            }
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

            MatchTheDesk(config, borrower, assignments, result);
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
    private static List<VirtualDisplaySpec> DeskPlaced(DeskBridgeConfig config, MachineConfig borrower, List<VirtualDisplaySpec> mine,
        IReadOnlyDictionary<(string, string), DisplayAssignment> assignments)
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
        // Its displays that show itself, and those that show the coordinator: the laptop panel showing the desktop is
        // still part of the laptop's own Windows desktop, where its windows go past an edge onto it, and its place on
        // the desk is where the panel is. One lent to a third machine is not placed on the desk for the borrower.
        var own = borrower.Displays
            .Where(d => desk.ContainsKey(d.Id)
                        && (ShowsItself(borrower, d.Id, assignments) || assignments.GetValueOrDefault((borrower.Id, d.Id))?.UsedBy == coordinator.Id))
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

    private static bool ShowsItself(MachineConfig machine, string display, IReadOnlyDictionary<(string, string), DisplayAssignment> assignments) =>
        assignments.GetValueOrDefault((machine.Id, display))?.UsedBy is not { } user || user == machine.Id;

    /// <summary>
    /// A virtual display of <paramref name="borrower"/> shown on a display of a third machine that is on the
    /// coordinator's desk (the laptop's screen on a tablet, relayed): placed in the borrower's desktop next to one of
    /// its displays already on the desk, the way the desk has them (below it when the tablet is below), so a window
    /// dragged there goes where the desk shows it. Left where it was if that would overlap another display.
    /// </summary>
    private static void MatchTheDesk(DeskBridgeConfig config, MachineConfig borrower,
        IReadOnlyDictionary<(string, string), DisplayAssignment> assignments, List<VirtualDisplaySpec> result)
    {
        var coordinator = config.Machines.Single(m => m.HasInput);
        if (borrower.HasInput)
            return;
        static DisplayRect Rect(VirtualDisplaySpec v) => new(v.X, v.Y, v.Width, v.Height);
        DisplayRect? DeskOf(MachineConfig machine, string display)
        {
            if (machine.DeskPositions?.TryGetValue(display, out var p) != true || machine.Displays.FirstOrDefault(d => d.Id == display) is not { } d)
                return null;
            int? chosen = machine.DeskWidths is { } widths && widths.TryGetValue(display, out var w) && w > 0 ? w : null;
            var (dw, dh) = Input.InputLayout.DeskSize(d.Width, d.Height, d.Scale, chosen);
            return new DisplayRect(p!.X, p.Y, dw, dh);
        }

        // Where the borrower already is on the desk: its virtual displays shown on the coordinator's monitors, and its
        // own displays placed there that show itself.
        var anchors = new List<(DisplayRect Here, DisplayRect Desk)>();
        foreach (var v in result.Where(v => v.Machine == borrower.Id && v.ForMachine == coordinator.Id))
            if (coordinator.Displays.FirstOrDefault(d => d.Id == v.ForDisplay) is { } cd)
                anchors.Add((Rect(v), new DisplayRect(cd.X, cd.Y, cd.Width, cd.Height)));
        foreach (var d in borrower.Displays.Where(d => ShowsItself(borrower, d.Id, assignments)))
            if (DeskOf(borrower, d.Id) is { } onDesk)
                anchors.Add((new DisplayRect(d.X, d.Y, d.Width, d.Height), onDesk));

        foreach (var v in result.Where(v => v.Machine == borrower.Id && v.ForMachine != coordinator.Id && v.ForMachine != borrower.Id).ToList())
        {
            if (assignments[(v.ForMachine, v.ForDisplay)].Position is not null || anchors.Count == 0
                || config.Machines.FirstOrDefault(m => m.Id == v.ForMachine) is not { } lender || DeskOf(lender, v.ForDisplay) is not { } target)
                continue;
            var (here, onDesk) = anchors.OrderBy(a => Gap(target, a.Desk)).First();
            double scale = (double)onDesk.Width / here.Width;
            int x, y;
            if (target.Bottom <= onDesk.Y || target.Y >= onDesk.Bottom)
            {
                y = target.Bottom <= onDesk.Y ? here.Y - v.Height : here.Bottom;
                x = Math.Clamp(here.X + (int)Math.Round((target.X - onDesk.X) / scale), here.X - v.Width + 1, here.Right - 1);
            }
            else
            {
                x = target.Right <= onDesk.X ? here.X - v.Width : here.Right;
                y = Math.Clamp(here.Y + (int)Math.Round((target.Y - onDesk.Y) / scale), here.Y - v.Height + 1, here.Bottom - 1);
            }
            var placed = new DisplayRect(x, y, v.Width, v.Height);
            var others = borrower.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height))
                .Concat(result.Where(o => o.Machine == borrower.Id && o.Key != v.Key).Select(Rect));
            if (others.Any(placed.Overlaps))
                continue;
            result[result.FindIndex(r => r.Key == v.Key)] = v with { X = x, Y = y };
            anchors.Add((placed, target));
        }
    }

    /// <summary>Distance between two rectangles (0 when they touch or overlap).</summary>
    private static long Gap(DisplayRect a, DisplayRect b)
    {
        long gx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
        long gy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
        return gx + gy;
    }

}
