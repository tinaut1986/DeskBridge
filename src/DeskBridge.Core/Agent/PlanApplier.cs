using DeskBridge.Core.Abstractions;
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
    public async Task ApplyAsync(DeskBridgeConfig config, LayoutPlan plan, string localMachine, CancellationToken ct = default)
    {
        var addresses = config.Machines.ToDictionary(m => m.Id, m => m.Address);
        if (!addresses.ContainsKey(localMachine))
            throw new ArgumentException($"Machine '{localMachine}' is not in the configuration.", nameof(localMachine));

        // Tear down first so a monitor never shows two things at once while switching.
        await receiver.StopAllAsync(ct);
        await sender.StopAllAsync(ct);

        await virtualDisplays.SetDisplaysAsync(plan.VirtualDisplaysOf(localMachine).ToList(), ct);

        foreach (var stream in plan.StreamsFrom(localMachine))
            await sender.StartAsync(stream, ct);

        foreach (var stream in plan.StreamsTo(localMachine))
            await receiver.StartAsync(stream, addresses[stream.SourceMachine], ct);

        if (localMachine == plan.Coordinator)
            await inputRouter.ApplyRoutesAsync(plan.InputRoutes, addresses, ct);
    }
}
