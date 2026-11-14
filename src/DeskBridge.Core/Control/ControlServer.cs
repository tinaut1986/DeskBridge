using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>Handles one request; <c>from</c> is the authenticated machine that sent it.</summary>
public delegate Task<ControlMessage> ControlRequestHandler(string from, ControlMessage request, CancellationToken ct);

/// <summary>
/// The listening side of the control channel. It authenticates the other machine (and proves itself),
/// encrypts the session and hands each request to the handler.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly string _machine;
    private readonly byte[] _key;
    private readonly ControlRequestHandler _onRequest;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;

    public ControlServer(
        string machine,
        byte[] key,
        ControlRequestHandler onRequest,
        int port,
        IPAddress? bindAddress = null,
        Action<string>? log = null)
    {
        _machine = machine;
        _key = key;
        _onRequest = onRequest;
        _log = log ?? (_ => { });
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
    }

    /// <summary>The port actually listened on (useful when started with port 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        // Off the caller's synchronization context (e.g. the GUI thread), so connections never
        // queue behind the UI and disposing from the UI thread cannot deadlock.
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using (client)
        {
            try
            {
                var channel = new LineChannel(client.GetStream());
                var serverNonce = ControlAuth.NewNonce();
                await channel.SendAsync(new ChallengeMessage(ControlProtocol.Version, _machine, serverNonce), ct);

                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                authCts.CancelAfter(AuthTimeout);
                var auth = await channel.ReceiveAsync(authCts.Token) as AuthMessage;
                if (auth is null || !ValidNonce(auth.Nonce)
                    || !ControlAuth.Matches(ControlAuth.ClientProof(_key, serverNonce, auth.Nonce, auth.Machine, _machine), auth.Proof))
                {
                    _log(L.T("Rejected connection from {remote}: authentication failed.", ("remote", remote)));
                    await channel.SendAsync(new ErrorMessage(L.T("Authentication failed: is the shared key the same on both machines?")), ct);
                    return;
                }
                await channel.SendAsync(new WelcomeMessage(ControlAuth.ServerProof(_key, serverNonce, auth.Nonce, auth.Machine, _machine)), ct);
                var (clientToServer, serverToClient) = ControlAuth.SessionKeys(_key, serverNonce, auth.Nonce);
                channel.Encrypt(sendKey: serverToClient, receiveKey: clientToServer);

                while (await channel.ReceiveAsync(ct) is { } message)
                    await channel.SendAsync(await HandleRequestAsync(auth.Machine, message, ct), ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException
                                           or InvalidDataException or OperationCanceledException)
            {
                _log(L.T("Connection with {remote} closed: {error}", ("remote", remote), ("error", ex.Message)));
            }
        }
    }

    private static bool ValidNonce(string? nonce)
    {
        try
        {
            return nonce is not null && Convert.FromBase64String(nonce).Length >= 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<ControlMessage> HandleRequestAsync(string from, ControlMessage request, CancellationToken ct)
    {
        try
        {
            return await _onRequest(from, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(L.T("Request {request} failed: {error}", ("request", request.GetType().Name), ("error", ex.Message)));
            return new ErrorMessage(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        _stop.Dispose();
    }
}
