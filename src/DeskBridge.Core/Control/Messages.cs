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
[JsonDerivedType(typeof(InputEnterMessage), "inputEnter")]
[JsonDerivedType(typeof(InputEventsMessage), "inputEvents")]
[JsonDerivedType(typeof(InputLeaveMessage), "inputLeave")]
[JsonDerivedType(typeof(InputReleaseMessage), "inputRelease")]
[JsonDerivedType(typeof(ConfigSyncMessage), "configSync")]
[JsonDerivedType(typeof(GetDiagnosticsMessage), "getDiagnostics")]
[JsonDerivedType(typeof(DiagnosticsMessage), "diagnostics")]
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

public enum InputKind { MouseMove, MouseButton, Wheel, Key }

/// <summary>
/// One keyboard or mouse event forwarded from the coordinator. MouseMove: X/Y are a relative move in
/// pixels. MouseButton: Code 0 left, 1 right, 2 middle, 3 X1, 4 X2. Wheel: X is the delta (120 per
/// notch), Extended = horizontal. Key: Code is the scan code, Extended the E0 prefix.
/// </summary>
public sealed record InputEvent(InputKind Kind, int X = 0, int Y = 0, int Code = 0, bool Down = false, bool Extended = false);

/// <summary>The coordinator hands the cursor over: it enters this machine through the edge facing the coordinator.</summary>
/// <param name="Side">Where this machine is relative to the coordinator (it enters through the opposite edge).</param>
/// <param name="Fraction">Position along that edge, 0 to 1 (top to bottom, or left to right).</param>
public sealed record InputEnterMessage(PlacementSide Side, double Fraction) : ControlMessage;

/// <summary>Keyboard and mouse events while this machine has the cursor (sent as notifications, in order).</summary>
public sealed record InputEventsMessage(IReadOnlyList<InputEvent> Events) : ControlMessage;

/// <summary>The cursor went back past the edge facing the coordinator, at this position along it.</summary>
public sealed record InputLeaveMessage(double Fraction) : ControlMessage;

/// <summary>The coordinator took the cursor back: release every key and button still held here.</summary>
public sealed record InputReleaseMessage() : ControlMessage;

/// <summary>Asks a machine for its state, to troubleshoot it from another one.</summary>
public sealed record GetDiagnosticsMessage() : ControlMessage;

/// <summary>A machine's state for troubleshooting: version, settings (never the shared key), configuration, recent log.</summary>
public sealed record DiagnosticsMessage(
    string Machine,
    string Version,
    string? ConnectTo,
    int ControlPort,
    bool IsCoordinator,
    DeskBridgeConfig? Config,
    IReadOnlyList<string> Log) : ControlMessage;

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
