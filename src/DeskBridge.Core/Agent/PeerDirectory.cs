using System.Collections.Concurrent;
using DeskBridge.Core.Control;

namespace DeskBridge.Core.Agent;

/// <summary>
/// Sessions currently open with other machines, whoever opened them. Before dialling a machine, this
/// machine looks here: if that machine connected to us (e.g. a laptop whose firewall blocks incoming
/// connections), we talk to it over that session.
/// </summary>
public sealed class PeerDirectory
{
    private readonly ConcurrentDictionary<string, ControlSession> _sessions = new();

    /// <summary>Raised (on any thread) when a machine connects or disconnects.</summary>
    public event Action? Changed;

    public void Add(ControlSession session)
    {
        if (_sessions.TryGetValue(session.RemoteMachine, out var previous) && previous != session)
            _ = previous.DisposeAsync().AsTask(); // the machine reconnected: the newest session wins
        _sessions[session.RemoteMachine] = session;
        session.Closed += s =>
        {
            if (_sessions.TryRemove(new KeyValuePair<string, ControlSession>(s.RemoteMachine, s)))
                Changed?.Invoke();
        };
        Changed?.Invoke();
        if (!session.IsOpen && _sessions.TryRemove(new KeyValuePair<string, ControlSession>(session.RemoteMachine, session)))
            Changed?.Invoke(); // closed before we subscribed
    }

    public ControlSession? Find(string machine) =>
        _sessions.TryGetValue(machine, out var s) && s.IsOpen ? s : null;

    public IReadOnlyCollection<string> Connected => _sessions.Where(kv => kv.Value.IsOpen).Select(kv => kv.Key).ToList();
}

/// <summary>A session to talk to a machine: one already open (left open) or one opened just for this (closed after).</summary>
public sealed class PeerLink : IAsyncDisposable
{
    private readonly bool _owned;

    internal PeerLink(ControlSession session, bool owned)
    {
        Session = session;
        _owned = owned;
    }

    public ControlSession Session { get; }

    public Task<ControlMessage> RequestAsync(ControlMessage request, CancellationToken ct = default) => Session.RequestAsync(request, ct);

    public ValueTask DisposeAsync() => _owned ? Session.DisposeAsync() : ValueTask.CompletedTask;
}
