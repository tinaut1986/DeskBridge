using DeskBridge.Core.Configuration;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Tests;

public class LayoutPlannerTests
{
    internal static DeskBridgeConfig Example() =>
        ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "deskbridge.example.json"));

    private static DeskBridgeConfig TwoMachines(DisplayConfig lent, StreamSettings? stream) => new(
        [
            new("a", "A", "10.0.0.1", [lent], HasInput: true),
            new("b", "B", "10.0.0.2", [new DisplayConfig("1", 1920, 1080)]),
        ],
        [new("p", [new DisplayAssignment("a", lent.Id, "b", stream)])]);

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
        Assert.All(plan.InputRoutes, r => Assert.Equal("desktop", r.ToMachine));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Four_screens_profile_streams_laptop_screen_from_desktop()
    {
        var plan = LayoutPlanner.Plan(Example(), "four-screens");

        var vd = Assert.Single(plan.VirtualDisplays);
        Assert.Equal("desktop", vd.Machine);
        var s = Assert.Single(plan.Streams);
        Assert.Equal(("desktop", "laptop", "1"), (s.SourceMachine, s.SinkMachine, s.SinkDisplay));
        Assert.Contains(plan.Warnings, w => w.Contains("laptop"));
    }

    [Fact]
    public void Work_profile_creates_two_virtual_displays_on_laptop_and_one_on_desktop()
    {
        var plan = LayoutPlanner.Plan(Example(), "work");

        Assert.Equal(2, plan.VirtualDisplaysOf("laptop").Count());
        Assert.Single(plan.VirtualDisplaysOf("desktop"));
        Assert.Equal(3, plan.Streams.Count);

        Assert.Equal(["2", "3"], plan.StreamsTo("desktop").Select(s => s.SinkDisplay).Order());
        Assert.Equal("1", Assert.Single(plan.StreamsTo("laptop")).SinkDisplay);

        Assert.Equal("desktop", plan.InputRoutes.Single(r => r.Display == "1").ToMachine);
        Assert.Equal("laptop", plan.InputRoutes.Single(r => r.Display == "2").ToMachine);
        Assert.Equal("laptop", plan.InputRoutes.Single(r => r.Display == "3").ToMachine);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Laptop_with_two_screens_can_lend_both()
    {
        var config = Example();
        var laptop = config.Machines.Single(m => m.Id == "laptop");
        config = config with
        {
            Machines =
            [
                config.Machines.Single(m => m.Id == "desktop"),
                laptop with { Displays = [.. laptop.Displays, new DisplayConfig("2", 2560, 1440, 60, 1920, 0)] },
            ],
            Profiles =
            [
                new("laptop-lends-all",
                [
                    new DisplayAssignment("laptop", "1", "desktop"),
                    new DisplayAssignment("laptop", "2", "desktop"),
                ]),
            ],
        };

        var plan = LayoutPlanner.Plan(config, "laptop-lends-all");

        Assert.Equal(2, plan.VirtualDisplaysOf("desktop").Count());
        Assert.Contains(plan.VirtualDisplays, v => (v.Width, v.Height) == (2560, 1440));
    }

    [Fact]
    public void By_default_virtual_display_matches_the_physical_display_it_is_shown_on()
    {
        var plan = LayoutPlanner.Plan(TwoMachines(new DisplayConfig("1", 2560, 1440, 144), null), "p");

        var vd = Assert.Single(plan.VirtualDisplays);
        Assert.Equal(("b", 2560, 1440, 144), (vd.Machine, vd.Width, vd.Height, vd.RefreshHz));
        Assert.Equal(LayoutPlanner.DefaultBitrateMbps(2560, 1440, 144), Assert.Single(plan.Streams).BitrateMbps);
    }

    [Fact]
    public void Stream_settings_override_resolution_refresh_and_bitrate()
    {
        var plan = LayoutPlanner.Plan(
            TwoMachines(new DisplayConfig("1", 2560, 1440, 144), new StreamSettings(1920, 1080, 60, 25)), "p");

        var s = Assert.Single(plan.Streams);
        Assert.Equal((1920, 1080, 60, 25), (s.Width, s.Height, s.RefreshHz, s.BitrateMbps));
        var vd = Assert.Single(plan.VirtualDisplays);
        Assert.Equal((1920, 1080, 60), (vd.Width, vd.Height, vd.RefreshHz));
        // Same 16:9 aspect and lower refresh: nothing to warn about the stream itself.
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("aspect ratio") || w.Contains("bandwidth"));
    }

    [Fact]
    public void Mismatched_aspect_ratio_and_excess_refresh_are_warned()
    {
        var plan = LayoutPlanner.Plan(
            TwoMachines(new DisplayConfig("1", 1920, 1080, 60), new StreamSettings(1920, 1200, 120)), "p");

        Assert.Contains(plan.Warnings, w => w.Contains("aspect ratio"));
        Assert.Contains(plan.Warnings, w => w.Contains("wastes bandwidth"));
    }

    [Fact]
    public void Default_bitrate_is_about_20_mbps_for_1080p60()
    {
        Assert.InRange(LayoutPlanner.DefaultBitrateMbps(1920, 1080, 60), 18, 22);
    }

    [Theory]
    [InlineData(1920, null, null, null, "both width and height")]
    [InlineData(100, 1080, null, null, "width must be")]
    [InlineData(1920, 1080, 500, null, "refresh rate must be")]
    [InlineData(null, null, null, 0, "bitrate must be")]
    public void Invalid_stream_settings_are_reported(int? w, int? h, int? hz, int? mbps, string expected)
    {
        var errors = LayoutPlanner.Validate(TwoMachines(new DisplayConfig("1", 1920, 1080), new StreamSettings(w, h, hz, mbps)));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Unknown_profile_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() => LayoutPlanner.Plan(Example(), "missing"));
        Assert.Contains(ex.Errors, e => e.Contains("missing"));
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
        Assert.Contains(errors, e => e.Contains("'a' has no display '9'"));
        Assert.Contains(errors, e => e.Contains("machine 'c' does not exist"));
        Assert.Contains(errors, e => e.Contains("machine 'zz' (usedBy) does not exist"));
    }

    [Fact]
    public void Config_round_trips_through_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskbridge-{Guid.NewGuid():N}.json");
        try
        {
            var original = Example();
            ConfigLoader.Save(path, original);
            var loaded = ConfigLoader.Load(path);

            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(original, ConfigLoader.JsonOptions),
                System.Text.Json.JsonSerializer.Serialize(loaded, ConfigLoader.JsonOptions));
            Assert.Equal(30, loaded.Profiles.Single(p => p.Name == "work").Assignments.Single(a => a.Display == "3").Stream!.BitrateMbps);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
