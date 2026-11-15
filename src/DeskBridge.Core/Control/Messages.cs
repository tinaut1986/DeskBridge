using System.Text.Json.Serialization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Control;

/// <summary>
/// Messages of the control channel: newline-delimited JSON over TCP.
/// Handshake (plain text): server sends <see cref="ChallengeMessage"/>, client answers <see cref="AuthMessage"/>,
/// server replies <see cref="WelcomeMessage"/> (with its own proof) or <see cref="ErrorMessage"/>.
/// Then encrypted <see cref="SessionFrame"/>s: either side can send requests on the same connection
/// (so a machine that cannot accept connections can still be reached over the one it opened).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionFrame), "frame")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(OkMessage), "ok")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ConfigSyncMessage), "configSync")]
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

/// <summary>A request (or the reply to request <paramref name="Id"/>) inside an authenticated session.</summary>
public sealed record SessionFrame(long Id, bool IsReply, ControlMessage Body) : ControlMessage;

/// <summary>Keeps a long-lived connection known to be alive; answered with <see cref="OkMessage"/>.</summary>
public sealed record PingMessage() : ControlMessage;

/// <param name="Machine">Id of the agent the client has reached.</param>
public sealed record ChallengeMessage(int Version, string Machine, string Nonce) : ControlMessage;

/// <param name="Link">The client keeps this session open as its link to a coordinator (it is that coordinator's client).</param>
public sealed record AuthMessage(string Machine, string Nonce, string Proof, bool Link = false) : ControlMessage;

public sealed record WelcomeMessage(string Proof) : ControlMessage;

public sealed record OkMessage() : ControlMessage;

public sealed record ErrorMessage(string Message) : ControlMessage;

/// <summary>The coordinator's current configuration, sent when a machine connects and whenever it is saved.</summary>
public sealed record ConfigSyncMessage(DeskBridgeConfig Config) : ControlMessage;

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
    /// <summary>2: mutual authentication and encrypted messages. 3: two-way sessions (requests from either side).</summary>
    public const int Version = 3;
}

public sealed class ControlException(string message) : Exception(message);
