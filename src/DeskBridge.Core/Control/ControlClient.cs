using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

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
            throw new ControlException(L.T("Could not connect to {host}:{port} ({error}). Is the agent running and the port open in the firewall?",
                ("host", host), ("port", port), ("error", ex.Message)));
        }

        try
        {
            var channel = new LineChannel(tcp.GetStream());

            if (await channel.ReceiveAsync(ct) is not ChallengeMessage challenge)
                throw new ControlException(L.T("{host}:{port} does not look like a DeskBridge agent.", ("host", host), ("port", port)));
            if (challenge.Version != ControlProtocol.Version)
                throw new ControlException(L.T("Protocol version mismatch with {host} ({remote}, here {local}). Update both machines.",
                    ("host", host), ("remote", challenge.Version), ("local", ControlProtocol.Version)));
            if (challenge.Machine != expectedMachine)
                throw new ControlException(L.T("'{found}' answers at {host}, not '{expected}'. Check the address in the configuration.",
                    ("found", challenge.Machine), ("host", host), ("expected", expectedMachine)));

            var proof = ControlAuth.Proof(key, challenge.Nonce, localMachine, challenge.Machine);
            await channel.SendAsync(new AuthMessage(localMachine, proof), ct);

            switch (await channel.ReceiveAsync(ct))
            {
                case WelcomeMessage:
                    break;
                case ErrorMessage error:
                    throw new ControlException(error.Message);
                default:
                    throw new ControlException(L.T("Unexpected reply from {host} during authentication.", ("host", host)));
            }

            await channel.SendAsync(request, ct);
            return await channel.ReceiveAsync(ct)
                ?? throw new ControlException(L.T("{host} closed the connection without replying.", ("host", host)));
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            throw new ControlException(L.T("Communication error with {host}: {error}", ("host", host), ("error", ex.Message)));
        }
    }
}
