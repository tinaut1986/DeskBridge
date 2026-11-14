using DeskBridge.Core.Model;

namespace DeskBridge.Core.Planning;

public sealed class ConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
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
            errors.Add("No machines are defined.");

        foreach (var dup in config.Machines.GroupBy(m => m.Id).Where(g => g.Count() > 1))
            errors.Add($"Machine '{dup.Key}' is defined more than once.");

        foreach (var machine in config.Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.Id))
                errors.Add("A machine has no id.");
            if (string.IsNullOrWhiteSpace(machine.Address))
                errors.Add($"Machine '{machine.Id}' has no address.");
            foreach (var dup in machine.Displays.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                errors.Add($"Display '{dup.Key}' of '{machine.Id}' is defined more than once.");
            foreach (var d in machine.Displays.Where(d => d.Width <= 0 || d.Height <= 0 || d.RefreshHz <= 0))
                errors.Add($"Display '{d.Id}' of '{machine.Id}' has an invalid resolution or refresh rate.");
        }

        int coordinators = config.Machines.Count(m => m.HasInput);
        if (coordinators != 1)
            errors.Add($"Exactly one machine must have \"hasInput\": true (found {coordinators}).");

        foreach (var dup in config.Profiles.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add($"Profile '{dup.Key}' is defined more than once.");

        var machines = config.Machines.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                errors.Add("A profile has no name.");

            foreach (var a in profile.Assignments)
            {
                if (!machines.TryGetValue(a.Machine, out var owner))
                    errors.Add($"Profile '{profile.Name}': machine '{a.Machine}' does not exist.");
                else if (owner.Displays.All(d => d.Id != a.Display))
                    errors.Add($"Profile '{profile.Name}': '{a.Machine}' has no display '{a.Display}'.");
                if (!machines.ContainsKey(a.UsedBy))
                    errors.Add($"Profile '{profile.Name}': machine '{a.UsedBy}' (usedBy) does not exist.");
                if (a.Stream is { } s)
                    ValidateStream(errors, $"Profile '{profile.Name}', display '{a.Display}' of '{a.Machine}'", s);
            }
            foreach (var dup in profile.Assignments.GroupBy(a => (a.Machine, a.Display)).Where(g => g.Count() > 1))
                errors.Add($"Profile '{profile.Name}': display '{dup.Key.Display}' of '{dup.Key.Machine}' is assigned more than once.");
        }

        return errors;
    }

    private static void ValidateStream(List<string> errors, string where, StreamSettings s)
    {
        if ((s.Width is null) != (s.Height is null))
            errors.Add($"{where}: set both width and height, or neither.");
        if (s.Width is < MinWidth or > MaxWidth)
            errors.Add($"{where}: width must be between {MinWidth} and {MaxWidth}.");
        if (s.Height is < MinHeight or > MaxHeight)
            errors.Add($"{where}: height must be between {MinHeight} and {MaxHeight}.");
        if (s.RefreshHz is < MinRefreshHz or > MaxRefreshHz)
            errors.Add($"{where}: refresh rate must be between {MinRefreshHz} and {MaxRefreshHz} Hz.");
        if (s.BitrateMbps is < MinBitrateMbps or > MaxBitrateMbps)
            errors.Add($"{where}: bitrate must be between {MinBitrateMbps} and {MaxBitrateMbps} Mbps.");
    }

    public static LayoutPlan Plan(DeskBridgeConfig config, string profileName)
    {
        var errors = Validate(config).ToList();
        var profile = config.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile is null)
            errors.Add($"Profile '{profileName}' does not exist.");
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
                    warnings.Add($"Display '{display.Id}' of '{machine.Id}' is {display.Width}x{display.Height} but receives {width}x{height}: the aspect ratio differs, there will be black bars.");
                if (refresh > display.RefreshHz)
                    warnings.Add($"Display '{display.Id}' of '{machine.Id}' runs at {display.RefreshHz} Hz; streaming at {refresh} Hz wastes bandwidth.");

                int index = nextVirtualIndex.GetValueOrDefault(user) + 1;
                nextVirtualIndex[user] = index;
                var vd = new VirtualDisplaySpec($"{user}/v{index}", user, width, height, refresh);
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
                warnings.Add($"'{machine.Id}' has no visible display in profile '{profile.Name}'.");
        }

        return new LayoutPlan(profile.Name, coordinator.Id, virtualDisplays, streams, inputRoutes, warnings);
    }
}
