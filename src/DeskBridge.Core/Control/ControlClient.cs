using System.Net.Sockets;
using System.Text.Json;

namespace DeskBridge.Core.Control;

/// <summary>The coordinator side: connects to an agent, authenticates and sends one request.</summary>
public static class ControlClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <param name="expectedMachine">Id the agent must announce, to catch a wrong address in the config.</param>
    public static async Task<ControlMessage> SendAsync(
        string host,
        int port,
        string localMachine,
        string expectedMachine,
        byte[] key,
        ControlMessage request,
        CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ControlException($"Could not connect to {host}:{port} ({ex.Message}). Is the agent running and the port open in the firewall?");
        }

        try
        {
            var channel = new LineChannel(tcp.GetStream());

            if (await channel.ReceiveAsync(ct) is not ChallengeMessage challenge)
                throw new ControlException($"{host}:{port} does not look like a DeskBridge agent.");
            if (challenge.Version != ControlProtocol.Version)
                throw new ControlException($"Protocol version mismatch with {host} ({challenge.Version}, here {ControlProtocol.Version}). Update both machines.");
            if (challenge.Machine != expectedMachine)
                throw new ControlException($"'{challenge.Machine}' answers at {host}, not '{expectedMachine}'. Check the address in the configuration.");

            var proof = ControlAuth.Proof(key, challenge.Nonce, localMachine, challenge.Machine);
            await channel.SendAsync(new AuthMessage(localMachine, proof), ct);

            switch (await channel.ReceiveAsync(ct))
            {
                case WelcomeMessage:
                    break;
                case ErrorMessage error:
                    throw new ControlException(error.Message);
                default:
                    throw new ControlException($"Unexpected reply from {host} during authentication.");
            }

            await channel.SendAsync(request, ct);
            return await channel.ReceiveAsync(ct)
                ?? throw new ControlException($"{host} closed the connection without replying.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            throw new ControlException($"Communication error with {host}: {ex.Message}");
        }
    }
}
