using System.Text.Json;
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
    };

    public static DeskBridgeConfig Parse(string json) =>
        JsonSerializer.Deserialize<DeskBridgeConfig>(json, JsonOptions)
        ?? throw new InvalidDataException("El fichero de configuración está vacío.");

    public static DeskBridgeConfig Load(string path) => Parse(File.ReadAllText(path));
}
