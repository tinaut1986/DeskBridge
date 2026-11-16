using DeskBridge.Core.Localization;
using System.Security.Cryptography;
using System.Text;

namespace DeskBridge.Core.Control;

/// <summary>
/// Mutual challenge-response with the shared key (which never travels), then per-session keys for
/// AES-GCM derived with HKDF from the key and both sides' nonces: every message after the handshake
/// is encrypted and authenticated, so clipboard contents and files cannot be read or altered on the LAN.
/// </summary>
public static class ControlAuth
{
    public const int KeyBytes = 32;

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public static byte[] DecodeKey(string key)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw new ControlException(L.T("The shared key is not valid. Generate a new one."));
        }
        if (bytes.Length < 16)
            throw new ControlException(L.T("The shared key is too short. Generate a new one."));
        return bytes;
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>What the client sends: proves it has the key, bound to both nonces and both names.</summary>
    public static string ClientProof(byte[] key, string serverNonce, string clientNonce, string client, string server) =>
        Mac(key, $"deskbridge/v{ControlProtocol.Version}/client|{serverNonce}|{clientNonce}|{client}|{server}");

    /// <summary>What the server answers: proves it has the key too (a fake agent gets nothing).</summary>
    public static string ServerProof(byte[] key, string serverNonce, string clientNonce, string client, string server) =>
        Mac(key, $"deskbridge/v{ControlProtocol.Version}/server|{serverNonce}|{clientNonce}|{client}|{server}");

    public static bool Matches(string expected, string? actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual ?? ""));

    /// <summary>Session keys, one per direction.</summary>
    public static (byte[] ClientToServer, byte[] ServerToClient) SessionKeys(byte[] key, string serverNonce, string clientNonce)
    {
        var salt = Convert.FromBase64String(serverNonce).Concat(Convert.FromBase64String(clientNonce)).ToArray();
        byte[] Derive(string direction) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, Encoding.UTF8.GetBytes($"deskbridge/v{ControlProtocol.Version} {direction}"));
        return (Derive("client->server"), Derive("server->client"));
    }

    private static string Mac(byte[] key, string text) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
}
