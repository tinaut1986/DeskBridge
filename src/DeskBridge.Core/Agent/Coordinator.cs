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
    /// <param name="directory">Sessions already open (machines that connected to us); others are dialled.</param>
    public static async Task<IReadOnlyList<MachineResult>> SwitchProfileAsync(
        DeskBridgeConfig config, LocalSettings local, string profile, PeerDirectory? directory = null, CancellationToken ct = default)
    {
        var plan = LayoutPlanner.Plan(config, profile); // validates before touching any machine
        if (plan.Coordinator != local.Machine)
            throw new ControlException(L.T("This machine is '{machine}', but the coordinator is '{coordinator}'. Switch profiles from there.",
                ("machine", local.Machine), ("coordinator", plan.Coordinator)));

        var request = new ApplyProfileMessage(config, profile);
        return await Task.WhenAll(config.Machines.Select(async machine =>
        {
            try
            {
                await using var link = await Peers.ConnectAsync(config, local, machine.Id, directory, ct);
                return await link.RequestAsync(request, ct) switch
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

    /// <summary>Sends the current configuration to every machine connected right now (best effort).</summary>
    public static async Task SyncConfigAsync(DeskBridgeConfig config, PeerDirectory directory, CancellationToken ct = default)
    {
        await Task.WhenAll(directory.Connected.Select(async machine =>
        {
            if (directory.Find(machine) is not { } session)
                return;
            try
            {
                await session.RequestAsync(new ConfigSyncMessage(config), ct);
            }
            catch (ControlException)
            {
                // it will get it again when it reconnects
            }
        }));
    }

    /// <summary>Asks a machine's agent for its physical monitors.</summary>
    public static async Task<IReadOnlyList<DisplayConfig>> GetDisplaysAsync(
        DeskBridgeConfig config, LocalSettings local, string machineId, PeerDirectory? directory = null, CancellationToken ct = default)
    {
        await using var link = await Peers.ConnectAsync(config, local, machineId, directory, ct);
        return await link.RequestAsync(new GetDisplaysMessage(), ct) switch
        {
            DisplaysMessage displays => displays.Displays,
            ErrorMessage error => throw new ControlException(error.Message),
            _ => throw new ControlException(L.T("Unexpected reply from '{machine}'.", ("machine", machineId))),
        };
    }
}
