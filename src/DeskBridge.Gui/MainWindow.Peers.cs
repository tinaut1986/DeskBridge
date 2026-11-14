using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Gui.ViewModels;

namespace DeskBridge.Gui;

/// <summary>
/// Machines finding each other: on the coordinator, a machine that connects is added to Machines (with
/// its real address and displays) if it was not there, and every connected machine gets the
/// coordinator's configuration, now and whenever it is saved. Nothing has to be added by hand.
/// </summary>
public partial class MainWindow
{
    /// <summary>Sessions already welcomed (a reconnection is a new session and is welcomed again).</summary>
    private readonly HashSet<ControlSession> _welcomed = [];

    private bool IsCoordinatorHere() =>
        _vm.Model.Machines.FirstOrDefault(m => m.HasInput)?.Id.Trim() == _vm.LocalMachine.Trim();

    /// <summary>On the UI thread, whenever a machine connects or disconnects.</summary>
    private void OnPeersChanged()
    {
        UpdateAgentStatus();
        if (_agent is null || !IsCoordinatorHere())
            return;

        _welcomed.RemoveWhere(s => !s.IsOpen);
        foreach (var id in _agent.Directory.Connected)
        {
            if (id == _vm.LocalMachine.Trim() || _agent.Directory.Find(id) is not { } session || !_welcomed.Add(session))
                continue;
            _ = WelcomeAsync(id, session);
        }
    }

    private async Task WelcomeAsync(string id, ControlSession session)
    {
        var machine = _vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == id);
        if (machine is null
            && _vm.Model.Machines.FirstOrDefault(m => !m.HasInput && m.Address.Trim() == session.RemoteHost
                                                      && _agent?.Directory.Find(m.Id.Trim()) is null) is { } sameAddress)
        {
            // A machine already known at this address now uses another id (older versions named it "laptop";
            // now it is its computer name): rename it, so its profiles follow, instead of adding a duplicate.
            AddLog(L.T("{old} is now identified as {machine}.", ("old", sameAddress.Id), ("machine", id)));
            sameAddress.Id = id;
            machine = sameAddress;
        }
        if (machine is null)
        {
            machine = new MachineVm { Id = id, Name = id, Address = session.RemoteHost };
            _vm.Model.Machines.Add(machine);
            AddLog(L.T("{machine} connected and was added to Machines.", ("machine", id)));
        }
        else if (machine.Address.Trim() != session.RemoteHost)
        {
            machine.Address = session.RemoteHost; // where it really is
        }

        try
        {
            if (await session.RequestAsync(new GetDisplaysMessage()) is DisplaysMessage displays && displays.Displays.Count > 0)
                MergeDisplays(machine, displays.Displays);
        }
        catch (ControlException ex)
        {
            AddLog(ex.Message);
        }

        await SaveAndSyncAsync();
    }

    /// <summary>Saves the configuration (if valid) and sends it to every connected machine.</summary>
    private async Task SaveAndSyncAsync()
    {
        var config = CurrentConfig(showErrors: false);
        if (config is null || _agent is null || !IsCoordinatorHere())
            return;
        if (_options.SnapshotDir is null)
            ConfigLoader.Save(_options.ConfigPath, config);
        await Coordinator.SyncConfigAsync(config, _agent.Directory);
    }
}
