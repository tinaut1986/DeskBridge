using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Model;

namespace DeskBridge.Platform.Linux;

/// <summary>
/// Monitors on KDE Plasma (Wayland or X11) from <c>kscreen-doctor -j</c>: connected, enabled outputs with
/// their position and current mode. Ids are the output numbers kscreen-doctor shows (1, 2...).
/// </summary>
public sealed class KScreenDisplayDetector : IDisplayDetector
{
    public IReadOnlyList<DisplayConfig> DetectDisplays()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("kscreen-doctor", "-j")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return [];
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Parse(json);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return []; // kscreen-doctor not installed: not a KDE session
        }
    }

    /// <summary>Parses kscreen-doctor's JSON (public for tests: it runs anywhere).</summary>
    public static IReadOnlyList<DisplayConfig> Parse(string json)
    {
        var result = new List<DisplayConfig>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var o in outputs.EnumerateArray())
            {
                if (!Bool(o, "connected") || !Bool(o, "enabled"))
                    continue;

                var currentMode = Str(o, "currentModeId");
                JsonElement? mode = null;
                if (o.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array)
                    mode = modes.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(m => Str(m!.Value, "id") == currentMode);

                // Physical pixels: the mode's size (the output "size" can be the scaled, logical one).
                var (width, height) = mode is { } m1 && m1.TryGetProperty("size", out var ms) ? Size(ms)
                    : o.TryGetProperty("size", out var os) ? Size(os) : (0, 0);
                if (width <= 0 || height <= 0)
                    continue;

                int refresh = mode is { } m2 && m2.TryGetProperty("refreshRate", out var r) && r.TryGetDouble(out var hz)
                    ? (int)Math.Round(hz)
                    : 60;
                var (x, y) = o.TryGetProperty("pos", out var pos) ? Point(pos) : (0, 0);
                var id = o.TryGetProperty("id", out var idElement) ? idElement.ToString() : (result.Count + 1).ToString(CultureInfo.InvariantCulture);

                result.Add(new DisplayConfig(id, width, height, refresh, x, y));
            }
        }
        return result.OrderBy(d => int.TryParse(d.Id, out var n) ? n : int.MaxValue).ToList();

        static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
        static (int, int) Size(JsonElement e) => (Int(e, "width"), Int(e, "height"));
        static (int, int) Point(JsonElement e) => (Int(e, "x"), Int(e, "y"));
        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? (int)Math.Round(d) : 0;
    }
}
