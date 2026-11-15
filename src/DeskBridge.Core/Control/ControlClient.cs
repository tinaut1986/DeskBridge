using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>Opens sessions to other machines' agents.</summary>
public static class ControlClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Connects, authenticates both ways and starts an encrypted session.
    /// </summary>
    /// <param name="expectedMachine">Id the agent must announce (catches a wrong address); null accepts any.</param>
    /// <param name="handler">Answers requests the other side sends over this session; null refuses them.</param>
    public static async Task<ControlSession> OpenAsync(
        string host,
        int port,
        string localMachine,
        string? expectedMachine,
        byte[] key,
        ControlRequestHandler? handler = null,
        Action<string>? log = null,
        CancellationToken ct = default,
        bool asLink = false)
    {
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new ControlException(L.T("Could not connect to {host}:{port} ({error}). Is the agent running and the port open in the firewall?",
                ("host", host), ("port", port), ("error", ex.Message)));
        }

        try
        {
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            tcp.NoDelay = true; // small, frequent messages (mouse moves) must not wait for more data
            var channel = new LineChannel(tcp.GetStream());

            if (await channel.ReceiveAsync(ct) is not ChallengeMessage challenge)
                throw new ControlException(L.T("{host}:{port} does not look like a DeskBridge agent.", ("host", host), ("port", port)));
            if (challenge.Version != ControlProtocol.Version)
                throw new ControlException(L.T("Protocol version mismatch with {host} ({remote}, here {local}). Update both machines.",
                    ("host", host), ("remote", challenge.Version), ("local", ControlProtocol.Version)));
            if (expectedMachine is not null && challenge.Machine != expectedMachine)
                throw new ControlException(L.T("'{found}' answers at {host}, not '{expected}'. Check the address in the configuration.",
                    ("found", challenge.Machine), ("host", host), ("expected", expectedMachine)));

            var clientNonce = ControlAuth.NewNonce();
            var proof = ControlAuth.ClientProof(key, challenge.Nonce, clientNonce, localMachine, challenge.Machine);
            await channel.SendAsync(new AuthMessage(localMachine, clientNonce, proof, asLink), ct);

            switch (await channel.ReceiveAsync(ct))
            {
                case WelcomeMessage welcome
                    when ControlAuth.Matches(ControlAuth.ServerProof(key, challenge.Nonce, clientNonce, localMachine, challenge.Machine), welcome.Proof):
                    break;
                case WelcomeMessage:
                    throw new ControlException(L.T("{host} could not prove it has the shared key.", ("host", host)));
                case ErrorMessage error:
                    throw new ControlException(error.Message);
                default:
                    throw new ControlException(L.T("Unexpected reply from {host} during authentication.", ("host", host)));
            }

            var (clientToServer, serverToClient) = ControlAuth.SessionKeys(key, challenge.Nonce, clientNonce);
            channel.Encrypt(sendKey: clientToServer, receiveKey: serverToClient);
            var session = new ControlSession(channel, tcp, challenge.Machine, host, handler, log, peerIsLink: false);
            session.Start();
            return session;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            tcp.Dispose();
            throw new ControlException(L.T("Communication error with {host}: {error}", ("host", host), ("error", ex.Message)));
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>One request on a fresh session, closed afterwards.</summary>
    public static async Task<ControlMessage> SendAsync(
        string host, int port, string localMachine, string expectedMachine, byte[] key, ControlMessage request, CancellationToken ct = default)
    {
        await using var session = await OpenAsync(host, port, localMachine, expectedMachine, key, ct: ct);
        return await session.RequestAsync(request, ct);
    }
}
