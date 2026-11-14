using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Agent;

public sealed record MachineResult(string Machine, bool Ok, string Message, IReadOnlyList<string> Warnings);

/// <summary>What the coordinator does: talk to every agent, including its own.</summary>
public static class Coordinator
{
    public static async Task<IReadOnlyList<MachineResult>> SwitchProfileAsync(
        DeskBridgeConfig config, LocalSettings local, string profile, CancellationToken ct = default)
    {
        var plan = LayoutPlanner.Plan(config, profile); // validates before touching any machine
        if (plan.Coordinator != local.Machine)
            throw new ControlException(L.T("This machine is '{machine}', but the coordinator is '{coordinator}'. Switch profiles from there.",
                ("machine", local.Machine), ("coordinator", plan.Coordinator)));

        var key = ControlAuth.DecodeKey(local.SharedKey);
        var request = new ApplyProfileMessage(config, profile);

        return await Task.WhenAll(config.Machines.Select(async machine =>
        {
            var (host, port) = Endpoint(config, local, machine);
            try
            {
                return await ControlClient.SendAsync(host, port, local.Machine, machine.Id, key, request, ct) switch
                {
                    AppliedMessage applied => new MachineResult(machine.Id, true, L.T("profile applied"), applied.Warnings),
                    ErrorMessage error => new MachineResult(machine.Id, false, error.Message, []),
                    var other => new MachineResult(machine.Id, false, L.T("unexpected reply {reply}", ("reply", other.GetType().Name)), []),
                };
            }
            catch (ControlException ex)
            {
                return new MachineResult(machine.Id, false, ex.Message, []);
            }
        }));
    }

    /// <summary>Asks a machine's agent for its physical monitors.</summary>
    public static async Task<IReadOnlyList<DisplayConfig>> GetDisplaysAsync(
        DeskBridgeConfig config, LocalSettings local, string machineId, CancellationToken ct = default)
    {
        var machine = config.Machines.FirstOrDefault(m => m.Id == machineId)
            ?? throw new ControlException(L.T("Machine '{machine}' is not in the configuration.", ("machine", machineId)));
        var (host, port) = Endpoint(config, local, machine);
        var reply = await ControlClient.SendAsync(host, port, local.Machine, machine.Id, ControlAuth.DecodeKey(local.SharedKey), new GetDisplaysMessage(), ct);
        return reply switch
        {
            DisplaysMessage displays => displays.Displays,
            ErrorMessage error => throw new ControlException(error.Message),
            _ => throw new ControlException(L.T("Unexpected reply from '{machine}'.", ("machine", machineId))),
        };
    }

    private static (string Host, int Port) Endpoint(DeskBridgeConfig config, LocalSettings local, MachineConfig machine) =>
        Peers.Endpoint(config, local, machine);
}
