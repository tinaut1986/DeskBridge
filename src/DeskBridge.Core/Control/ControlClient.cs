using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>
/// An authenticated, encrypted connection to another machine's agent. Several requests can go
/// over it one after another (a file is sent as many chunks on the same connection).
/// </summary>
public sealed class ControlConnection : IDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpClient _tcp;
    private readonly LineChannel _channel;
    private readonly string _host;

    private ControlConnection(TcpClient tcp, LineChannel channel, string host)
    {
        _tcp = tcp;
        _channel = channel;
        _host = host;
    }

    /// <param name="expectedMachine">Id the agent must announce, to catch a wrong address in the config.</param>
    public static async Task<ControlConnection> OpenAsync(
        string host, int port, string localMachine, string expectedMachine, byte[] key, CancellationToken ct = default)
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
            var channel = new LineChannel(tcp.GetStream());

            if (await channel.ReceiveAsync(ct) is not ChallengeMessage challenge)
                throw new ControlException(L.T("{host}:{port} does not look like a DeskBridge agent.", ("host", host), ("port", port)));
            if (challenge.Version != ControlProtocol.Version)
                throw new ControlException(L.T("Protocol version mismatch with {host} ({remote}, here {local}). Update both machines.",
                    ("host", host), ("remote", challenge.Version), ("local", ControlProtocol.Version)));
            if (challenge.Machine != expectedMachine)
                throw new ControlException(L.T("'{found}' answers at {host}, not '{expected}'. Check the address in the configuration.",
                    ("found", challenge.Machine), ("host", host), ("expected", expectedMachine)));

            var clientNonce = ControlAuth.NewNonce();
            var proof = ControlAuth.ClientProof(key, challenge.Nonce, clientNonce, localMachine, challenge.Machine);
            await channel.SendAsync(new AuthMessage(localMachine, clientNonce, proof), ct);

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
            return new ControlConnection(tcp, channel, host);
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

    public async Task<ControlMessage> RequestAsync(ControlMessage request, CancellationToken ct = default)
    {
        try
        {
            await _channel.SendAsync(request, ct);
            return await _channel.ReceiveAsync(ct)
                ?? throw new ControlException(L.T("{host} closed the connection without replying.", ("host", _host)));
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            throw new ControlException(L.T("Communication error with {host}: {error}", ("host", _host), ("error", ex.Message)));
        }
    }

    public void Dispose() => _tcp.Dispose();
}

/// <summary>One request on a fresh connection.</summary>
public static class ControlClient
{
    public static async Task<ControlMessage> SendAsync(
        string host, int port, string localMachine, string expectedMachine, byte[] key, ControlMessage request, CancellationToken ct = default)
    {
        using var connection = await ControlConnection.OpenAsync(host, port, localMachine, expectedMachine, key, ct);
        return await connection.RequestAsync(request, ct);
    }
}
