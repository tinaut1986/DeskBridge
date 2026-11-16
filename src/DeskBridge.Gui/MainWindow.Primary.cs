using System.IO;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Platform.Windows;

namespace DeskBridge.Gui;

/// <summary>
/// The coordinator's primary display (Start menu, taskbar, the Windows key, new windows). While a profile shows
/// another machine on it, another display of this PC is made the primary one (the profile's choice, or the
/// nearest free one), and the original comes back when it is free again or DeskBridge stops showing other
/// machines. The original is kept in a file, so it is known after a restart.
/// <para>
/// Making another display primary moves Windows' origin: every display shifts by the same amount. The layout
/// keeps the coordinates of the original arrangement (the desk, and what Detect saves), and
/// <see cref="DeskOffset"/> converts between both.
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
    /// the position of the original primary display (desk coordinates + offset = Windows' coordinates now).
    /// </summary>
    private static (int X, int Y) DeskOffset()
    {
        if (OriginalPrimary() is not { } original)
            return (0, 0);
        var display = WindowsDisplayDetector.DetectDetailed().FirstOrDefault(d => d.Config.Id == original);
        return display is null ? (0, 0) : (display.Config.X, display.Config.Y);
    }

    /// <summary>After a profile is applied here (agent thread): the primary display it needs, before its streams start.</summary>
    private void OnPlanAppliedForPrimary(LayoutPlan plan) => Dispatcher.Invoke(() =>
    {
        if (_options.SnapshotDir is not null || !IsCoordinatorHere())
            return;
        string here = _vm.LocalMachine.Trim();
        var lent = plan.StreamsTo(here).Select(s => s.SinkDisplay).ToHashSet();
        var displays = WindowsDisplayDetector.DetectDetailed();
        if (displays.Count == 0)
            return;
        string? current = displays.FirstOrDefault(d => d.IsPrimary)?.Config.Id;
        string original = OriginalPrimary() ?? current ?? displays[0].Config.Id;
        var rects = displays.ToDictionary(d => d.Config.Id, d => new DisplayRect(d.Config.X, d.Config.Y, d.Config.Width, d.Config.Height));
        string wanted = PrimaryChoice.Decide(rects, original, lent, plan.PrimaryDisplay);
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

    // ------------------------------------------------------------------ the profile's choice (Layout)

    private bool _fillingPrimary;

    /// <summary>"Automatic" and each display of the coordinator, with the selected profile's choice selected.</summary>
    private void UpdatePrimaryBox()
    {
        var self = _vm.Model.Machines.FirstOrDefault(m => m.HasInput) ?? _vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == _vm.LocalMachine.Trim());
        var ids = self?.Displays.Select(d => d.Id.Trim()).ToList() ?? [];
        var wanted = new List<(string? Id, string Label)> { (null, L.T("Automatic")) };
        wanted.AddRange(ids.Select(id => ((string?)id, L.T("Display {id}", ("id", id)))));
        _fillingPrimary = true;
        try
        {
            var shown = PrimaryBox.Items.OfType<System.Windows.Controls.ComboBoxItem>().Select(i => ((string?)i.Tag, (string)i.Content)).ToList();
            if (!shown.SequenceEqual(wanted))
            {
                PrimaryBox.Items.Clear();
                foreach (var (id, label) in wanted)
                    PrimaryBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = id });
            }
            string? chosen = _vm.SelectedProfile?.PrimaryDisplay;
            PrimaryBox.SelectedItem = PrimaryBox.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == chosen)
                                      ?? PrimaryBox.Items[0];
            PrimaryBox.IsEnabled = _vm.SelectedProfile is not null && !IsClientHere();
        }
        finally
        {
            _fillingPrimary = false;
        }
    }

    private void PrimaryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_fillingPrimary && _vm.SelectedProfile is { } profile && PrimaryBox.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            profile.PrimaryDisplay = item.Tag as string;
    }
}
