using System.Security.Cryptography;
using System.Text;

namespace DeskBridge.Core.Control;

/// <summary>
/// Challenge-response with a shared key: the key never travels over the network.
/// The channel is authenticated but not encrypted (it only carries profile names and the layout).
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
            throw new ControlException("La clave compartida no es base64 válido. Genera una con 'deskbridge clave'.");
        }
        if (bytes.Length < 16)
            throw new ControlException("La clave compartida es demasiado corta. Genera una con 'deskbridge clave'.");
        return bytes;
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Proof(byte[] key, string nonce, string clientMachine, string serverMachine)
    {
        var payload = Encoding.UTF8.GetBytes($"deskbridge/v{ControlProtocol.Version}|{nonce}|{clientMachine}|{serverMachine}");
        return Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }

    public static bool Verify(byte[] key, string nonce, string clientMachine, string serverMachine, string proof)
    {
        var expected = Encoding.ASCII.GetBytes(Proof(key, nonce, clientMachine, serverMachine));
        var actual = Encoding.ASCII.GetBytes(proof ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
