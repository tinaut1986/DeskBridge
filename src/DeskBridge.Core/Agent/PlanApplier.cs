using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Agent;

/// <summary>
/// Applies the local machine's share of a plan. Every machine runs the same agent;
/// what it does depends only on its id, not on a fixed "server" or "client" role.
/// </summary>
public sealed class PlanApplier(
    IVirtualDisplayDriver virtualDisplays,
    IStreamSender sender,
    IStreamReceiver receiver,
    IInputRouter inputRouter)
{
    /// <summary>
    /// Whether a machine is connected right now. On the coordinator, a virtual display for a machine that is not
    /// (switched off, independent, another network) is left out: nobody would show it, and the cursor and windows
    /// would get lost there. It comes when the machine connects (<see cref="RefreshVirtualDisplaysAsync"/>).
    /// Null: every planned virtual display (simulation).
    /// </summary>
    public Func<string, bool>? IsConnected { get; set; }

    /// <summary>The keys of the virtual displays in place here, in order.</summary>
    private IReadOnlyList<string> _placed = [];

    private List<VirtualDisplaySpec> Wanted(LayoutPlan plan, string localMachine) =>
        plan.VirtualDisplaysOf(localMachine)
            .Where(v => localMachine != plan.Coordinator || IsConnected is not { } connected
                        || v.ForMachine == localMachine || string.IsNullOrEmpty(v.ForMachine) || connected(v.ForMachine))
            .ToList();

    private async Task SetVirtualDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct)
    {
        _placed = [];
        await virtualDisplays.SetDisplaysAsync(displays, ct);
        _placed = displays.Select(d => d.Key).ToList();
    }

    /// <summary>
    /// The virtual displays of an applied plan again, for the machines connected now (see <see cref="IsConnected"/>);
    /// streams are left as they are. With <paramref name="removing"/> false, only when displays are added and none
    /// goes (a machine that came back); a machine gone is let go of after a while (it may be back at once).
    /// Returns whether anything changed.
    /// </summary>
    public async Task<bool> RefreshVirtualDisplaysAsync(LayoutPlan plan, string localMachine, bool removing, CancellationToken ct = default)
    {
        var wanted = Wanted(plan, localMachine);
        var keys = wanted.Select(d => d.Key).ToList();
        if (keys.SequenceEqual(_placed) || (!removing && _placed.Except(keys).Any()))
            return false;
        await SetVirtualDisplaysAsync(wanted, ct);
        return true;
    }

    public async Task ApplyAsync(DeskBridgeConfig config, LayoutPlan plan, string localMachine, CancellationToken ct = default)
    {
        var addresses = config.Machines.ToDictionary(m => m.Id, m => m.Address);
        if (!addresses.ContainsKey(localMachine))
            throw new ArgumentException(L.T("Machine '{machine}' is not in the configuration.", ("machine", localMachine)), nameof(localMachine));

        // Tear down first so a monitor never shows two things at once while switching.
        await receiver.StopAllAsync(ct);
        await sender.StopAllAsync(ct);

        await SetVirtualDisplaysAsync(Wanted(plan, localMachine), ct);

        foreach (var stream in plan.StreamsFrom(localMachine))
            await sender.StartAsync(stream, ct);

        foreach (var stream in plan.StreamsTo(localMachine))
            await receiver.StartAsync(stream, addresses[stream.SourceMachine], ct);

        if (localMachine == plan.Coordinator)
            await inputRouter.ApplyRoutesAsync(plan.InputRoutes, addresses, ct);
    }

    /// <summary>
    /// Undoes this machine's share of any plan: no streams and no virtual displays, the machine on its own
    /// (the coordinator went away, or sharing was turned off).
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await receiver.StopAllAsync(ct);
        await sender.StopAllAsync(ct);
        await SetVirtualDisplaysAsync([], ct);
    }
}
