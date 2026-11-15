using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>
/// The listening side of the control channel. It authenticates the other machine (and proves itself),
/// encrypts the session and keeps it open: requests from the other side go to the handler, and the
/// session is announced (<see cref="SessionOpened"/>) so this side can send requests over it too.
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
    private readonly List<ControlSession> _sessions = [];
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

    /// <summary>Raised (on a thread-pool thread) for every authenticated incoming session.</summary>
    public event Action<ControlSession>? SessionOpened;

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
            _ = HandshakeAsync(client, ct);
        }
    }

    private async Task HandshakeAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var channel = new LineChannel(client.GetStream());
            var serverNonce = ControlAuth.NewNonce();
            await channel.SendAsync(new ChallengeMessage(ControlProtocol.Version, _machine, serverNonce), ct);

            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            authCts.CancelAfter(AuthTimeout);
            var received = await channel.ReceiveAsync(authCts.Token);
            if (received is null)
            {
                // The other side hung up before authenticating (e.g. it saw this is not the machine it wanted).
                client.Dispose();
                return;
            }
            var auth = received as AuthMessage;
            if (auth is null || !ValidNonce(auth.Nonce)
                || !ControlAuth.Matches(ControlAuth.ClientProof(_key, serverNonce, auth.Nonce, auth.Machine, _machine), auth.Proof))
            {
                _log(L.T("Rejected connection from {remote}: authentication failed.", ("remote", remote)));
                await channel.SendAsync(new ErrorMessage(L.T("Authentication failed: is the shared key the same on both machines?")), ct);
                client.Dispose();
                return;
            }
            bool fromThisPc = client.Client.RemoteEndPoint is IPEndPoint { Address: var address } && IPAddress.IsLoopback(address);
            if (auth.Machine == _machine && !fromThisPc)
            {
                // Two machines with the same id would be mistaken for one another (it happens if both keep a default name).
                var sameName = L.T("The other machine uses the same name '{machine}' as this one. Change This PC is on one of them.", ("machine", _machine));
                _log(sameName);
                await channel.SendAsync(new ErrorMessage(sameName), ct);
                client.Dispose();
                return;
            }
            await channel.SendAsync(new WelcomeMessage(ControlAuth.ServerProof(_key, serverNonce, auth.Nonce, auth.Machine, _machine)), ct);
            var (clientToServer, serverToClient) = ControlAuth.SessionKeys(_key, serverNonce, auth.Nonce);
            channel.Encrypt(sendKey: serverToClient, receiveKey: clientToServer);

            var host = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? remote;
            var session = new ControlSession(channel, client, auth.Machine, host, (from, request, token) => _onRequest(from, request, token), _log, peerIsLink: auth.Link);
            lock (_sessions)
                _sessions.Add(session);
            session.Closed += s =>
            {
                lock (_sessions)
                    _sessions.Remove(s);
                if (!fromThisPc)
                    _log(L.T("{machine} disconnected.", ("machine", s.RemoteMachine)));
            };
            session.Start();
            if (!fromThisPc) // the coordinator talking to its own agent is not news
                _log(L.T("{machine} connected from {host}.", ("machine", auth.Machine), ("host", host)));
            SessionOpened?.Invoke(session);
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException
                                       or InvalidDataException or OperationCanceledException)
        {
            _log(L.T("Connection with {remote} closed: {error}", ("remote", remote), ("error", ex.Message)));
            client.Dispose();
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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        List<ControlSession> open;
        lock (_sessions)
            open = [.. _sessions];
        foreach (var session in open)
            await session.DisposeAsync();
        _stop.Dispose();
    }
}
