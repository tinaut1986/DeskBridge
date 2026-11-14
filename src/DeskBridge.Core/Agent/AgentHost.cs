using DeskBridge.Core.Abstractions;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Agent;

/// <summary>
/// The background part that runs on every machine: listens on the control channel and does
/// what the coordinator asks. Hosted by the GUI (normal use) or by 'deskbridge agent' (CLI).
/// </summary>
public sealed class AgentHost(LocalSettings local, PlanApplier applier, IDisplayDetector detector, Action<string> log)
    : IAsyncDisposable
{
    private ControlServer? _server;

    public string Machine => local.Machine;
    public int? Port => _server?.Port;
    public string? ActiveProfile { get; private set; }

    /// <summary>Raised (on a thread-pool thread) after a profile is applied.</summary>
    public event Action<string>? ProfileApplied;

    public void Start()
    {
        if (_server is not null)
            throw new InvalidOperationException("The agent is already running.");
        var server = new ControlServer(local.Machine, ControlAuth.DecodeKey(local.SharedKey), HandleAsync, local.ControlPort, log: log);
        server.Start(); // throws SocketException if the port is taken
        _server = server;
        log(L.T("Agent '{machine}' listening on port {port}.", ("machine", local.Machine), ("port", server.Port)));
    }

    private async Task<ControlMessage> HandleAsync(ControlMessage request, CancellationToken ct)
    {
        switch (request)
        {
            case ApplyProfileMessage apply:
            {
                log(L.T("Applying profile '{profile}'...", ("profile", apply.Profile)));
                var plan = LayoutPlanner.Plan(apply.Config, apply.Profile);
                await applier.ApplyAsync(apply.Config, plan, local.Machine, ct);
                ActiveProfile = plan.Profile;
                log(L.T("Profile '{profile}' applied.", ("profile", plan.Profile)));
                ProfileApplied?.Invoke(plan.Profile);
                return new AppliedMessage(local.Machine, plan.Profile, plan.Warnings);
            }
            case GetDisplaysMessage:
                return new DisplaysMessage(local.Machine, detector.DetectDisplays());
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
