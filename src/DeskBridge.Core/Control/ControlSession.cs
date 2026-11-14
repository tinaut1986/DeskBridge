using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Control;

/// <summary>Handles one request; <c>from</c> is the authenticated machine that sent it.</summary>
public delegate Task<ControlMessage> ControlRequestHandler(string from, ControlMessage request, CancellationToken ct);

/// <summary>
/// An authenticated, encrypted connection with another machine where <b>both sides</b> can send requests.
/// Requests and replies are numbered (<see cref="SessionFrame"/>), so they can cross on the wire. Whoever
/// opened the connection does not matter: a machine whose firewall blocks incoming connections opens
/// one to the coordinator and is then reached over it.
/// </summary>
public sealed class ControlSession : IAsyncDisposable
{
    private readonly LineChannel _channel;
    private readonly IDisposable _transport;
    private readonly ControlRequestHandler? _handler;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ControlMessage>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _closedFlag;

    internal ControlSession(LineChannel channel, IDisposable transport, string remoteMachine, string remoteHost,
        ControlRequestHandler? handler, Action<string>? log)
    {
        _channel = channel;
        _transport = transport;
        _handler = handler;
        _log = log ?? (_ => { });
        RemoteMachine = remoteMachine;
        RemoteHost = remoteHost;
    }

    /// <summary>The authenticated id of the machine on the other end.</summary>
    public string RemoteMachine { get; }

    public string RemoteHost { get; }

    public bool IsOpen => Volatile.Read(ref _closedFlag) == 0;

    /// <summary>Completes when the connection is gone.</summary>
    public Task Completion => _closed.Task;

    /// <summary>Raised once, when the connection is gone.</summary>
    public event Action<ControlSession>? Closed;

    internal void Start() => _ = Task.Run(ReadLoopAsync);

    public async Task<ControlMessage> RequestAsync(ControlMessage request, CancellationToken ct = default)
    {
        if (!IsOpen)
            throw Lost();
        long id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await SendAsync(new SessionFrame(id, IsReply: false, request), ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            Close();
            throw Lost();
        }

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        });
        return await reply.Task;
    }

    private async Task SendAsync(SessionFrame frame, CancellationToken ct)
    {
        // The channel's encryption counters need one frame at a time.
        await _sendLock.WaitAsync(ct);
        try
        {
            await _channel.SendAsync(frame, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _channel.ReceiveJsonAsync(_closing.Token) is { } json)
            {
                var frame = ReadFrame(json);
                if (frame.IsReply)
                {
                    if (_pending.TryRemove(frame.Id, out var pending))
                    {
                        if (frame.Body is { } body)
                            pending.TrySetResult(body);
                        else
                            pending.TrySetException(new ControlException(Unknown(frame.Type)));
                    }
                }
                else if (frame.Body is { } request)
                {
                    // Not awaited: a slow request (applying a profile) must not hold up replies.
                    // Senders wait for each reply before the next request where order matters (file chunks).
                    _ = HandleAsync(frame.Id, request);
                }
                else
                {
                    // A request this version does not know (the other machine is newer): say so, keep the session.
                    _ = ReplyAsync(frame.Id, new ErrorMessage(Unknown(frame.Type)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException
                                       or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            if (IsOpen)
                _log(L.T("Connection with {remote} closed: {error}", ("remote", RemoteMachine), ("error", ex.Message)));
        }
        finally
        {
            Close();
        }
    }

    /// <summary>A received frame; <c>Body</c> is null when its message type is unknown to this version.</summary>
    private readonly record struct ReceivedFrame(long Id, bool IsReply, string Type, ControlMessage? Body);

    private static ReceivedFrame ReadFrame(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "frame"
            || !root.TryGetProperty("id", out var id) || !root.TryGetProperty("body", out var body))
            throw new InvalidDataException("Unexpected message outside a session frame.");
        bool isReply = root.TryGetProperty("isReply", out var r) && r.ValueKind == JsonValueKind.True;
        var bodyType = body.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
        ControlMessage? message;
        try
        {
            message = body.Deserialize<ControlMessage>(Configuration.ConfigLoader.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            message = null;
        }
        return new ReceivedFrame(id.GetInt64(), isReply, bodyType, message);
    }

    private static string Unknown(string type) =>
        L.T("The other machine sent '{request}', which this version of DeskBridge does not know. Update both machines.", ("request", type));

    private async Task HandleAsync(long id, ControlMessage request)
    {
        ControlMessage reply;
        try
        {
            reply = request switch
            {
                PingMessage => new OkMessage(),
                _ when _handler is null => new ErrorMessage(L.T("Unexpected request: {request}.", ("request", request.GetType().Name))),
                _ => await _handler(RemoteMachine, request, _closing.Token),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(L.T("Request {request} failed: {error}", ("request", request.GetType().Name), ("error", ex.Message)));
            reply = new ErrorMessage(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ReplyAsync(id, reply);
    }

    private async Task ReplyAsync(long id, ControlMessage reply)
    {
        try
        {
            await SendAsync(new SessionFrame(id, IsReply: true, reply), _closing.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close();
        }
    }

    private ControlException Lost() =>
        new(L.T("The connection with {machine} was lost.", ("machine", RemoteMachine)));

    private void Close()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
            return;
        _closing.Cancel();
        _transport.Dispose();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetException(Lost());
        }
        _closed.TrySetResult();
        Closed?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await Completion;
    }
}
