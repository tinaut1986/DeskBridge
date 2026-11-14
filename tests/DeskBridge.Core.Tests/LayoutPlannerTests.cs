using DeskBridge.Core.Configuration;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class LayoutPlannerTests
{
    private static DeskBridgeConfig Example()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deskbridge.example.json");
        return ConfigLoader.Load(path);
    }

    [Fact]
    public void Example_config_is_valid()
    {
        Assert.Empty(LayoutPlanner.Validate(Example()));
    }

    [Fact]
    public void Personal_profile_moves_nothing()
    {
        var plan = LayoutPlanner.Plan(Example(), "personal");

        Assert.Empty(plan.VirtualDisplays);
        Assert.Empty(plan.Streams);
        Assert.All(plan.InputRoutes, r => Assert.Equal("sobremesa", r.ToMachine));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Four_screens_profile_streams_laptop_screen_from_desktop()
    {
        var plan = LayoutPlanner.Plan(Example(), "cuatro-pantallas");

        var vd = Assert.Single(plan.VirtualDisplays);
        Assert.Equal("sobremesa", vd.Machine);
        var s = Assert.Single(plan.Streams);
        Assert.Equal(("sobremesa", "portatil", "1"), (s.SourceMachine, s.SinkMachine, s.SinkDisplay));
        Assert.Contains(plan.Warnings, w => w.Contains("portatil"));
    }

    [Fact]
    public void Work_profile_creates_two_virtual_displays_on_laptop_and_one_on_desktop()
    {
        var plan = LayoutPlanner.Plan(Example(), "trabajo");

        Assert.Equal(2, plan.VirtualDisplaysOf("portatil").Count());
        Assert.Single(plan.VirtualDisplaysOf("sobremesa"));
        Assert.Equal(3, plan.Streams.Count);

        Assert.Equal(["2", "3"], plan.StreamsTo("sobremesa").Select(s => s.SinkDisplay).Order());
        Assert.Equal("1", Assert.Single(plan.StreamsTo("portatil")).SinkDisplay);

        Assert.Equal("sobremesa", plan.InputRoutes.Single(r => r.Display == "1").ToMachine);
        Assert.Equal("portatil", plan.InputRoutes.Single(r => r.Display == "2").ToMachine);
        Assert.Equal("portatil", plan.InputRoutes.Single(r => r.Display == "3").ToMachine);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Virtual_display_matches_the_physical_display_it_is_shown_on()
    {
        var config = Example() with
        {
            Machines =
            [
                new("a", "A", "10.0.0.1", [new DisplayConfig("1", 2560, 1440, 144)], HasInput: true),
                new("b", "B", "10.0.0.2", [new DisplayConfig("1", 1920, 1080)]),
            ],
            Profiles = [new("p", [new DisplayAssignment("a", "1", "b")])],
        };

        var vd = Assert.Single(LayoutPlanner.Plan(config, "p").VirtualDisplays);
        Assert.Equal(("b", 2560, 1440, 144), (vd.Machine, vd.Width, vd.Height, vd.RefreshHz));
    }

    [Fact]
    public void Unknown_profile_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() => LayoutPlanner.Plan(Example(), "no-existe"));
        Assert.Contains(ex.Errors, e => e.Contains("no-existe"));
    }

    [Fact]
    public void Bad_references_and_coordinator_count_are_reported()
    {
        var config = new DeskBridgeConfig(
            [
                new("a", "A", "10.0.0.1", [new DisplayConfig("1", 1920, 1080)]),
                new("b", "B", "10.0.0.2", [new DisplayConfig("1", 1920, 1080)]),
            ],
            [
                new("p",
                [
                    new DisplayAssignment("a", "9", "b"),
                    new DisplayAssignment("c", "1", "a"),
                    new DisplayAssignment("b", "1", "zz"),
                ]),
            ]);

        var errors = LayoutPlanner.Validate(config);

        Assert.Contains(errors, e => e.Contains("hasInput"));
        Assert.Contains(errors, e => e.Contains("'a' no tiene pantalla '9'"));
        Assert.Contains(errors, e => e.Contains("'c' no existe"));
        Assert.Contains(errors, e => e.Contains("'zz' (usedBy) no existe"));
    }
}
