using DeskBridge.Core.Localization;
using System.Text.Json;

namespace DeskBridge.Core.Configuration;

/// <summary>
/// Per-machine settings that never leave the machine (deskbridge.local.json):
/// who this machine is and the shared key of the control channel.
/// </summary>
/// <param name="Machine">This machine's id in deskbridge.json.</param>
/// <param name="SharedKey">Same value on every machine; generate it with 'deskbridge keygen'.</param>
/// <param name="ControlPort">Port the agent listens on; must match the control port in deskbridge.json.</param>
/// <param name="ConnectTo">
/// "host" or "host:port" of the coordinator, for a machine that cannot accept incoming connections
/// (a firewall it cannot change): its agent connects there and is reached over that connection.
/// </param>
public sealed record LocalSettings(string Machine, string SharedKey, int ControlPort = 47900, string? ConnectTo = null)
{
    public static LocalSettings Load(string path)
    {
        var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException(L.T("'{path}' is empty.", ("path", path)));
        if (string.IsNullOrWhiteSpace(settings.Machine) || string.IsNullOrWhiteSpace(settings.SharedKey))
            throw new InvalidDataException(L.T("'{path}' does not say which machine this is or has no shared key.", ("path", path)));
        return settings;
    }

    public void Save(string path) => ConfigLoader.WriteJson(path, this);
}

/// <summary>Default file locations: %APPDATA%\DeskBridge.</summary>
public static class AppPaths
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskBridge");

    public static string Config => Path.Combine(Directory, "deskbridge.json");
    public static string Local => Path.Combine(Directory, "deskbridge.local.json");
}
