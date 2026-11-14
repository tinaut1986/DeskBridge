using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DeskBridge.Core.Control;

/// <summary>
/// The agent side of the control channel. Every machine runs one; it authenticates the
/// coordinator and applies the profiles it receives, one at a time.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    private readonly string _machine;
    private readonly byte[] _key;
    private readonly Func<ApplyProfileMessage, CancellationToken, Task<ControlMessage>> _onApply;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;

    public ControlServer(
        string machine,
        byte[] key,
        Func<ApplyProfileMessage, CancellationToken, Task<ControlMessage>> onApply,
        int port,
        IPAddress? bindAddress = null,
        Action<string>? log = null)
    {
        _machine = machine;
        _key = key;
        _onApply = onApply;
        _log = log ?? (_ => { });
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
    }

    /// <summary>The port actually listened on (useful when started with port 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
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
                    _log($"Conexión rechazada desde {remote}: autenticación fallida.");
                    await channel.SendAsync(new ErrorMessage("Autenticación fallida: ¿la clave compartida es la misma en los dos equipos?"), ct);
                    return;
                }
                await channel.SendAsync(new WelcomeMessage(), ct);
                _log($"'{auth.Machine}' conectado desde {remote}.");

                while (await channel.ReceiveAsync(ct) is { } message)
                {
                    var reply = message switch
                    {
                        ApplyProfileMessage apply => await ApplyAsync(apply, ct),
                        _ => new ErrorMessage($"Mensaje no esperado: {message.GetType().Name}."),
                    };
                    await channel.SendAsync(reply, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException
                                           or InvalidDataException or OperationCanceledException)
            {
                _log($"Conexión con {remote} cerrada: {ex.Message}");
            }
        }
    }

    private async Task<ControlMessage> ApplyAsync(ApplyProfileMessage message, CancellationToken ct)
    {
        await _applyLock.WaitAsync(ct);
        try
        {
            return await _onApply(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Error aplicando '{message.Profile}': {ex.Message}");
            return new ErrorMessage(ex.Message);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        _stop.Dispose();
        _applyLock.Dispose();
    }
}
