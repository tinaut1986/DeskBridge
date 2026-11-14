using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>
/// The listening side of the control channel. It authenticates the coordinator and hands
/// each request to <c>onRequest</c>, one at a time.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly string _machine;
    private readonly byte[] _key;
    private readonly Func<ControlMessage, CancellationToken, Task<ControlMessage>> _onRequest;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;

    public ControlServer(
        string machine,
        byte[] key,
        Func<ControlMessage, CancellationToken, Task<ControlMessage>> onRequest,
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
                var nonce = ControlAuth.NewNonce();
                await channel.SendAsync(new ChallengeMessage(ControlProtocol.Version, _machine, nonce), ct);

                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                authCts.CancelAfter(AuthTimeout);
                var auth = await channel.ReceiveAsync(authCts.Token) as AuthMessage;
                if (auth is null || !ControlAuth.Verify(_key, nonce, auth.Machine, _machine, auth.Proof))
                {
                    _log(L.T("Rejected connection from {remote}: authentication failed.", ("remote", remote)));
                    await channel.SendAsync(new ErrorMessage(L.T("Authentication failed: is the shared key the same on both machines?")), ct);
                    return;
                }
                await channel.SendAsync(new WelcomeMessage(), ct);

                while (await channel.ReceiveAsync(ct) is { } message)
                    await channel.SendAsync(await HandleRequestAsync(message, ct), ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException
                                           or InvalidDataException or OperationCanceledException)
            {
                _log(L.T("Connection with {remote} closed: {error}", ("remote", remote), ("error", ex.Message)));
            }
        }
    }

    private async Task<ControlMessage> HandleRequestAsync(ControlMessage request, CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct);
        try
        {
            return await _onRequest(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(L.T("Request {request} failed: {error}", ("request", request.GetType().Name), ("error", ex.Message)));
            return new ErrorMessage(ex.Message);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        _stop.Dispose();
        _requestLock.Dispose();
    }
}
