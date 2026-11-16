using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Transfer;
using DeskBridge.Core.Video;

namespace DeskBridge.Core.Agent;

/// <summary>
/// The background part that runs on every machine: listens on the control channel (and, when set, keeps
/// a connection open to the coordinator) and does what the other machines ask: apply a profile, list
/// displays, take a clipboard or a file. Hosted by the GUI (normal use) or by 'deskbridge agent' (CLI).
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly LocalSettings local;
    private readonly PlanApplier applier;
    private readonly IDisplayDetector detector;
    private readonly Action<string> log;
    private readonly FileReceiver? files;

    public AgentHost(LocalSettings local, PlanApplier applier, IDisplayDetector detector, Action<string> log, FileReceiver? files = null)
    {
        this.local = local;
        this.applier = applier;
        this.detector = detector;
        this.log = log;
        this.files = files;
        AcceptConfigFrom = ConnectsTo;
        AcceptProfilesFrom = from => from == local.Machine || ConnectsTo(from)
                                     || (string.IsNullOrWhiteSpace(local.ConnectTo) && !IsLinkedClient(from));
    }

    /// <summary>Machines holding a link session to this one: our clients, never our coordinator.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ControlSession, string> _linkedClients = new();

    /// <summary>The machine holds a link session to this one: it is our client, never our coordinator.</summary>
    public bool IsLinkedClient(string machine) => _linkedClients.Values.Contains(machine);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warnedMutual = new();

    private void OnSessionOpened(ControlSession session)
    {
        if (session.PeerIsLink && _reverse is { } reverse && reverse.ConnectedTo == session.RemoteMachine
            && _warnedMutual.TryAdd(session.RemoteMachine, 0))
        {
            // Both machines have Connect to pointing at each other: each new session replaces the other's.
            log(L.T("{machine} and this PC both have Connect to set to each other. Only the client should; clear it on the coordinator.",
                ("machine", session.RemoteMachine)));
        }
        if (session.PeerIsLink)
        {
            _linkedClients[session] = session.RemoteMachine;
            session.Closed += s => _linkedClients.TryRemove(s, out _);
        }
        Directory.Add(session);
    }

    private void OnVideoOpened(VideoChannel channel)
    {
        if (VideoOpened is { } handler)
            handler(channel);
        else
            _ = channel.DisposeAsync().AsTask();
    }

    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private ControlServer? _server;
    private ReverseLink? _reverse;

    public string Machine => local.Machine;
    public int? Port => _server?.Port;
    public string? ActiveProfile { get; private set; }

    /// <summary>Open sessions with other machines (incoming, or the one to the coordinator).</summary>
    public PeerDirectory Directory { get; } = new();

    /// <summary>The coordinator this machine connects to, when <see cref="LocalSettings.ConnectTo"/> is set.</summary>
    public ReverseLink? Reverse => _reverse;

    /// <summary>Raised (on a thread-pool thread) after a profile is applied.</summary>
    public event Action<string>? ProfileApplied;

    /// <summary>Raised (on a thread-pool thread) after a profile is applied, with its whole plan.</summary>
    public event Action<LayoutPlan>? PlanApplied;

    /// <summary>A plan is about to be applied here (before its virtual displays are placed).</summary>
    public event Action<LayoutPlan>? PlanApplying;

    private (DeskBridgeConfig Config, LayoutPlan Plan)? _lastApplied;

    /// <summary>
    /// Puts the last applied profile in place again here (e.g. once a driver it needed is installed).
    /// False when no profile was applied yet.
    /// </summary>
    public async Task<bool> ReapplyAsync(CancellationToken ct = default)
    {
        if (_lastApplied is not { } last)
            return false;
        await _applyLock.WaitAsync(ct);
        try
        {
            log(L.T("Applying profile '{profile}'...", ("profile", last.Plan.Profile)));
            await applier.ApplyAsync(last.Config, last.Plan, local.Machine, ct);
        }
        finally
        {
            _applyLock.Release();
        }
        return true;
    }

    /// <summary>The configuration the coordinator sent with the last profile (so non-coordinators know the others).</summary>
    public event Action<DeskBridgeConfig>? ConfigReceived;

    /// <summary>Takes a clipboard from another machine; false (or unset) refuses it.</summary>
    public Func<string, ClipboardMessage, Task<bool>>? ClipboardHandler { get; set; }

    /// <summary>
    /// Whether a machine may replace this one's configuration (configSync, or the config carried by a
    /// profile). Default: only the coordinator this agent connects to; an agent that connects to
    /// nobody keeps its own configuration (it is the coordinator, or is set up by hand).
    /// </summary>
    public Func<string, bool> AcceptConfigFrom { get; set; }

    /// <summary>
    /// Whether a machine may apply profiles here. Default: this machine itself, or the coordinator it
    /// connects to; without a coordinator to connect to, the one named in the profile's config.
    /// </summary>
    public Func<string, bool> AcceptProfilesFrom { get; set; }

    private bool ConnectsTo(string machine) => _reverse?.ConnectedTo == machine;

    /// <summary>
    /// Keyboard and mouse sharing messages (enter, events, release on the machine that gets the cursor;
    /// leave on the coordinator). Unset: they are ignored.
    /// </summary>
    public Func<string, ControlMessage, Task>? InputHandler { get; set; }

    /// <summary>
    /// Video streams: start/stop requests from the coordinator (<see cref="StartVideoMessage"/>,
    /// <see cref="StopVideoMessage"/>). Unset: refused.
    /// </summary>
    public Func<string, ControlMessage, CancellationToken, Task<ControlMessage>>? VideoHandler { get; set; }

    /// <summary>Remote updates and video probes (troubleshooting from the coordinator). Unset: refused.</summary>
    public Func<string, ControlMessage, CancellationToken, Task<ControlMessage>>? MaintenanceHandler { get; set; }

    /// <summary>Raised (on a thread-pool thread) for every video connection another machine opens to this one; the handler owns it.</summary>
    public event Action<VideoChannel>? VideoOpened;

    /// <summary>Host and port of the coordinator this machine connects to (for its video connections).</summary>
    public (string Host, int Port)? CoordinatorEndpoint =>
        string.IsNullOrWhiteSpace(local.ConnectTo) ? null : ReverseLink.ParseAddress(local.ConnectTo, local.ControlPort);

    /// <summary>This machine's state for another machine troubleshooting it (set by the GUI; basic otherwise).</summary>
    public Func<DiagnosticsMessage>? DiagnosticsProvider { get; set; }

    /// <param name="listen">
    /// Accept incoming connections. Off is fine on a machine that only connects out; if the port is
    /// taken and <see cref="LocalSettings.ConnectTo"/> is set, the agent carries on without listening.
    /// </param>
    public void Start(bool listen = true)
    {
        if (_server is not null || _reverse is not null)
            throw new InvalidOperationException("The agent is already running.");

        if (listen)
        {
            var server = new ControlServer(local.Machine, ControlAuth.DecodeKey(local.SharedKey), HandleAsync, local.ControlPort, log: log);
            server.SessionOpened += OnSessionOpened;
            server.VideoOpened += OnVideoOpened;
            try
            {
                server.Start();
                _server = server;
                log(L.T("Agent '{machine}' listening on port {port}.", ("machine", local.Machine), ("port", server.Port)));
            }
            catch (System.Net.Sockets.SocketException) when (!string.IsNullOrWhiteSpace(local.ConnectTo))
            {
                log(L.T("Port {port} is busy: this agent only connects out.", ("port", local.ControlPort)));
            }
        }

        if (!string.IsNullOrWhiteSpace(local.ConnectTo))
        {
            ControlAuth.DecodeKey(local.SharedKey); // fail now, not in the background loop
            var (host, port) = ReverseLink.ParseAddress(local.ConnectTo, local.ControlPort);
            _reverse = new ReverseLink(local, host, port, HandleAsync, Directory, log);
            _reverse.Start();
        }
    }

    private async Task<ControlMessage> HandleAsync(string from, ControlMessage request, CancellationToken ct)
    {
        switch (request)
        {
            case ApplyProfileMessage apply:
            {
                var plan = LayoutPlanner.Plan(apply.Config, apply.Profile);
                if (plan.Coordinator != from || !AcceptProfilesFrom(from))
                    return new ErrorMessage(L.T("Only the coordinator ({coordinator}) can apply profiles.", ("coordinator", plan.Coordinator)));

                await _applyLock.WaitAsync(ct);
                try
                {
                    log(L.T("Applying profile '{profile}'...", ("profile", apply.Profile)));
                    PlanApplying?.Invoke(plan);
                    await applier.ApplyAsync(apply.Config, plan, local.Machine, ct);
                    ActiveProfile = plan.Profile;
                    _lastApplied = (apply.Config, plan);
                }
                finally
                {
                    _applyLock.Release();
                }
                log(L.T("Profile '{profile}' applied.", ("profile", plan.Profile)));
                if (AcceptConfigFrom(from))
                    ConfigReceived?.Invoke(apply.Config);
                ProfileApplied?.Invoke(plan.Profile);
                PlanApplied?.Invoke(plan);
                return new AppliedMessage(local.Machine, plan.Profile, plan.Warnings);
            }
            case RelayMessage relay:
            {
                // Only the command line on this very PC (a session with this machine's own id comes only from loopback).
                if (from != local.Machine)
                    return new ErrorMessage(L.T("Only this PC can relay requests to other machines."));
                if (Directory.Find(relay.Target) is not { } target)
                    return new ErrorMessage(L.T("{machine} is not connected.", ("machine", relay.Target)));
                return await target.RequestAsync(relay.Inner, ct);
            }

            case UpdateStartMessage or UpdateChunkMessage or UpdateEndMessage or VideoProbeMessage or ProbeMessage:
                return MaintenanceHandler is { } maintenance
                    ? await maintenance(from, request, ct)
                    : new ErrorMessage(L.T("Unexpected request: {request}.", ("request", request.GetType().Name)));

            case StartVideoMessage or StopVideoMessage:
                return VideoHandler is { } video
                    ? await video(from, request, ct)
                    : new ErrorMessage(L.T("This machine cannot stream video."));

            case GetDisplaysMessage:
                return new DisplaysMessage(local.Machine, detector.DetectDisplays());

            case InputEnterMessage or InputEventsMessage or InputLeaveMessage or InputReleaseMessage:
                if (InputHandler is { } input)
                    await input(from, request);
                return new OkMessage();

            case GetDiagnosticsMessage:
                return DiagnosticsProvider?.Invoke()
                    ?? new DiagnosticsMessage(local.Machine, "?", local.ConnectTo, local.ControlPort, false, null, []);

            case ConfigSyncMessage sync:
                // Only the coordinator this machine connects to may replace its configuration, and only
                // with one that names that coordinator (a machine that wrongly thinks it is the
                // coordinator must not overwrite the real one's configuration).
                if (!AcceptConfigFrom(from) || sync.Config.Machines.FirstOrDefault(m => m.HasInput)?.Id != from)
                    return new ErrorMessage(L.T("Only the coordinator can change the configuration."));
                ConfigReceived?.Invoke(sync.Config);
                return new OkMessage();

            case ClipboardMessage clipboard:
                return ClipboardHandler is { } handler && await handler(from, clipboard)
                    ? new OkMessage()
                    : new ErrorMessage(L.T("This machine does not accept the clipboard of other machines."));

            case FileStartMessage or FileChunkMessage or FileEndMessage:
                return files?.Handle(from, request)
                    ?? new ErrorMessage(L.T("This machine does not accept files from other machines. It can be enabled on it, in Sharing."));

            default:
                return new ErrorMessage(L.T("Unexpected request: {request}.", ("request", request.GetType().Name)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool running = _server is not null || _reverse is not null;
        if (_reverse is not null)
        {
            await _reverse.DisposeAsync();
            _reverse = null;
        }
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
        if (running)
            log(L.T("Agent stopped."));
    }
}
