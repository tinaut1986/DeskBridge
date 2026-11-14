using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
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
            throw new ControlException($"This machine is '{local.Machine}', but the coordinator is '{plan.Coordinator}'. Switch profiles from there.");

        var key = ControlAuth.DecodeKey(local.SharedKey);
        var request = new ApplyProfileMessage(config, profile);

        return await Task.WhenAll(config.Machines.Select(async machine =>
        {
            var (host, port) = Endpoint(config, local, machine);
            try
            {
                return await ControlClient.SendAsync(host, port, local.Machine, machine.Id, key, request, ct) switch
                {
                    AppliedMessage applied => new MachineResult(machine.Id, true, "profile applied", applied.Warnings),
                    ErrorMessage error => new MachineResult(machine.Id, false, error.Message, []),
                    var other => new MachineResult(machine.Id, false, $"unexpected reply {other.GetType().Name}", []),
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
            ?? throw new ControlException($"Machine '{machineId}' is not in the configuration.");
        var (host, port) = Endpoint(config, local, machine);
        var reply = await ControlClient.SendAsync(host, port, local.Machine, machine.Id, ControlAuth.DecodeKey(local.SharedKey), new GetDisplaysMessage(), ct);
        return reply switch
        {
            DisplaysMessage displays => displays.Displays,
            ErrorMessage error => throw new ControlException(error.Message),
            _ => throw new ControlException($"Unexpected reply from '{machineId}'."),
        };
    }

    private static (string Host, int Port) Endpoint(DeskBridgeConfig config, LocalSettings local, MachineConfig machine) =>
        (machine.Id == local.Machine ? "127.0.0.1" : machine.Address, machine.ControlPort ?? config.ControlPort);
}
