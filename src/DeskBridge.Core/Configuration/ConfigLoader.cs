using DeskBridge.Core.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Configuration;

public static class ConfigLoader
{
    /// <summary>Shared by config files and the control channel (which needs single-line output).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions FileOptions = new(JsonOptions) { WriteIndented = true };

    public static DeskBridgeConfig Parse(string json) =>
        JsonSerializer.Deserialize<DeskBridgeConfig>(json, JsonOptions)
        ?? throw new InvalidDataException(L.T("The configuration file is empty."));

    public static DeskBridgeConfig Load(string path) => Parse(File.ReadAllText(path));

    public static void Save(string path, DeskBridgeConfig config) => WriteJson(path, config);

    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Write then move, so a crash never leaves a half-written file behind.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, FileOptions));
        File.Move(temp, path, overwrite: true);
    }
}
