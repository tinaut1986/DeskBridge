using System.IO;
using System.Text.Json;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Gui;

/// <summary>
/// The profile in place, kept apart from the configuration being edited: shown in the profile bar (with a
/// warning when the edits change it but are not applied yet), remembered across restarts, and applied again
/// to every machine that (re)connects, so the layout comes back by itself after a reboot or an update.
/// </summary>
public partial class MainWindow
{
    private sealed record AppliedState(string Profile, DeskBridgeConfig Config);

    private AppliedState? _applied;

    private static string AppliedPath => Path.Combine(AppPaths.Directory, "applied-profile.json");

    /// <summary>A profile was applied from here: remember it exactly as applied.</summary>
    private void RememberApplied(string profile, DeskBridgeConfig config)
    {
        _applied = new AppliedState(profile, config);
        if (_options.SnapshotDir is null)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Directory);
                File.WriteAllText(AppliedPath, JsonSerializer.Serialize(_applied, ConfigLoader.JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddLog(ex.Message);
            }
        }
        UpdateAppliedIndicator();
    }

    /// <summary>Nothing is in place any more (the streams were stopped by hand).</summary>
    private void ForgetApplied()
    {
        _applied = null;
        try
        {
            File.Delete(AppliedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        UpdateAppliedIndicator();
    }

    /// <summary>
    /// At start, on the coordinator: the profile applied last time is put back in place on this PC, and on
    /// every other machine as it connects (with its streams).
    /// </summary>
    private async Task RestoreAppliedAsync()
    {
        if (_options.SnapshotDir is not null || !IsCoordinatorHere() || !File.Exists(AppliedPath))
            return;
        AppliedState? state;
        try
        {
            state = JsonSerializer.Deserialize<AppliedState>(await File.ReadAllTextAsync(AppliedPath), ConfigLoader.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return;
        }
        if (state is null || state.Config.Profiles.All(p => p.Name != state.Profile))
            return;

        LayoutPlan plan;
        try
        {
            plan = LayoutPlanner.Plan(state.Config, state.Profile);
        }
        catch (ConfigException)
        {
            return;
        }
        _applied = state;
        _streamPlan = plan;
        UpdateAppliedIndicator();
        AddLog(L.T("Putting profile {profile} back in place, as it was before closing.", ("profile", state.Profile)));
        await ApplyToMachineAsync(_vm.LocalMachine.Trim());
        foreach (var id in _agent?.Directory.Connected.Where(m => m != _vm.LocalMachine.Trim()) ?? [])
            await ReapplyToAsync(id);
    }

    /// <summary>A machine (re)connected: give it the applied profile again, then start its streams.</summary>
    private async Task ReapplyToAsync(string machine)
    {
        // Connected before the applied profile was read back at start: nothing to give it yet, the restore does
        // it (and must not find it already done).
        if (_applied is null)
            return;
        // At start the restore and the machine's connection can ask at the same moment: once is enough.
        if (_lastReapply.TryGetValue(machine, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(10))
            return;
        _lastReapply[machine] = DateTime.UtcNow;
        await ApplyToMachineAsync(machine);
        ResumeProfileStreams(machine);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastReapply = new();

    private async Task ApplyToMachineAsync(string machine)
    {
        if (_applied is not { } applied || CurrentLocalSettings(showErrors: false) is not { } local)
            return;
        if (applied.Config.Machines.All(m => m.Id != machine))
            return;
        try
        {
            await using var link = await Peers.ConnectAsync(applied.Config, local, machine, _agent?.Directory);
            if (await link.RequestAsync(new ApplyProfileMessage(applied.Config, applied.Profile)) is ErrorMessage error)
                AddLog($"{machine}: {L.T("ERROR")} {error.Message}");
        }
        catch (ControlException ex)
        {
            AddLog($"{machine}: {L.T("ERROR")} {ex.Message}");
        }
    }

    /// <summary>"Applied: work", in a warning colour when the edits would change it but are not applied yet.</summary>
    private void UpdateAppliedIndicator()
    {
        UpdateAppliedText();
        // Home shows the same, larger.
        HomeAppliedText.Text = AppliedText.Text;
        HomeAppliedText.Foreground = AppliedText.Foreground == MutedBrush ? ThemeManager.Resource("NormalText") : AppliedText.Foreground;
    }

    private void UpdateAppliedText()
    {
        if (IsClientHere())
        {
            AppliedText.Text = _activeProfile is { } active ? L.T("Applied: {profile}", ("profile", active)) : "";
            AppliedText.Foreground = MutedBrush;
            AppliedText.ToolTip = null;
            return;
        }
        if (_applied is not { } applied)
        {
            AppliedText.Text = L.T("No profile applied");
            AppliedText.Foreground = MutedBrush;
            AppliedText.ToolTip = null;
            return;
        }

        bool changed;
        var errors = new List<string>();
        var current = _vm.Model.ToConfig(errors);
        try
        {
            changed = errors.Count > 0
                      || JsonSerializer.Serialize(LayoutPlanner.Plan(current, applied.Profile), ConfigLoader.JsonOptions)
                         != JsonSerializer.Serialize(LayoutPlanner.Plan(applied.Config, applied.Profile), ConfigLoader.JsonOptions);
        }
        catch (ConfigException)
        {
            changed = true; // renamed, deleted or not valid any more
        }
        AppliedText.Text = changed
            ? L.T("Applied: {profile} (changes not applied yet)", ("profile", applied.Profile))
            : L.T("Applied: {profile}", ("profile", applied.Profile));
        AppliedText.Foreground = changed ? ThemeManager.Resource("WarningText") : MutedBrush;
        AppliedText.ToolTip = changed ? L.T("The machines keep the profile as it was applied until you press Apply profile.") : null;
    }

    private static System.Windows.Media.Brush MutedBrush => ThemeManager.Resource("MutedText");
}
