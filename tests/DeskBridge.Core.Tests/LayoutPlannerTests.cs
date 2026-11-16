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
    public void Virtual_displays_keep_the_lender_arrangement_beside_all_physical_displays()
    {
        var plan = LayoutPlanner.Plan(Example(), "work");

        // The laptop screen stays in the laptop's desktop (it shows the desktop's stream), so its two
        // virtual displays go to its right, side by side like desktop displays 2 and 3.
        var laptop = plan.VirtualDisplaysOf("laptop").OrderBy(v => v.X).ToList();
        Assert.Equal([(1920, 0, "2"), (3840, 0, "3")], laptop.Select(v => (v.X, v.Y, v.ForDisplay)));

        // Desktop displays 2 and 3 are lent but still in the desktop's Windows desktop: the
        // laptop screen goes after all three.
        var desktop = Assert.Single(plan.VirtualDisplaysOf("desktop"));
        Assert.Equal((5760, 0), (desktop.X, desktop.Y));
    }

    [Theory]
    [InlineData(PlacementSide.Right, 5760, 0)]
    [InlineData(PlacementSide.Left, -1920, 0)]
    [InlineData(PlacementSide.Above, 0, -1080)]
    [InlineData(PlacementSide.Below, 0, 1080)]
    public void Placement_side_is_relative_to_the_displays_the_borrower_keeps(PlacementSide side, int x, int y)
    {
        var config = Example();
        config = config with
        {
            Profiles = [new("p", [new DisplayAssignment("laptop", "1", "desktop")], new Dictionary<string, PlacementSide> { ["desktop"] = side })],
        };

        var vd = Assert.Single(LayoutPlanner.Plan(config, "p").VirtualDisplays);
        Assert.Equal((x, y), (vd.X, vd.Y));
    }

    [Fact]
    public void Stacked_lent_displays_stay_stacked_and_scale_with_the_stream_resolution()
    {
        var config = new DeskBridgeConfig(
            [
                new("a", "A", "10.0.0.1", [new DisplayConfig("1", 1920, 1080), new DisplayConfig("2", 1920, 1080, 60, 0, 1080)], HasInput: true),
                new("b", "B", "10.0.0.2", [new DisplayConfig("1", 1280, 800)]),
            ],
            [
                new("p",
                [
                    new DisplayAssignment("a", "1", "b", new StreamSettings(1280, 720)),
                    new DisplayAssignment("a", "2", "b", new StreamSettings(1280, 720)),
                ]),
            ]);

        var vds = LayoutPlanner.Plan(config, "p").VirtualDisplaysOf("b").OrderBy(v => v.Y).ToList();
        Assert.Equal([(1280, 0), (1280, 720)], vds.Select(v => (v.X, v.Y)));
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

        Assert.Contains(errors, e => e.Contains("keyboard and mouse"));
        Assert.Contains(errors, e => e.Contains("'a' has no display '9'"));
        Assert.Contains(errors, e => e.Contains("machine 'c' does not exist"));
        Assert.Contains(errors, e => e.Contains("machine 'zz' (used by) does not exist"));
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

    [Fact]
    public void A_virtual_display_shown_above_the_laptop_panel_goes_above_the_panel_on_the_laptop()
    {
        // The owner's desk: displays 2, 3, 1 in a row; the laptop panel (2880x1800 at 175%, matched to 1920 wide)
        // under display 3, which shows the laptop.
        var config = new DeskBridgeConfig(
            [
                new MachineConfig("skynet", "SKYNET", "192.168.1.52",
                    [new DisplayConfig("2", 1920, 1080, X: -1920), new DisplayConfig("3", 1920, 1080), new DisplayConfig("1", 1920, 1080, X: 1920)],
                    HasInput: true),
                new MachineConfig("laptop-oscar", "LAPTOP", "192.168.1.140", [new DisplayConfig("1", 2880, 1800, Scale: 175)],
                    DeskPositions: new Dictionary<string, VirtualPosition> { ["1"] = new(0, 1080) },
                    DeskWidths: new Dictionary<string, int> { ["1"] = 1920 }),
            ],
            [new ProfileConfig("work", [new DisplayAssignment("skynet", "3", "laptop-oscar")])]);

        var plan = LayoutPlanner.Plan(config, "work");
        var v = Assert.Single(plan.VirtualDisplays);
        Assert.Equal((1920, 1080, 0, -1080), (v.Width, v.Height, v.X, v.Y));
    }

    [Fact]
    public void A_primary_display_shown_on_the_coordinator_is_that_machines_virtual_display()
    {
        // a (the coordinator) shows b's virtual display on its display 3; b's primary is that one, a's is its 2.
        var config = TwoMachines(new DisplayConfig("3", 1920, 1080), null);
        config = config with
        {
            Profiles = [config.Profiles[0] with { PrimaryDisplays = new Dictionary<string, string> { ["b"] = "shows:3", ["a"] = "2", ["c"] = "shows:9" } }],
        };
        var plan = LayoutPlanner.Plan(config, "p");
        var shown = Assert.Single(plan.VirtualDisplaysOf("b"));
        Assert.Equal(shown.Key, plan.PrimaryDisplayOf("b"));
        Assert.Equal("2", plan.PrimaryDisplayOf("a"));
        Assert.Null(plan.PrimaryDisplayOf("c")); // nothing shown there: dropped
    }
}
