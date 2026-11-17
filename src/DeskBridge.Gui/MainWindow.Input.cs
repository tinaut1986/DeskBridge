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

    /// <summary>
    /// The coordinator's desktop for keyboard and mouse sharing: its own virtual displays too (those another machine
    /// shows, e.g. the tablet under the monitors). Without them, the cursor coming back from a machine shown on a
    /// display above one of them found nothing there and stayed stuck at that machine's edge.
    /// </summary>
    private static IReadOnlyList<DisplayRect> CoordinatorDisplays() =>
        Platform.Windows.WindowsDisplayDetector.DetectAll().Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList();

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
        // Not one that shows a screen of this PC in the applied profile: that screen is a display of this PC, placed
        // right there, and the cursor simply moves onto it.
        var showingThisPc = (_streamPlan?.Streams ?? [])
            .Where(s => s.SourceMachine == _vm.LocalMachine.Trim())
            .Select(s => (s.SinkMachine, s.SinkDisplay))
            .ToHashSet();
        // A display on the desk that shows a third machine (the tablet showing the laptop, relayed through this PC):
        // the cursor goes to the machine shown there, onto the display it sends, at the size of the one showing it.
        // Not to the machine the display belongs to: it would not even take the cursor (it is busy showing).
        var shownElsewhere = (_streamPlan?.Streams ?? [])
            .Where(s => s.SourceMachine != _vm.LocalMachine.Trim() && s.SinkMachine != _vm.LocalMachine.Trim())
            .ToDictionary(s => (s.SinkMachine, s.SinkDisplay), s => s);
        var edges = new List<InputTarget>();
        foreach (var m in _vm.Model.Machines.Where(m => m.Id.Trim() != _vm.LocalMachine.Trim() && connected.Contains(m.Id.Trim())))
        {
            foreach (var d in m.Displays.Where(d => m.DeskPositions.ContainsKey(d.Id.Trim()) && !showingThisPc.Contains((m.Id.Trim(), d.Id.Trim()))))
            {
                var position = new VirtualPosition(m.DeskPositions[d.Id.Trim()].X + ox, m.DeskPositions[d.Id.Trim()].Y + oy);
                int? deskWidth = m.DeskWidths.TryGetValue(d.Id.Trim(), out var w) ? w : null;
                if (shownElsewhere.TryGetValue((m.Id.Trim(), d.Id.Trim()), out var stream))
                {
                    // Only once the stream is there (where the sender's display is): until then, nowhere.
                    if (_relays.TryGetValue(stream.Key, out var relay) && relay.There is { } there && connected.Contains(stream.SourceMachine))
                    {
                        double scale = (double)InputLayout.DeskSize(d.Width, d.Height, d.Scale, deskWidth).Width / Math.Max(1, there.Width);
                        edges.Add(InputLayout.Desk(stream.SourceMachine, there, position, scale));
                    }
                    continue;
                }
                edges.Add(InputLayout.Desk(m.Id.Trim(), new DisplayRect(d.X, d.Y, d.Width, d.Height), position,
                    InputLayout.DeskScale(d.Width, d.Height, d.Scale, deskWidth)));
            }
        }
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
        return lent.Concat(edges.Where(e => !shown.Any(l => l.Machine == e.Machine && e.Displays[0] == l.There))).ToList();
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
        if (message is InputCursorMessage cursor)
        {
            _share?.OnCursor(from, cursor);
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
                var local = CoordinatorDisplays();
                _share = new KeyboardMouseShare(local, InputTargets(local), SessionOf, AddLog,
                    machine => Dispatcher.BeginInvoke(() =>
                    {
                        _inputRemote = machine;
                        UpdateAgentStatus();
                    }))
                {
                    Emergency = () => Dispatcher.BeginInvoke(Emergency),
                };
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
        var local = CoordinatorDisplays();
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
