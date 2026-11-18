using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Simulation;

namespace DeskBridge.Core.Tests;

public class PlanApplierTests
{
    private sealed class RecordingDriver : IVirtualDisplayDriver
    {
        public List<List<string>> Calls { get; } = [];

        public Task SetDisplaysAsync(IReadOnlyList<VirtualDisplaySpec> displays, CancellationToken ct = default)
        {
            Calls.Add(displays.Select(d => d.Key).ToList());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Coordinator_leaves_out_virtual_displays_for_machines_not_connected_until_they_connect()
    {
        var config = LayoutPlannerTests.Example();
        var plan = LayoutPlanner.Plan(config, "four-screens");
        Assert.Equal("desktop", plan.Coordinator);
        var driver = new RecordingDriver();
        var applier = Simulated.Applier(_ => { }, driver);
        bool laptopHere = false;
        applier.IsConnected = m => m == "desktop" || (m == "laptop" && laptopHere);

        await applier.ApplyAsync(config, plan, "desktop");
        Assert.Empty(driver.Calls[^1]);

        // Nothing to take off, nothing to add: no call.
        Assert.False(await applier.RefreshVirtualDisplaysAsync(plan, "desktop", removing: true));

        laptopHere = true;
        Assert.True(await applier.RefreshVirtualDisplaysAsync(plan, "desktop", removing: false));
        Assert.Single(driver.Calls[^1]);

        // Gone again: not at once, only when removals are allowed (after the wait).
        laptopHere = false;
        Assert.False(await applier.RefreshVirtualDisplaysAsync(plan, "desktop", removing: false));
        Assert.True(await applier.RefreshVirtualDisplaysAsync(plan, "desktop", removing: true));
        Assert.Empty(driver.Calls[^1]);
    }

    [Fact]
    public async Task A_client_places_its_virtual_displays_whoever_is_connected()
    {
        var config = LayoutPlannerTests.Example();
        var plan = LayoutPlanner.Plan(config, "work");
        var driver = new RecordingDriver();
        var applier = Simulated.Applier(_ => { }, driver);
        applier.IsConnected = _ => false;

        await applier.ApplyAsync(config, plan, "laptop");

        Assert.Equal(2, driver.Calls[^1].Count);
        Assert.False(await applier.RefreshVirtualDisplaysAsync(plan, "laptop", removing: true));
    }
}
