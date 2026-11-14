using System.Text.Json.Serialization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Control;

/// <summary>
/// Messages of the control channel: newline-delimited JSON over TCP.
/// Handshake (plain text): server sends <see cref="ChallengeMessage"/>, client answers <see cref="AuthMessage"/>,
/// server replies <see cref="WelcomeMessage"/> (with its own proof) or <see cref="ErrorMessage"/>.
/// Then encrypted request/reply pairs, as many as the client wants on the same connection.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(OkMessage), "ok")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ApplyProfileMessage), "applyProfile")]
[JsonDerivedType(typeof(AppliedMessage), "applied")]
[JsonDerivedType(typeof(GetDisplaysMessage), "getDisplays")]
[JsonDerivedType(typeof(DisplaysMessage), "displays")]
[JsonDerivedType(typeof(ClipboardMessage), "clipboard")]
[JsonDerivedType(typeof(FileStartMessage), "fileStart")]
[JsonDerivedType(typeof(FileChunkMessage), "fileChunk")]
[JsonDerivedType(typeof(FileEndMessage), "fileEnd")]
[JsonDerivedType(typeof(FileSavedMessage), "fileSaved")]
public abstract record ControlMessage;

/// <param name="Machine">Id of the agent the client has reached.</param>
public sealed record ChallengeMessage(int Version, string Machine, string Nonce) : ControlMessage;

public sealed record AuthMessage(string Machine, string Nonce, string Proof) : ControlMessage;

public sealed record WelcomeMessage(string Proof) : ControlMessage;

public sealed record OkMessage() : ControlMessage;

public sealed record ErrorMessage(string Message) : ControlMessage;

/// <summary>The coordinator sends the whole config, so it only has to be edited on one machine.</summary>
public sealed record ApplyProfileMessage(DeskBridgeConfig Config, string Profile) : ControlMessage;

public sealed record AppliedMessage(string Machine, string Profile, IReadOnlyList<string> Warnings) : ControlMessage;

/// <summary>Asks an agent for the physical monitors of its machine, so the coordinator can fill the config.</summary>
public sealed record GetDisplaysMessage() : ControlMessage;

public sealed record DisplaysMessage(string Machine, IReadOnlyList<DisplayConfig> Displays) : ControlMessage;

/// <summary>Something copied on another machine: text, or an image as PNG (base64).</summary>
public sealed record ClipboardMessage(string? Text, string? PngBase64) : ControlMessage;

/// <summary>Starts sending one file; the agent answers ok (accepted) or error (refused).</summary>
public sealed record FileStartMessage(Guid Transfer, string Name, long Size) : ControlMessage;

public sealed record FileChunkMessage(Guid Transfer, long Offset, string DataBase64) : ControlMessage;

/// <summary>The whole file has been sent; the agent checks the hash and keeps it.</summary>
public sealed record FileEndMessage(Guid Transfer, string Sha256) : ControlMessage;

public sealed record FileSavedMessage(string Path) : ControlMessage;

public static class ControlProtocol
{
    /// <summary>2: mutual authentication and encrypted messages.</summary>
    public const int Version = 2;
}

public sealed class ControlException(string message) : Exception(message);
