using System.Text.Json.Serialization;
using DeskBridge.Core.Model;

namespace DeskBridge.Core.Control;

/// <summary>
/// Messages of the control channel: newline-delimited JSON over TCP.
/// Handshake: server sends <see cref="ChallengeMessage"/>, client answers <see cref="AuthMessage"/>,
/// server replies <see cref="WelcomeMessage"/> or <see cref="ErrorMessage"/>. Then request/reply.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(ApplyProfileMessage), "applyProfile")]
[JsonDerivedType(typeof(AppliedMessage), "applied")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record ControlMessage;

/// <param name="Machine">Id of the agent the client has reached.</param>
public sealed record ChallengeMessage(int Version, string Machine, string Nonce) : ControlMessage;

public sealed record AuthMessage(string Machine, string Proof) : ControlMessage;

public sealed record WelcomeMessage() : ControlMessage;

/// <summary>The coordinator sends the whole config, so it only has to be edited on one machine.</summary>
public sealed record ApplyProfileMessage(DeskBridgeConfig Config, string Profile) : ControlMessage;

public sealed record AppliedMessage(string Machine, string Profile, IReadOnlyList<string> Warnings) : ControlMessage;

public sealed record ErrorMessage(string Message) : ControlMessage;

public static class ControlProtocol
{
    public const int Version = 1;
}

public sealed class ControlException(string message) : Exception(message);
