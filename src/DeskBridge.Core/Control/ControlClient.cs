using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Video;

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
        var (tcp, channel, machine, clientToServer, serverToClient) = await HandshakeAsync(host, port, localMachine, expectedMachine, key, asLink, video: null, ct);
        channel.Encrypt(sendKey: clientToServer, receiveKey: serverToClient);
        var session = new ControlSession(channel, tcp, machine, host, handler, log, peerIsLink: false);
        session.Start();
        return session;
    }

    /// <summary>Opens a video connection for stream <paramref name="streamKey"/> (same authentication, then binary packets).</summary>
    public static async Task<VideoChannel> OpenVideoAsync(
        string host, int port, string localMachine, string? expectedMachine, byte[] key, string streamKey, CancellationToken ct = default)
    {
        var (tcp, _, machine, clientToServer, serverToClient) = await HandshakeAsync(host, port, localMachine, expectedMachine, key, false, streamKey, ct);
        return new VideoChannel(tcp, tcp.GetStream(), machine, streamKey, sendKey: clientToServer, receiveKey: serverToClient);
    }

    private static async Task<(TcpClient Tcp, LineChannel Channel, string Machine, byte[] ClientToServer, byte[] ServerToClient)> HandshakeAsync(
        string host, int port, string localMachine, string? expectedMachine, byte[] key, bool asLink, string? video, CancellationToken ct)
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
            await channel.SendAsync(new AuthMessage(localMachine, clientNonce, proof, asLink, video), ct);

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
            return (tcp, channel, challenge.Machine, clientToServer, serverToClient);
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
