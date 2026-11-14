namespace DeskBridge.Core.Configuration;

/// <summary>
/// Per-machine settings that never leave the machine (deskbridge.local.json):
/// who this machine is and the shared key of the control channel.
/// </summary>
/// <param name="Machine">This machine's id in deskbridge.json.</param>
/// <param name="SharedKey">Same value on every machine; generate it with 'deskbridge clave'.</param>
/// <param name="ControlPort">Port the agent listens on; must match controlPort in deskbridge.json.</param>
public sealed record LocalSettings(string Machine, string SharedKey, int ControlPort = 47900)
{
    public static LocalSettings Load(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(path), ConfigLoader.JsonOptions)
        ?? throw new InvalidDataException($"'{path}' está vacío.");
}
