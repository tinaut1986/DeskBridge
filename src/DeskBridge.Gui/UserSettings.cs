using System.IO;
using System.Text.Json;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui;

/// <summary>
/// Interface preferences of this user (%APPDATA%\DeskBridge\settings.json), separate from the
/// machine's identity (deskbridge.local.json) and the shared layout (deskbridge.json).
/// </summary>
/// <param name="Language">"system" or a language code.</param>
/// <param name="Betas">Update channel; null = follow the installed build's channel.</param>
public sealed record UserSettings(string Language = L.System, bool? Betas = null)
{
    public static string DefaultPath => Path.Combine(AppPaths.Directory, "settings.json");

    public static UserSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), ConfigLoader.JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(ConfigLoader.JsonOptions) { WriteIndented = true }));
    }
}
