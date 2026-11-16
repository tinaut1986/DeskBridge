using DeskBridge.Platform.Linux;

namespace DeskBridge.Core.Tests;

public class PlatformTests
{
    /// <summary>Shape of `kscreen-doctor -j` on Plasma 6 (trimmed): a laptop panel scaled 1.25 and an external monitor to its right.</summary>
    private const string KScreen = """
        {
          "outputs": [
            {
              "id": 1, "name": "eDP-1", "connected": true, "enabled": true,
              "currentModeId": "1", "pos": { "x": 0, "y": 0 }, "scale": 1.25,
              "size": { "width": 1536, "height": 864 },
              "modes": [
                { "id": "1", "name": "1920x1080@60", "refreshRate": 60.0, "size": { "width": 1920, "height": 1080 } },
                { "id": "2", "name": "1280x720@60", "refreshRate": 59.94, "size": { "width": 1280, "height": 720 } }
              ]
            },
            {
              "id": 2, "name": "DP-2", "connected": true, "enabled": true,
              "currentModeId": "7", "pos": { "x": 1536, "y": 0 }, "scale": 1,
              "size": { "width": 2560, "height": 1440 },
              "modes": [ { "id": "7", "name": "2560x1440@143.97", "refreshRate": 143.97, "size": { "width": 2560, "height": 1440 } } ]
            },
            { "id": 3, "name": "HDMI-A-1", "connected": false, "enabled": false, "modes": [] },
            { "id": 4, "name": "DP-3", "connected": true, "enabled": false, "currentModeId": "", "modes": [] }
          ]
        }
        """;

    [Fact]
    public void Kscreen_outputs_become_displays_with_physical_mode_and_position()
    {
        var displays = KScreenDisplayDetector.Parse(KScreen);

        Assert.Equal(2, displays.Count);
        Assert.Equal(("1", 1920, 1080, 60, 0, 0), (displays[0].Id, displays[0].Width, displays[0].Height, displays[0].RefreshHz, displays[0].X, displays[0].Y));
        Assert.Equal(("2", 2560, 1440, 144, 1536, 0), (displays[1].Id, displays[1].Width, displays[1].Height, displays[1].RefreshHz, displays[1].X, displays[1].Y));
    }

    [Fact]
    public void Broken_kscreen_output_gives_no_displays()
    {
        Assert.Empty(KScreenDisplayDetector.Parse("not json"));
        Assert.Empty(KScreenDisplayDetector.Parse("{}"));
    }

    [Fact]
    public void Autostart_entry_starts_this_executable_minimized()
    {
        var entry = XdgAutostart.Entry("/home/me/Apps/DeskBridge");

        Assert.Contains("Exec=\"/home/me/Apps/DeskBridge\" --minimized", entry);
        Assert.Contains("[Desktop Entry]", entry);
    }
}
