using System.Windows;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Gui;

/// <summary>
/// Turning things off without changing the configuration, and what happens when a machine goes away:
/// <list type="bullet">
/// <item>Pause screen sharing (coordinator): the applied profile stays chosen, but every machine shows its own
/// displays again (no video, no virtual displays); keyboard, mouse, clipboard and sound go on.</item>
/// <item>Independent machines (any PC): this PC stops talking to the others, and each one works on its own.</item>
/// <item>A coordinator that goes away (switched off, restarted, off the network): after a short wait, the
/// machine lets go of its part of the profile, so no virtual display is left that nobody shows. On the
/// coordinator, a machine that goes away stops being shown at once.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    /// <summary>How long a client waits for its coordinator to come back before working on its own.</summary>
    private static readonly TimeSpan CoordinatorGrace = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _coordinatorGone;

    /// <summary>On the UI thread, whenever a machine connects or disconnects.</summary>
    private void OnPeersChangedForPresence()
    {
        DropStreamsOfGoneMachines();
        WatchCoordinator();
    }

    /// <summary>Another machine is connected to this one right now (the borders and the rest only mean something then).</summary>
    private bool OthersConnected() =>
        _agent?.Directory.Connected.Any(m => m != _vm.LocalMachine.Trim()) == true;

    /// <summary>
    /// A display showing a machine that is not connected any more shows this PC again (its stream is asked for
    /// again when the machine is back).
    /// </summary>
    private void DropStreamsOfGoneMachines()
    {
        if (_agent is not { } agent)
            return;
        foreach (var receiver in _videoReceivers.Values.ToArray())
        {
            var machine = receiver.RemoteMachine;
            if (machine == _vm.LocalMachine.Trim() || agent.Directory.Find(machine) is not null)
                continue;
            AddLog(L.T("{machine} is not connected any more: its screen is no longer shown here.", ("machine", MachineName(machine))));
            _ = receiver.DisposeAsync().AsTask(); // its completion cleans up (and asks for it again later)
        }
    }

    /// <summary>On a client: the coordinator whose profile is in place here went away; after a while, let it go.</summary>
    private void WatchCoordinator()
    {
        if (_agent is not { } agent || IsCoordinatorHere())
            return;
        string? coordinator = agent.AppliedBy;
        if (coordinator is null || coordinator == agent.Machine || agent.Directory.Find(coordinator) is not null)
        {
            _coordinatorGone?.Cancel();
            _coordinatorGone = null;
            return;
        }
        if (_coordinatorGone is not null)
            return; // already waiting
        var gone = _coordinatorGone = new CancellationTokenSource();
        _ = Task.Delay(CoordinatorGrace, gone.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.InvokeAsync(async () =>
                {
                    if (gone.IsCancellationRequested || _agent != agent || agent.Directory.Find(coordinator) is not null)
                        return;
                    _coordinatorGone = null;
                    await ReleaseHereAsync(L.T("{machine} is not connected any more: this PC works on its own (without virtual displays) until it is back.",
                        ("machine", MachineName(coordinator))));
                });
        }, TaskScheduler.Default);
    }

    /// <summary>How long a client that has just started waits for its coordinator before removing left-overs.</summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A client that starts without finding its coordinator (switched off, another network, no network) may still
    /// have virtual displays from last time (the driver remembers how many it had): nobody shows them, so the
    /// cursor and windows would get lost there. After a while alone, they go (they come back with the profile).
    /// </summary>
    private void ReleaseLeftOversIfAlone(AgentHost agent)
    {
        _ = Task.Delay(StartGrace).ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
        {
            if (_agent != agent || agent.Reverse is not { } reverse || reverse.ConnectedTo is not null || agent.AppliedBy is not null)
                return;
            bool virtualHere = await Task.Run(() =>
                Platform.Windows.WindowsDisplayDetector.DetectAll().Count > Platform.Windows.WindowsDisplayDetector.DetectDetailed().Count);
            if (!virtualHere)
                return;
            await agent.ReleaseAsync(L.T("{address} cannot be reached: this PC works on its own (without virtual displays) until it connects.",
                ("address", reverse.Target)), evenIfNothingApplied: true);
            RestorePrimary();
            UpdateAgentStatus();
        }), TaskScheduler.Default);
    }

    /// <summary>This PC leaves its part of the profile: no streams, no virtual displays, its own primary display.</summary>
    private async Task ReleaseHereAsync(string reason)
    {
        if (_agent is { } agent)
            await agent.ReleaseAsync(reason);
        await StopVideoStreamsAsync();
        _lentDisplays.Clear();
        _mirroredDisplays.Clear();
        RefreshInputTargets();
        RestorePrimary();
        _activeProfile = null;
        UpdateAgentStatus();
        UpdateAppliedIndicator();
        ScheduleRedraw();
    }

    // ------------------------------------------------------------------ pause screen sharing (coordinator)

    /// <summary>The applied configuration with the applied profile showing nothing: what a pause applies.</summary>
    private static DeskBridgeConfig PausedConfig(DeskBridgeConfig config, string profile) => config with
    {
        Profiles = config.Profiles.Select(p => p.Name != profile ? p : p with
        {
            Assignments = [],
            Placement = null,
            // A primary display that is a virtual one ("shows:N") is not there while paused.
            PrimaryDisplays = p.PrimaryDisplays?
                .Where(kv => !kv.Value.StartsWith(ProfileConfig.ShowsPrefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        }).ToList(),
    };

    /// <summary>What the machines are given for the applied profile: paused or not.</summary>
    private DeskBridgeConfig ConfigToApply(DeskBridgeConfig applied, string profile) =>
        _settings.ScreensPaused ? PausedConfig(applied, profile) : applied;

    private async Task SetScreensPausedAsync(bool paused)
    {
        if (!IsCoordinatorHere() || _settings.Independent || paused == _settings.ScreensPaused)
            return;
        SaveUserSettings(_settings with { ScreensPaused = paused });
        UpdatePauseUi();
        if (_applied is not { } applied || CurrentLocalSettings(showErrors: false) is not { } local)
            return;
        try
        {
            if (paused)
                await StopProfileStreamsAsync();
            var results = await Coordinator.SwitchProfileAsync(ConfigToApply(applied.Config, applied.Profile), local, applied.Profile, _agent?.Directory);
            foreach (var r in results.Where(r => !r.Ok))
                AddLog($"{r.Machine}: {L.T("ERROR")} {r.Message}");
            if (paused)
            {
                RestorePrimary();
                _vm.Status = L.T("Screen sharing paused: every machine shows its own displays. Keyboard, mouse, clipboard and sound keep working.");
            }
            else
            {
                await StartProfileStreamsAsync(LayoutPlanner.Plan(applied.Config, applied.Profile));
                _vm.Status = L.T("Screen sharing resumed: profile {profile}.", ("profile", applied.Profile));
            }
            AddLog(_vm.Status);
            Notify(_vm.Status);
        }
        catch (Exception ex) when (ex is ControlException or ConfigException)
        {
            AddLog(ex.Message);
        }
        UpdateAppliedIndicator();
    }

    // ------------------------------------------------------------------ independent machines

    private async Task SetIndependentAsync(bool independent)
    {
        if (independent == _settings.Independent)
            return;
        if (independent)
        {
            // The others let go of this PC's screens first (a client lets go by itself when it loses the coordinator,
            // but only after a wait).
            if (IsCoordinatorHere() && _applied is { } applied && CurrentLocalSettings(showErrors: false) is { } local && _agent is not null)
            {
                await StopProfileStreamsAsync();
                try
                {
                    await Coordinator.SwitchProfileAsync(PausedConfig(applied.Config, applied.Profile), local, applied.Profile, _agent.Directory);
                }
                catch (Exception ex) when (ex is ControlException or ConfigException)
                {
                    AddLog(ex.Message);
                }
            }
            SaveUserSettings(_settings with { Independent = true });
            await ReleaseHereAsync(L.T("Independent machines: this PC no longer connects to the others."));
            await RestartAgentAsync(CurrentLocalSettings(showErrors: false)); // stops it, and does not start it again
            _vm.Status = L.T("Independent machines: this PC no longer connects to the others.");
        }
        else
        {
            SaveUserSettings(_settings with { Independent = false });
            await RestartAgentAsync(CurrentLocalSettings(showErrors: false));
            _vm.Status = L.T("Connected again: the applied profile comes back when the machines find each other.");
            // As at start: the coordinator puts its profile back (the others get it as they connect).
            await Task.Delay(1500);
            await RestoreAppliedAsync();
        }
        AddLog(_vm.Status);
        Notify(_vm.Status);
        UpdatePauseUi();
        UpdateAppliedIndicator();
    }

    // ------------------------------------------------------------------ buttons and menus

    private void UpdatePauseUi()
    {
        bool coordinator = IsCoordinatorHere();
        PauseScreensButton.Visibility = coordinator ? Visibility.Visible : Visibility.Collapsed;
        PauseScreensButton.IsEnabled = !_settings.Independent;
        PauseScreensButton.Content = _settings.ScreensPaused ? L.T("Resume screen sharing") : L.T("Pause screen sharing");
        IndependentButton.Content = _settings.Independent ? L.T("Connect with the other machines") : L.T("Independent machines");
        IndependentButton.ToolTip = _settings.Independent
            ? null
            : L.T("Stops everything with the other machines (screens, keyboard and mouse, clipboard, sound, files) until you connect again. Nothing in the configuration changes.");
        // (The tray menu is built each time it opens.)
    }

    private void PauseScreens_Click(object sender, RoutedEventArgs e) => _ = SetScreensPausedAsync(!_settings.ScreensPaused);

    private void Independent_Click(object sender, RoutedEventArgs e) => _ = SetIndependentAsync(!_settings.Independent);
}
