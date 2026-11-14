using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Transfer;

namespace DeskBridge.Core.Agent;

/// <summary>
/// The background part that runs on every machine: listens on the control channel and does what
/// the other machines ask (apply a profile, list displays, take a clipboard or a file). Hosted by
/// the GUI (normal use) or by 'deskbridge agent' (CLI).
/// </summary>
public sealed class AgentHost(
    LocalSettings local,
    PlanApplier applier,
    IDisplayDetector detector,
    Action<string> log,
    FileReceiver? files = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private ControlServer? _server;

    public string Machine => local.Machine;
    public int? Port => _server?.Port;
    public string? ActiveProfile { get; private set; }

    /// <summary>Raised (on a thread-pool thread) after a profile is applied.</summary>
    public event Action<string>? ProfileApplied;

    /// <summary>The configuration the coordinator sent with the last profile (so non-coordinators know the others).</summary>
    public event Action<DeskBridgeConfig>? ConfigReceived;

    /// <summary>Takes a clipboard from another machine; false (or unset) refuses it.</summary>
    public Func<string, ClipboardMessage, Task<bool>>? ClipboardHandler { get; set; }

    public void Start()
    {
        if (_server is not null)
            throw new InvalidOperationException("The agent is already running.");
        var server = new ControlServer(local.Machine, ControlAuth.DecodeKey(local.SharedKey), HandleAsync, local.ControlPort, log: log);
        server.Start(); // throws SocketException if the port is taken
        _server = server;
        log(L.T("Agent '{machine}' listening on port {port}.", ("machine", local.Machine), ("port", server.Port)));
    }

    private async Task<ControlMessage> HandleAsync(string from, ControlMessage request, CancellationToken ct)
    {
        switch (request)
        {
            case ApplyProfileMessage apply:
            {
                var plan = LayoutPlanner.Plan(apply.Config, apply.Profile);
                if (plan.Coordinator != from)
                    return new ErrorMessage(L.T("Only the coordinator ({coordinator}) can apply profiles.", ("coordinator", plan.Coordinator)));

                await _applyLock.WaitAsync(ct);
                try
                {
                    log(L.T("Applying profile '{profile}'...", ("profile", apply.Profile)));
                    await applier.ApplyAsync(apply.Config, plan, local.Machine, ct);
                    ActiveProfile = plan.Profile;
                }
                finally
                {
                    _applyLock.Release();
                }
                log(L.T("Profile '{profile}' applied.", ("profile", plan.Profile)));
                ConfigReceived?.Invoke(apply.Config);
                ProfileApplied?.Invoke(plan.Profile);
                return new AppliedMessage(local.Machine, plan.Profile, plan.Warnings);
            }
            case GetDisplaysMessage:
                return new DisplaysMessage(local.Machine, detector.DetectDisplays());

            case ClipboardMessage clipboard:
                return ClipboardHandler is { } handler && await handler(from, clipboard)
                    ? new OkMessage()
                    : new ErrorMessage(L.T("This machine does not accept the clipboard of other machines."));

            case FileStartMessage or FileChunkMessage or FileEndMessage:
                return files?.Handle(from, request)
                    ?? new ErrorMessage(L.T("This machine does not accept files from other machines. It can be enabled in its This PC tab."));

            default:
                return new ErrorMessage(L.T("Unexpected request: {request}.", ("request", request.GetType().Name)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
            log(L.T("Agent stopped."));
        }
    }
}
