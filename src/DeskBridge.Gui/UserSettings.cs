using System.IO;
using System.Text.Json;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui;

/// <summary>Light, dark or whatever Windows uses.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Preferences of this user on this machine (%APPDATA%\DeskBridge\settings.json), separate from the
/// machine's identity (deskbridge.local.json) and the shared layout (deskbridge.json).
/// Sharing is off by default: one of the machines may be a work computer.
/// </summary>
/// <param name="Language">"system" or a language code.</param>
/// <param name="Betas">Update channel; null = follow the installed build's channel.</param>
/// <param name="ClipboardSend">Send what is copied here to the other machines.</param>
/// <param name="ClipboardReceive">Put here what is copied on the other machines.</param>
/// <param name="AcceptFiles">Accept files sent by the other machines.</param>
/// <param name="FilesFolder">Where received files go; null = Downloads\DeskBridge.</param>
/// <param name="ShareInput">On the coordinator: its keyboard and mouse move to another machine past the edge.</param>
/// <param name="AcceptInput">On a client: the coordinator's keyboard and mouse may control this PC.</param>
/// <param name="AcceptUpdates">The coordinator may install new DeskBridge builds here (troubleshooting).</param>
/// <param name="SendAudio">On a client: everything this PC plays is heard on the coordinator (and muted here).</param>
/// <param name="PlayAudio">On the coordinator: the sound other machines send is played here.</param>
/// <param name="AudioVolumes">On the coordinator: volume of each machine's sound, in percent (100 when missing).</param>
/// <param name="ShareMic">On the coordinator: the other machines may use its microphone.</param>
/// <param name="UseRemoteMic">On a client: the coordinator's microphone comes out of VB-CABLE here.</param>
/// <param name="ShowFrames">Each display gets a thin border in the colour of the machine it belongs to.</param>
/// <param name="VideoAtOnce">On the coordinator: video goes on screen at once, not at the monitor's next refresh (may tear).</param>
/// <param name="CloseToTray">Closing the window keeps DeskBridge running in the notification area.</param>
public sealed record UserSettings(
    string Language = L.System,
    bool? Betas = null,
    bool ClipboardSend = false,
    bool ClipboardReceive = false,
    bool AcceptFiles = false,
    string? FilesFolder = null,
    ThemeChoice Theme = ThemeChoice.System,
    bool CloseToTray = true,
    bool ShareInput = false,
    bool AcceptInput = false,
    bool AcceptUpdates = false,
    bool SendAudio = false,
    bool PlayAudio = true,
    Dictionary<string, int>? AudioVolumes = null,
    bool ShareMic = false,
    bool UseRemoteMic = false,
    bool ShowFrames = true,
    bool VideoAtOnce = false)
{
    public static string DefaultPath => Path.Combine(AppPaths.Directory, "settings.json");

    public static string DefaultFilesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeskBridge");

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveFilesFolder => string.IsNullOrWhiteSpace(FilesFolder) ? DefaultFilesFolder : FilesFolder;

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
