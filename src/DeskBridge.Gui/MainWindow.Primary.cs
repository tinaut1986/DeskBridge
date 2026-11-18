using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Gui.ViewModels;
using DeskBridge.Platform.Windows;

namespace DeskBridge.Gui;

/// <summary>
/// Windows' primary display of each machine (Start menu, taskbar, the Windows key, new windows), chosen per
/// profile in the Layout map (right click). On the coordinator, while a profile shows another machine on its
/// primary display, the nearest free one becomes primary unless the profile chose one. On another machine, the
/// chosen one can be its virtual display shown on the coordinator, so its Start menu opens there. The original
/// primary display comes back when the profile no longer asks for another one or DeskBridge stops showing other
/// machines; it is kept in a file, so it is known after a restart.
/// <para>
/// Making another display primary moves Windows' origin: every display shifts by the same amount. Layouts keep
/// the coordinates of the original arrangement, and <see cref="DeskOffset"/> converts between both.
/// </para>
/// </summary>
public partial class MainWindow
{
    private static string OriginalPrimaryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBridge", "original-primary.txt");

    private static string? OriginalPrimary()
    {
        try
        {
            return File.Exists(OriginalPrimaryPath) ? File.ReadAllText(OriginalPrimaryPath).Trim() is { Length: > 0 } id ? id : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the original arrangement's (0,0) is now: (0,0) unless DeskBridge made another display primary, then
    /// the position of the original primary display (layout coordinates + offset = Windows' coordinates now).
    /// </summary>
    private static (int X, int Y) DeskOffset()
    {
        if (OriginalPrimary() is not { } original)
            return (0, 0);
        var display = WindowsDisplayDetector.DetectDetailed(includeVirtual: true).FirstOrDefault(d => d.Config.Id == original);
        return display is null ? (0, 0) : (display.Config.X, display.Config.Y);
    }

    /// <summary>
    /// Before a profile is applied here (agent thread): a machine that places virtual displays goes back to its
    /// original arrangement first, so they are placed where the plan says (its coordinates are the original ones).
    /// A virtual display that is the primary one gives that back first too, whatever the plan: it may be about to
    /// go away, and Windows does not take its primary display off the desktop.
    /// </summary>
    private void OnPlanApplyingForPrimary(LayoutPlan plan) => Dispatcher.Invoke(() =>
    {
        if (_options.SnapshotDir is not null)
            return;
        if ((plan.VirtualDisplaysOf(_vm.LocalMachine.Trim()).Any() && OriginalPrimary() is not null) || VirtualIsPrimary())
            PrimaryOffVirtual();
    });

    /// <summary>The primary display is a virtual one (the Virtual Display Driver's).</summary>
    private static bool VirtualIsPrimary()
    {
        string? primary = WindowsDisplayDetector.PrimaryId();
        return primary is not null && WindowsDisplayDetector.DetectDetailed().All(d => d.Config.Id != primary);
    }

    /// <summary>
    /// Before this PC's virtual displays go away (another profile, a pause, independent machines, its coordinator
    /// gone): the primary display goes back to one of its own. Windows does not take its primary display off the
    /// desktop, so a virtual one that is primary would stay, with the taskbar, new windows and the cursor on a
    /// display that nobody shows any more. The original primary display, or the nearest physical one to (0,0).
    /// </summary>
    private void PrimaryOffVirtual()
    {
        RestorePrimary();
        if (!VirtualIsPrimary())
            return;
        var nearest = WindowsDisplayDetector.DetectDetailed()
            .OrderBy(d => (long)d.Config.X * d.Config.X + (long)d.Config.Y * d.Config.Y)
            .FirstOrDefault();
        if (nearest is null)
            return;
        AddLog($"[displays] the primary display is a virtual one: display {nearest.Config.Id} takes over before virtual displays go");
        if (WindowsDisplayDetector.MakePrimary(nearest.Config.Id, AddLog))
            ForgetOriginalPrimary();
    }

    /// <summary>After a profile is applied here (agent thread): the primary display it needs, before its streams start.</summary>
    private void OnPlanAppliedForPrimary(LayoutPlan plan) => Dispatcher.Invoke(() =>
    {
        if (_options.SnapshotDir is not null)
            return;
        string here = _vm.LocalMachine.Trim();
        var displays = WindowsDisplayDetector.DetectDetailed(includeVirtual: true);
        if (displays.Count == 0)
            return;
        string? current = displays.FirstOrDefault(d => d.IsPrimary)?.Config.Id;
        string original = OriginalPrimary() ?? current ?? displays[0].Config.Id;

        // The profile's choice: one of this PC's displays, or its virtual display (found where it was placed).
        string? chosen = plan.PrimaryDisplayOf(here);
        if (chosen is not null && chosen.Contains('/'))
            chosen = Platform.Windows.VirtualDisplays.MttVirtualDisplays.Find(chosen) is { } at
                ? displays.FirstOrDefault(d => d.Config.X == at.X && d.Config.Y == at.Y)?.Config.Id
                : null;
        string wanted;
        if (here == plan.Coordinator)
        {
            var lent = plan.StreamsTo(here).Select(s => s.SinkDisplay).ToHashSet();
            var physical = WindowsDisplayDetector.DetectDetailed();
            var rects = physical.ToDictionary(d => d.Config.Id, d => new DisplayRect(d.Config.X, d.Config.Y, d.Config.Width, d.Config.Height));
            wanted = PrimaryChoice.Decide(rects, original, lent, chosen);
        }
        else if (chosen is not null && displays.Any(d => d.Config.Id == chosen))
        {
            wanted = chosen;
        }
        else
        {
            // Nothing chosen: like on the coordinator, a primary display that shows the coordinator's screen (covered
            // by its video) gives way to the nearest one that does not, e.g. this PC's virtual display shown on one of
            // the coordinator's monitors; otherwise the taskbar and new windows would land under that video.
            var covered = plan.StreamsTo(here).Select(s => s.SinkDisplay).ToHashSet();
            var rects = displays.ToDictionary(d => d.Config.Id, d => new DisplayRect(d.Config.X, d.Config.Y, d.Config.Width, d.Config.Height));
            wanted = PrimaryChoice.Decide(rects, original, covered, null);
        }

        if (plan.PrimaryDisplayOf(here) is { } asked)
            AddLog($"[displays] primary display: the profile asks for {asked} (display {chosen ?? "not found"}); now {current}, original {original}: {wanted}");
        if (wanted == current)
        {
            if (wanted == original)
                ForgetOriginalPrimary();
            return;
        }
        if (wanted != original)
            RememberOriginalPrimary(original);
        if (WindowsDisplayDetector.MakePrimary(wanted, AddLog) && wanted == original)
            ForgetOriginalPrimary();
    });

    /// <summary>DeskBridge stops showing other machines here (closing, the tray's stop): the original primary display comes back.</summary>
    private void RestorePrimary()
    {
        if (OriginalPrimary() is not { } original)
            return;
        if (WindowsDisplayDetector.MakePrimary(original, AddLog))
            ForgetOriginalPrimary();
    }

    private static void RememberOriginalPrimary(string id)
    {
        try
        {
            if (File.Exists(OriginalPrimaryPath))
                return; // the first one is the user's
            Directory.CreateDirectory(Path.GetDirectoryName(OriginalPrimaryPath)!);
            File.WriteAllText(OriginalPrimaryPath, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ForgetOriginalPrimary()
    {
        try
        {
            File.Delete(OriginalPrimaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ the Layout map: marks and the right-click menu

    /// <summary>
    /// The machines whose primary display this tile is in the selected profile: its own machine's (the choice,
    /// or what it would be: the coordinator's moves off a display that shows another machine), and the machine
    /// it shows when that machine's chosen primary is its virtual display here.
    /// </summary>
    private List<MachineVm> PrimaryMarks(MachineVm machine, DisplayVm display, MachineVm shows, MachineVm coordinator)
    {
        var marks = new List<MachineVm>();
        if (_vm.SelectedProfile is not { } profile)
            return marks;
        string id = display.Id.Trim();
        if (EffectivePrimary(profile, machine, coordinator) == id)
            marks.Add(machine);
        if (shows != machine && machine == coordinator
            && (profile.PrimaryDisplays.GetValueOrDefault(shows.Id.Trim()) == ProfileConfig.ShowsPrefix + id
                || GivesWayHere(profile, shows, coordinator, id)))
            marks.Add(shows);
        return marks;
    }

    /// <summary>
    /// Nothing chosen for <paramref name="other"/>, and its own primary display shows another machine: it gives way
    /// to its virtual display (see OnPlanAppliedForPrimary), the one shown on the coordinator's first display that
    /// shows it, <paramref name="display"/> here.
    /// </summary>
    private static bool GivesWayHere(ProfileVm profile, MachineVm other, MachineVm coordinator, string display)
    {
        if (profile.PrimaryDisplays.ContainsKey(other.Id.Trim()) || OwnPrimaryLent(profile, other) is null)
            return false;
        var first = profile.Assignments
            .Where(a => a.Owner == coordinator && a.UsedBy == other)
            .Select(a => a.Display.Id.Trim())
            .FirstOrDefault();
        return first == display;
    }

    /// <summary>Its own primary display (the one at 0,0) when the profile lends it to another machine; otherwise null.</summary>
    private static string? OwnPrimaryLent(ProfileVm profile, MachineVm machine)
    {
        var own = machine.Displays.FirstOrDefault(d => d.X == 0 && d.Y == 0) ?? machine.Displays.FirstOrDefault();
        if (own is null)
            return null;
        bool lent = profile.Assignments.Any(a => a.Owner == machine && a.Display == own && a.UsedBy != machine);
        return lent ? own.Id.Trim() : null;
    }

    /// <summary>The display id that is (or will be) primary on that machine with this profile, among its own.</summary>
    private static string? EffectivePrimary(ProfileVm profile, MachineVm machine, MachineVm coordinator)
    {
        var own = machine.Displays.Select(d => (Id: d.Id.Trim(), Rect: new DisplayRect(d.X, d.Y, d.Width, d.Height))).ToList();
        if (own.Count == 0)
            return null;
        // Windows' primary display is the one at (0,0) of the arrangement the layout keeps.
        string original = own.FirstOrDefault(d => d.Rect.X == 0 && d.Rect.Y == 0).Id ?? own[0].Id;
        string? chosen = profile.PrimaryDisplays.GetValueOrDefault(machine.Id.Trim());
        if (chosen is not null && chosen.StartsWith(ProfileConfig.ShowsPrefix, StringComparison.Ordinal))
            return null; // its virtual display, marked on the coordinator's display that shows it
        if (machine != coordinator)
        {
            if (chosen is not null && own.Any(d => d.Id == chosen))
                return chosen;
            // Its primary shows another machine: it gives way to its virtual display (marked where that is shown).
            return OwnPrimaryLent(profile, machine) is not null && profile.Assignments.Any(a => a.Owner == coordinator && a.UsedBy == machine)
                ? null
                : original;
        }
        var lent = profile.Assignments.Where(a => a.Owner == coordinator && a.UsedBy != coordinator).Select(a => a.Display.Id.Trim()).ToHashSet();
        return PrimaryChoice.Decide(own.ToDictionary(d => d.Id, d => d.Rect), original, lent, chosen);
    }

    private ContextMenu? PrimaryMenu(MachineVm machine, DisplayVm display, MachineVm shows, MachineVm coordinator)
    {
        if (_vm.SelectedProfile is not { } profile)
            return null;
        string id = display.Id.Trim();
        var menu = new ContextMenu();
        bool editable = !IsClientHere();

        void Add(MachineVm of, string value, string label)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = profile.PrimaryDisplays.GetValueOrDefault(of.Id.Trim()) == value,
                IsEnabled = editable,
            };
            item.Click += (_, _) =>
            {
                profile.SetPrimary(of.Id.Trim(), item.IsChecked ? value : null);
                ScheduleRedraw();
            };
            menu.Items.Add(item);
        }

        void Automatic(MachineVm of)
        {
            var item = new MenuItem
            {
                Header = L.T("Primary display of {machine}: automatic", ("machine", of)),
                IsCheckable = true,
                IsChecked = !profile.PrimaryDisplays.ContainsKey(of.Id.Trim()),
                IsEnabled = editable,
            };
            item.Click += (_, _) =>
            {
                profile.SetPrimary(of.Id.Trim(), null);
                ScheduleRedraw();
            };
            menu.Items.Add(item);
        }

        Add(machine, id, L.T("Make it the primary display of {machine}", ("machine", machine)));
        if (shows != machine && machine == coordinator)
            Add(shows, ProfileConfig.ShowsPrefix + id, L.T("Make what it shows the primary display of {machine}", ("machine", shows)));
        menu.Items.Add(new Separator());
        Automatic(machine);
        if (shows != machine && machine == coordinator)
            Automatic(shows);
        return menu;
    }

    /// <summary>
    /// This PC's displays as other machines see them (Detect on the coordinator): in the original arrangement,
    /// even while DeskBridge has made another display primary.
    /// </summary>
    private sealed class OriginalArrangement(Core.Abstractions.IDisplayDetector detector) : Core.Abstractions.IDisplayDetector
    {
        public IReadOnlyList<DisplayConfig> DetectDisplays()
        {
            var (ox, oy) = DeskOffset();
            return detector.DetectDisplays().Select(d => d with { X = d.X - ox, Y = d.Y - oy }).ToList();
        }
    }
}
