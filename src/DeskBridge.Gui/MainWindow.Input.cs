using System.Windows.Threading;
using DeskBridge.Core.Control;
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

    /// <summary>Connected machines that can get the cursor, with the side of this PC where each one is.</summary>
    private IReadOnlyList<(string Machine, PlacementSide Side)> InputTargets()
    {
        if (_agent is null)
            return [];
        var connected = _agent.Directory.Connected.ToHashSet();
        return _vm.Model.Machines
            .Where(m => m.Id.Trim() != _vm.LocalMachine.Trim() && connected.Contains(m.Id.Trim()))
            .Select(m => (m.Id.Trim(), m.InputSide))
            .ToList();
    }

    private InputReceiver Receiver() => _receiver ??= new InputReceiver(
        () => _settings.AcceptInput,
        LocalDisplays,
        SessionOf,
        from => Dispatcher.BeginInvoke(() =>
        {
            _controlledBy = from;
            UpdateAgentStatus();
        }));

    /// <summary>Called on the agent's connection thread for every keyboard and mouse sharing message.</summary>
    private Task HandleInputAsync(string from, ControlMessage message)
    {
        if (message is InputLeaveMessage leave)
        {
            _share?.OnLeave(from, leave.Fraction);
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
                _share = new KeyboardMouseShare(LocalDisplays(), InputTargets(), SessionOf, AddLog,
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
            _inputTimer.Tick += (_, _) => _share?.Update(LocalDisplays(), InputTargets());
            _inputTimer.Start();
        }
        else
        {
            _share.Update(LocalDisplays(), InputTargets());
        }
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
