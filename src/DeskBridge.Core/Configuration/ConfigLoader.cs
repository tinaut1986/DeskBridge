using System.Text.Json;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Configuration;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static DeskBridgeConfig Parse(string json) =>
        JsonSerializer.Deserialize<DeskBridgeConfig>(json, Options)
        ?? throw new InvalidDataException("El fichero de configuración está vacío.");

    public static DeskBridgeConfig Load(string path) => Parse(File.ReadAllText(path));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
