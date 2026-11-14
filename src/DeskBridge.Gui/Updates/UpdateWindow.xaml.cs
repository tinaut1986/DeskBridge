using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui.Updates;

public partial class UpdateWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0xD1, 0xFA, 0xE5));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    private readonly Updater _updater;
    private string _lastButtons = "";
    private bool? _compact;

    public UpdateWindow(Updater updater)
    {
        _updater = updater;
        InitializeComponent();
        updater.Changed += Render;
        Closed += (_, _) => updater.Changed -= Render;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Buttons.Children.Count > 0)
                Close();
        };
        Render();
    }

    private enum BarKind { None, Busy, Progress, Done }

    private sealed record View(string Icon, string Title, string Sub, BarKind Bar, bool Notes, (string Label, Action Click, bool Primary)[] Buttons);

    /// <summary>What each phase shows: icon, title, subtitle, progress bar, notes, buttons.</summary>
    private View ViewOf(Updater u)
    {
        var installed = L.T("Installed version: {version}", ("version", AppInfo.DisplayVersion));
        var version = u.Version?.ToString() ?? "";
        return u.Phase switch
        {
            UpdatePhase.Checking => new("⬇", L.T("Checking for updates…"), installed, BarKind.Busy, false, [(L.T("Close"), Close, false)]),
            UpdatePhase.None => new("✓", L.T("DeskBridge is up to date"), installed, BarKind.None, false, [(L.T("Close"), Close, true)]),
            UpdatePhase.Disabled => new("ⓘ", L.T("Updates only work in the published app"), installed, BarKind.None, false, [(L.T("Close"), Close, true)]),
            UpdatePhase.Error => new("!", L.T("Could not update"), u.Error ?? "", BarKind.None, u.Version is not null,
                [(L.T("Close"), Close, false), (L.T("Try again"), u.Retry, true)]),
            UpdatePhase.Downloading => new("⬇", L.T("Downloading DeskBridge {version}", ("version", version)),
                L.T("You can keep working; you will be told when it is ready to install."), BarKind.Progress, true, [(L.T("Hide"), Close, false)]),
            UpdatePhase.Ready => new("✓", L.T("DeskBridge {version} is ready", ("version", version)),
                L.T("It will be installed when you restart the app."), BarKind.Done, true,
                [(L.T("Later"), Close, false), (L.T("Restart now"), u.RestartNow, true)]),
            UpdatePhase.Installing => new("⟳", L.T("Installing DeskBridge {version}…", ("version", version)),
                L.T("The app will close and reopen by itself in a few seconds."), BarKind.Busy, true, []),
            _ => new("⬇", L.T("Checking for updates…"), "", BarKind.Busy, false, [(L.T("Close"), Close, false)]),
        };
    }

    private void Render()
    {
        var u = _updater;
        var view = ViewOf(u);

        IconText.Text = view.Icon;
        IconBox.Background = view.Icon == "✓" ? Ok : view.Icon == "!" ? Bad : Neutral;
        Heading.Text = view.Title;
        Sub.Text = view.Sub;
        Sub.Visibility = view.Sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ProgressPanel.Visibility = view.Bar == BarKind.None ? Visibility.Collapsed : Visibility.Visible;
        var p = u.Progress;
        bool known = view.Bar == BarKind.Done || (view.Bar == BarKind.Progress && p?.Percent is > 0);
        ProgressBarControl.IsIndeterminate = view.Bar == BarKind.Busy || (view.Bar == BarKind.Progress && !known);
        ProgressBarControl.Value = view.Bar == BarKind.Done ? 100 : p?.Percent ?? 0;
        ProgressBarControl.Foreground = view.Bar == BarKind.Done ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) : new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        ProgressLeft.Text = view.Bar == BarKind.Progress && p is { Total: > 0 }
            ? L.T("{done} of {total} MB", ("done", Mb(p.Transferred)), ("total", Mb(p.Total)))
              + (p.BytesPerSecond > 0 ? " · " + L.T("{speed} MB/s", ("speed", Mb((long)p.BytesPerSecond))) : "")
            : "";
        ProgressRight.Text = known ? $"{Math.Floor(ProgressBarControl.Value)} %" : "";

        NotesHeading.Text = L.T("What's new");
        NotesPanel.Visibility = view.Notes ? Visibility.Visible : Visibility.Collapsed;
        if (view.Notes)
            RenderNotes(u);

        // Buttons are rebuilt only when they change, so keyboard focus survives progress updates.
        var key = string.Join("|", view.Buttons.Select(b => b.Label + b.Primary));
        if (key != _lastButtons)
        {
            _lastButtons = key;
            Buttons.Children.Clear();
            foreach (var (label, click, primary) in view.Buttons)
            {
                var button = new Button { Content = label, MinWidth = 90, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
                if (primary)
                {
                    button.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                    button.Foreground = Brushes.White;
                    button.Loaded += (_, _) => button.Focus();
                }
                button.Click += (_, _) => click();
                Buttons.Children.Add(button);
            }
        }

        Title = view.Title;
        FitSize(u.Compact);
    }

    private void FitSize(bool compact)
    {
        if (_compact == compact)
            return;
        _compact = compact;
        SizeToContent = compact ? SizeToContent.Height : SizeToContent.Manual;
        if (!compact)
            Height = Math.Max(Height, 540);
    }

    private void RenderNotes(Updater u)
    {
        NotesList.Children.Clear();
        void MutedLine(string text) => NotesList.Children.Add(new TextBlock { Text = text, Foreground = Muted, Margin = new Thickness(0, 10, 0, 10) });

        if (u.NotesFailed)
        {
            MutedLine(L.T("Could not load what's new."));
            return;
        }
        if (u.Notes is null)
        {
            MutedLine(L.T("Loading what's new…"));
            return;
        }
        if (u.Notes.Count == 0)
        {
            MutedLine(L.T("No notes."));
            return;
        }

        foreach (var n in u.Notes)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
            header.Children.Add(new TextBlock { Text = $"v{n.Version}", FontWeight = FontWeights.SemiBold });
            if (n.Beta)
                header.Children.Add(Chip(L.T("Beta"), Color.FromRgb(0xFE, 0xF3, 0xC7), null));
            if (!n.Translated)
                header.Children.Add(Chip("English", Color.FromRgb(0xE5, 0xE7, 0xEB), L.T("Not translated into your language yet")));
            NotesList.Children.Add(header);

            if (n.Lines.Count == 0)
            {
                MutedLine(L.T("No notes."));
                continue;
            }
            foreach (var line in n.Lines)
            {
                var row = new DockPanel { Margin = new Thickness(6, 2, 0, 2) };
                var bullet = new TextBlock { Text = "•", Width = 14 };
                DockPanel.SetDock(bullet, Dock.Left);
                row.Children.Add(bullet);
                row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
                NotesList.Children.Add(row);
            }
        }
    }

    private static Border Chip(string text, Color background, string? tooltip) => new()
    {
        Background = new SolidColorBrush(background),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 1),
        Margin = new Thickness(8, 0, 0, 0),
        ToolTip = tooltip,
        Child = new TextBlock { Text = text, FontSize = 11 },
    };

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
}
