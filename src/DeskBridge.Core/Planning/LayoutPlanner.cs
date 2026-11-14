using DeskBridge.Core.Model;

namespace DeskBridge.Core.Planning;

public sealed class ConfigException(IReadOnlyList<string> errors)
    : Exception("Configuración no válida:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Turns a profile into a <see cref="LayoutPlan"/>. Pure logic: no I/O, no Windows APIs,
/// so every agent computes the same plan from the same config.
/// </summary>
public static class LayoutPlanner
{
    public static IReadOnlyList<string> Validate(DeskBridgeConfig config)
    {
        var errors = new List<string>();

        if (config.Machines.Count == 0)
            errors.Add("No hay equipos definidos.");

        foreach (var dup in config.Machines.GroupBy(m => m.Id).Where(g => g.Count() > 1))
            errors.Add($"El equipo '{dup.Key}' está repetido.");

        foreach (var machine in config.Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.Id))
                errors.Add("Hay un equipo sin id.");
            foreach (var dup in machine.Displays.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                errors.Add($"La pantalla '{dup.Key}' de '{machine.Id}' está repetida.");
            foreach (var d in machine.Displays.Where(d => d.Width <= 0 || d.Height <= 0 || d.RefreshHz <= 0))
                errors.Add($"La pantalla '{d.Id}' de '{machine.Id}' tiene una resolución o frecuencia no válida.");
        }

        int coordinators = config.Machines.Count(m => m.HasInput);
        if (coordinators != 1)
            errors.Add($"Debe haber exactamente un equipo con \"hasInput\": true (hay {coordinators}).");

        foreach (var dup in config.Profiles.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add($"El perfil '{dup.Key}' está repetido.");

        var machines = config.Machines.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var profile in config.Profiles)
        {
            foreach (var a in profile.Assignments)
            {
                if (!machines.TryGetValue(a.Machine, out var owner))
                    errors.Add($"Perfil '{profile.Name}': el equipo '{a.Machine}' no existe.");
                else if (owner.Displays.All(d => d.Id != a.Display))
                    errors.Add($"Perfil '{profile.Name}': '{a.Machine}' no tiene pantalla '{a.Display}'.");
                if (!machines.ContainsKey(a.UsedBy))
                    errors.Add($"Perfil '{profile.Name}': el equipo '{a.UsedBy}' (usedBy) no existe.");
            }
            foreach (var dup in profile.Assignments.GroupBy(a => (a.Machine, a.Display)).Where(g => g.Count() > 1))
                errors.Add($"Perfil '{profile.Name}': la pantalla '{dup.Key.Display}' de '{dup.Key.Machine}' está asignada varias veces.");
        }

        return errors;
    }

    public static LayoutPlan Plan(DeskBridgeConfig config, string profileName)
    {
        var errors = Validate(config).ToList();
        var profile = config.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile is null)
            errors.Add($"El perfil '{profileName}' no existe.");
        if (errors.Count > 0)
            throw new ConfigException(errors);

        var coordinator = config.Machines.Single(m => m.HasInput);
        var usedBy = profile!.Assignments.ToDictionary(a => (a.Machine, a.Display), a => a.UsedBy);

        var virtualDisplays = new List<VirtualDisplaySpec>();
        var streams = new List<StreamSpec>();
        var inputRoutes = new List<InputRoute>();
        var nextVirtualIndex = new Dictionary<string, int>();

        foreach (var machine in config.Machines)
        {
            foreach (var display in machine.Displays)
            {
                var user = usedBy.GetValueOrDefault((machine.Id, display.Id), machine.Id);

                if (machine.Id == coordinator.Id)
                    inputRoutes.Add(new InputRoute(display.Id, user));

                if (user == machine.Id)
                    continue;

                // The borrowing machine creates a virtual monitor matching the physical one
                // that will show it, so the image is pixel-exact.
                int index = nextVirtualIndex.GetValueOrDefault(user) + 1;
                nextVirtualIndex[user] = index;
                var vd = new VirtualDisplaySpec($"{user}/v{index}", user, display.Width, display.Height, display.RefreshHz);
                virtualDisplays.Add(vd);
                streams.Add(new StreamSpec(
                    $"{vd.Key}->{machine.Id}/{display.Id}",
                    user, vd.Key, machine.Id, display.Id,
                    display.Width, display.Height, display.RefreshHz));
            }
        }

        var warnings = new List<string>();
        foreach (var machine in config.Machines)
        {
            bool keepsOwn = machine.Displays.Any(d => usedBy.GetValueOrDefault((machine.Id, d.Id), machine.Id) == machine.Id);
            bool borrows = virtualDisplays.Any(v => v.Machine == machine.Id);
            if (!keepsOwn && !borrows)
                warnings.Add($"'{machine.Id}' se queda sin ninguna pantalla visible en el perfil '{profile.Name}'.");
        }

        return new LayoutPlan(profile.Name, coordinator.Id, virtualDisplays, streams, inputRoutes, warnings);
    }
}
