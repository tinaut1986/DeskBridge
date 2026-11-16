using System.Windows.Threading;
using DeskBridge.Core.Control;
using DeskBridge.Core.Input;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Gui.Sharing;
using DeskBridge.Platform;

namespace DeskBridge.Gui;

/// <summary>
/// Keyboard and mouse sharing: on the coordinator (with the option on) the cursor moves to a connected machine
/// past the edge where that machine is; on a client (with the option on) the coordinator's input is played here.
/// </summary>
public partial class MainWindow
{
    private KeyboardMouseShare? _share;
    private InputReceiver? _receiver;
    private DispatcherTimer? _inputTimer;
    private string? _inputRemote;   // coordinator: machine that has the cursor
    private string? _controlledBy;  // client: coordinator controlling this PC

    private static IReadOnlyList<DisplayRect> LocalDisplays() =>
        PlatformServices.DisplayDetector().DetectDisplays().Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList();

    private ControlSession? SessionOf(string machine) => _agent?.Directory.Find(machine);

    /// <summary>Connected machines that can get the cursor, with where their displays are around this PC's.</summary>
    private IReadOnlyList<InputTarget> InputTargets(IReadOnlyList<DisplayRect> local)
    {
        if (_agent is null || local.Count == 0)
            return [];
        var connected = _agent.Directory.Connected.ToHashSet();
        // Desk positions are in the original arrangement's coordinates (see DeskOffset).
        var (ox, oy) = DeskOffset();
        // Displays of other machines placed on this PC's desk (Layout tab): reached past the edge where they touch.
        var edges = _vm.Model.Machines
            .Where(m => m.Id.Trim() != _vm.LocalMachine.Trim() && connected.Contains(m.Id.Trim()))
            .SelectMany(m => m.Displays
                .Where(d => m.DeskPositions.ContainsKey(d.Id.Trim()))
                .Select(d => InputLayout.Desk(m.Id.Trim(), new DisplayRect(d.X, d.Y, d.Width, d.Height),
                    new VirtualPosition(m.DeskPositions[d.Id.Trim()].X + ox, m.DeskPositions[d.Id.Trim()].Y + oy),
                    InputLayout.DeskScale(d.Width, d.Height, d.Scale, m.DeskWidths.TryGetValue(d.Id.Trim(), out var w) ? w : null))));
        // Displays here that show another machine's screen: entering one moves the cursor to that machine (where
        // that display is now: it moves when the primary display changes).
        var here = PlatformServices.DisplayDetector().DetectDisplays().ToDictionary(d => d.Id, d => new DisplayRect(d.X, d.Y, d.Width, d.Height));
        var shown = _lentDisplays.Values
            .Where(l => connected.Contains(l.Machine) && here.ContainsKey(l.Display))
            .Select(l => (l.Machine, Here: here[l.Display], l.There))
            .ToList();
        var lent = shown.Select(l => InputLayout.Lent(l.Machine, l.Here, l.There));
        // A display of that machine shown here AND placed on the desk (no virtual display yet: its main display is
        // streamed instead) would be on the desk twice and the cursor would bounce between both: only the display
        // showing it counts while that lasts.
        edges = edges.Where(e => !shown.Any(l => l.Machine == e.Machine && e.Displays[0] == l.There));
        return lent.Concat(edges).ToList();
    }

    private InputReceiver Receiver() => _receiver ??= new InputReceiver(
        () => _settings.AcceptInput,
        LocalDisplays,
        SessionOf,
        from => Dispatcher.BeginInvoke(() =>
        {
            _controlledBy = from;
            UpdateAgentStatus();
        }),
        DeskOffset,
        () => _secureLink);

    /// <summary>Called on the agent's connection thread for every keyboard and mouse sharing message.</summary>
    private Task HandleInputAsync(string from, ControlMessage message)
    {
        if (message is InputLeaveMessage leave)
        {
            _share?.OnLeave(from, leave);
            return Task.CompletedTask;
        }
        return Receiver().HandleAsync(from, message);
    }

    /// <summary>Starts or stops the coordinator's hooks to match the option and this PC's role.</summary>
    private void UpdateInputSharing()
    {
        bool wanted = _settings.ShareInput && _agent is not null && IsCoordinatorHere() && _options.SnapshotDir is null;
        if (!wanted)
        {
            StopInputSharing();
            return;
        }
        if (_share is null)
        {
            try
            {
                var local = LocalDisplays();
                _share = new KeyboardMouseShare(local, InputTargets(local), SessionOf, AddLog,
                    machine => Dispatcher.BeginInvoke(() =>
                    {
                        _inputRemote = machine;
                        UpdateAgentStatus();
                    }));
            }
            catch (InvalidOperationException ex)
            {
                AddLog(ex.Message);
                return;
            }
            _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _inputTimer.Tick += (_, _) => RefreshInputTargets();
            _inputTimer.Start();
        }
        else
        {
            RefreshInputTargets();
        }
    }

    private void RefreshInputTargets()
    {
        var local = LocalDisplays();
        _share?.Update(local, InputTargets(local));
    }

    private void StopInputSharing()
    {
        _inputTimer?.Stop();
        _inputTimer = null;
        _share?.Dispose();
        _share = null;
        _inputRemote = null;
    }

    /// <summary>Extra status while the keyboard and mouse are on another machine, or controlled from one.</summary>
    private string? InputStatus() =>
        _inputRemote is { } remote ? L.T("keyboard and mouse on {machine} (Ctrl+Alt+Home to come back)", ("machine", MachineName(remote)))
        : _controlledBy is { } coordinator ? L.T("controlled from {machine}", ("machine", MachineName(coordinator)))
        : null;
}
