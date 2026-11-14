using System.Globalization;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;

namespace DeskBridge.Core.Agent;

/// <summary>
/// For a machine that cannot accept incoming connections (a firewall it cannot change): its agent
/// connects to the coordinator itself and keeps that session open, pinging it, and reconnecting when
/// it drops. Everything the coordinator sends (profiles, clipboard, files) arrives over it.
/// </summary>
public sealed class ReverseLink : IAsyncDisposable
{
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly LocalSettings _local;
    private readonly string _host;
    private readonly int _port;
    private readonly ControlRequestHandler _handler;
    private readonly PeerDirectory _directory;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private string? _lastError;

    public ReverseLink(LocalSettings local, string host, int port, ControlRequestHandler handler, PeerDirectory directory, Action<string> log)
    {
        _local = local;
        _host = host;
        _port = port;
        _handler = handler;
        _directory = directory;
        _log = log;
    }

    /// <summary>The machine we are connected to, or null while (re)connecting.</summary>
    public string? ConnectedTo { get; private set; }

    public string Target => $"{_host}:{_port}";

    /// <summary>Raised (on a thread-pool thread) when the link connects or drops.</summary>
    public event Action? StateChanged;

    /// <summary>"host" or "host:port" (default port when absent).</summary>
    public static (string Host, int Port) ParseAddress(string address, int defaultPort)
    {
        var a = address.Trim();
        int colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':') == colon && int.TryParse(a[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            return (a[..colon], port);
        return (a, defaultPort);
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var key = ControlAuth.DecodeKey(_local.SharedKey);
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var session = await ControlClient.OpenAsync(_host, _port, _local.Machine, expectedMachine: null, key, _handler, _log, ct);
                _directory.Add(session);
                ConnectedTo = session.RemoteMachine;
                _lastError = null;
                backoff = TimeSpan.FromSeconds(2);
                _log(L.T("Connected to {machine} at {host}.", ("machine", session.RemoteMachine), ("host", Target)));
                StateChanged?.Invoke();

                await KeepAliveAsync(session, ct);
                if (!ct.IsCancellationRequested)
                    _log(L.T("Lost the connection to {machine}; reconnecting.", ("machine", session.RemoteMachine)));
            }
            catch (ControlException ex)
            {
                if (ex.Message != _lastError)
                {
                    _lastError = ex.Message;
                    _log(ex.Message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ConnectedTo is not null)
            {
                ConnectedTo = null;
                StateChanged?.Invoke();
            }
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>Returns when the session is gone (closed by the other side, or not answering pings).</summary>
    private static async Task KeepAliveAsync(ControlSession session, CancellationToken ct)
    {
        while (session.IsOpen && !ct.IsCancellationRequested)
        {
            await Task.WhenAny(session.Completion, Task.Delay(PingEvery, ct));
            if (!session.IsOpen || ct.IsCancellationRequested)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PingTimeout);
            try
            {
                await session.RequestAsync(new PingMessage(), timeout.Token);
            }
            catch (Exception ex) when (ex is ControlException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return; // the await using closes it; the loop reconnects
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}
