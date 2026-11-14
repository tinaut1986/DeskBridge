using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Simulation;
using DeskBridge.Gui.ViewModels;
using DeskBridge.Windows;

namespace DeskBridge.Gui;

public partial class MainWindow : Window
{
    private static readonly Color[] MachineColors =
    [
        Color.FromRgb(0x25, 0x63, 0xEB), // blue
        Color.FromRgb(0xEA, 0x58, 0x0C), // orange
        Color.FromRgb(0x05, 0x96, 0x69), // green
        Color.FromRgb(0x7C, 0x3A, 0xED), // purple
        Color.FromRgb(0xDB, 0x27, 0x77), // pink
    ];

    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));

    private readonly StartupOptions _options;
    private readonly MainViewModel _vm = new();
    private AgentHost? _agent;
    private bool _redrawPending;

    public MainWindow(StartupOptions options)
    {
        _options = options;
        InitializeComponent();
        DataContext = _vm;

        ObservableObject.AnyChanged += ScheduleRedraw;
        LoadConfig();
        LoadLocalSettings();

        if (options.StartAgent)
            RestartAgent();
        if (options.SnapshotDir is { } dir)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            ShowActivated = false;
            ContentRendered += (_, _) => SaveSnapshots(dir);
        }
    }

    // ------------------------------------------------------------------ loading and saving

    private void LoadConfig()
    {
        DeskBridgeConfig config;
        try
        {
            config = File.Exists(_options.ConfigPath) ? ConfigLoader.Load(_options.ConfigPath) : CreateDefaultConfig();
            if (!File.Exists(_options.ConfigPath))
            {
                ConfigLoader.Save(_options.ConfigPath, config);
                AddLog($"Created {_options.ConfigPath} from this PC's displays.");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not read {_options.ConfigPath}:\n{ex.Message}\n\nStarting with a new configuration (the file is not overwritten until you save).",
                "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            config = CreateDefaultConfig();
        }

        _vm.Model = EditorModel.FromConfig(config);
        _vm.Model.Machines.CollectionChanged += (_, _) => _vm.RaiseMachineIds();
        _vm.SelectedProfile = _vm.Model.Profiles.FirstOrDefault(p => p.Name == _options.Profile) ?? _vm.Model.Profiles.FirstOrDefault();
        _vm.SelectedMachine = _vm.Model.Machines.FirstOrDefault();
        _vm.RaiseMachineIds();
    }

    private static DeskBridgeConfig CreateDefaultConfig()
    {
        var displays = new WindowsDisplayDetector().DetectDisplays();
        if (displays.Count == 0)
            displays = [new DisplayConfig("1", 1920, 1080)];

        var desktop = new MachineConfig("desktop", Environment.MachineName, LocalIPv4() ?? "127.0.0.1", displays, HasInput: true);
        var laptop = new MachineConfig("laptop", "Laptop", "192.168.1.20", [new DisplayConfig("1", 1920, 1080)]);

        var profiles = new List<ProfileConfig>
        {
            new("personal", []),
            new("four-screens", [new DisplayAssignment("laptop", "1", "desktop")]),
        };
        if (displays.Count >= 2)
        {
            // The laptop takes every desktop display except the primary-ish first one (at most two).
            var lent = displays.Skip(1).Take(2).Select(d => new DisplayAssignment("desktop", d.Id, "laptop"));
            profiles.Add(new("work", [.. lent, new DisplayAssignment("laptop", "1", "desktop")]));
        }
        return new DeskBridgeConfig([desktop, laptop], profiles);
    }

    private static string? LocalIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private void LoadLocalSettings()
    {
        LocalSettings local;
        try
        {
            local = LocalSettings.Load(_options.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            var coordinator = _vm.Model.Machines.FirstOrDefault(m => m.HasInput)?.Id ?? "desktop";
            local = new LocalSettings(coordinator, ControlAuth.NewKey(), _vm.Model.ControlPort);
            if (_options.SnapshotDir is null)
            {
                local.Save(_options.LocalPath);
                AddLog($"Created {_options.LocalPath} with a new shared key.");
            }
        }
        _vm.LocalMachine = local.Machine;
        _vm.LocalKey = local.SharedKey;
        _vm.LocalPort = local.ControlPort.ToString();
    }

    private LocalSettings? CurrentLocalSettings(bool showErrors)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(_vm.LocalMachine))
            error = "Pick which machine this PC is.";
        else if (!int.TryParse(_vm.LocalPort, out var port) || port is < 1 or > 65535)
            error = "The control port must be a number between 1 and 65535.";
        else
        {
            try
            {
                ControlAuth.DecodeKey(_vm.LocalKey.Trim());
                return new LocalSettings(_vm.LocalMachine.Trim(), _vm.LocalKey.Trim(), port);
            }
            catch (ControlException ex)
            {
                error = ex.Message;
            }
        }
        if (showErrors)
            MessageBox.Show(this, error, "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>Validates and returns the edited config, or null after telling the user what is wrong.</summary>
    private DeskBridgeConfig? CurrentConfig(bool showErrors)
    {
        var errors = new List<string>();
        var config = _vm.Model.ToConfig(errors);
        errors.AddRange(LayoutPlanner.Validate(config));
        if (errors.Count == 0)
            return config;
        if (showErrors)
            MessageBox.Show(this, "Fix these first:\n\n" + string.Join("\n", errors.Select(e => "• " + e)),
                "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    private bool SaveConfig()
    {
        var config = CurrentConfig(showErrors: true);
        if (config is null)
            return false;
        ConfigLoader.Save(_options.ConfigPath, config);
        _vm.Status = $"Saved {_options.ConfigPath}";
        return true;
    }

    // ------------------------------------------------------------------ agent

    private void RestartAgent()
    {
        var local = CurrentLocalSettings(showErrors: false);
        _ = RestartAgentAsync(local);
    }

    private async Task RestartAgentAsync(LocalSettings? local)
    {
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
            _agent = null;
        }
        if (local is null)
        {
            _vm.AgentStatus = "Agent stopped: check 'This PC'";
            return;
        }

        var agent = new AgentHost(local, Simulated.Applier(AddLog), new WindowsDisplayDetector(), AddLog);
        agent.ProfileApplied += profile => Dispatcher.BeginInvoke(() => _vm.AgentStatus = $"Agent '{local.Machine}' · port {local.ControlPort} · active: {profile}");
        try
        {
            agent.Start();
            _agent = agent;
            _vm.AgentStatus = $"Agent '{local.Machine}' · port {local.ControlPort} · simulated";
        }
        catch (SocketException ex)
        {
            _vm.AgentStatus = $"Agent stopped: port {local.ControlPort} busy";
            AddLog($"Could not start the agent on port {local.ControlPort}: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ObservableObject.AnyChanged -= ScheduleRedraw;
        // The agent's loops run on the thread pool, so blocking briefly here cannot deadlock.
        _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnClosed(e);
    }

    private void AddLog(string message)
    {
        void Append()
        {
            foreach (var line in message.Split('\n'))
                _vm.Log.Add($"{DateTime.Now:HH:mm:ss}  {line.TrimEnd()}");
            while (_vm.Log.Count > 1000)
                _vm.Log.RemoveAt(0);
            if (_vm.Log.Count > 0)
                LogList.ScrollIntoView(_vm.Log[^1]);
        }

        if (Dispatcher.CheckAccess())
            Append();
        else
            Dispatcher.BeginInvoke(Append);
    }

    // ------------------------------------------------------------------ layout drawing

    private void ScheduleRedraw()
    {
        if (_redrawPending)
            return;
        _redrawPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _redrawPending = false;
            DrawLayout();
            UpdateSummary();
        });
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLayout();

    private Color ColorOf(MachineVm machine)
    {
        int index = _vm.Model.Machines.IndexOf(machine);
        return MachineColors[Math.Max(index, 0) % MachineColors.Length];
    }

    private void DrawLayout()
    {
        var canvas = LayoutCanvas;
        canvas.Children.Clear();
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        var machines = _vm.Model.Machines.Where(m => m.Displays.Count > 0).ToList();
        if (width <= 0 || height <= 0 || machines.Count == 0)
            return;

        const double gap = 60, labelHeight = 28;
        var boxes = machines.Select(m => (
            Machine: m,
            MinX: m.Displays.Min(d => d.X),
            MinY: m.Displays.Min(d => d.Y),
            Width: (double)(m.Displays.Max(d => d.X + d.Width) - m.Displays.Min(d => d.X)),
            Height: (double)(m.Displays.Max(d => d.Y + d.Height) - m.Displays.Min(d => d.Y)))).ToList();

        double totalWidth = boxes.Sum(b => b.Width);
        double scale = Math.Min((width - gap * (boxes.Count - 1)) / totalWidth, (height - labelHeight) / boxes.Max(b => b.Height));
        if (scale <= 0)
            return;
        double x = (width - totalWidth * scale - gap * (boxes.Count - 1)) / 2;
        var selected = AssignmentGrid.SelectedItem as AssignmentVm;

        foreach (var box in boxes)
        {
            var color = ColorOf(box.Machine);
            var label = new TextBlock
            {
                Text = box.Machine + (box.Machine.HasInput ? "   (keyboard & mouse)" : ""),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, 2);
            canvas.Children.Add(label);

            foreach (var display in box.Machine.Displays)
            {
                var assignment = _vm.SelectedProfile?.Assignments.FirstOrDefault(a => a.Owner == box.Machine && a.Display == display);
                var user = assignment?.UsedBy ?? box.Machine;
                var userColor = ColorOf(user);
                bool lent = user != box.Machine;
                bool isSelected = assignment is not null && assignment == selected;

                var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock
                {
                    Text = display.Id,
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(userColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                text.Children.Add(new TextBlock
                {
                    Text = lent ? $"→ {user}" : "own",
                    FontWeight = lent ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = lent ? new SolidColorBrush(userColor) : InfoBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                text.Children.Add(new TextBlock
                {
                    Text = lent && assignment is not null ? StreamLabel(assignment) : $"{display.Width}x{display.Height}",
                    FontSize = 11,
                    Foreground = InfoBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });

                var rect = new Border
                {
                    Width = Math.Max(display.Width * scale - 6, 10),
                    Height = Math.Max(display.Height * scale - 6, 10),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(lent ? (byte)0x55 : (byte)0x22, userColor.R, userColor.G, userColor.B)),
                    BorderBrush = new SolidColorBrush(isSelected ? Colors.Black : color),
                    BorderThickness = new Thickness(isSelected ? 3 : 1.5),
                    Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) },
                    Cursor = Cursors.Hand,
                    ToolTip = $"Display {display.Id} of {box.Machine}: {display.Summary}\nUsed by {user}",
                };
                rect.MouseLeftButtonDown += (_, _) =>
                {
                    if (assignment is null)
                        return;
                    AssignmentGrid.SelectedItem = assignment;
                    AssignmentGrid.ScrollIntoView(assignment);
                };
                Canvas.SetLeft(rect, x + (display.X - box.MinX) * scale + 3);
                Canvas.SetTop(rect, labelHeight + (display.Y - box.MinY) * scale + 3);
                canvas.Children.Add(rect);
            }

            x += box.Width * scale + gap;
        }
    }

    private static string StreamLabel(AssignmentVm a)
    {
        string res = a.Resolution.Equals(AssignmentVm.Native, StringComparison.OrdinalIgnoreCase) ? $"{a.Display.Width}x{a.Display.Height}" : a.Resolution;
        string hz = a.Refresh.Equals(AssignmentVm.Native, StringComparison.OrdinalIgnoreCase) ? a.Display.RefreshHz.ToString() : a.Refresh;
        string mbps = a.Bitrate.Equals(AssignmentVm.Auto, StringComparison.OrdinalIgnoreCase) ? "auto" : a.Bitrate + " Mbps";
        return $"{res} @ {hz} · {mbps}";
    }

    private void UpdateSummary()
    {
        var lines = new List<SummaryLine>();
        var profile = _vm.SelectedProfile;
        if (profile is null)
        {
            _vm.Summary = [new SummaryLine("No profile selected. Create one with 'New'.", InfoBrush)];
            return;
        }

        var errors = new List<string>();
        var config = _vm.Model.ToConfig(errors);
        if (errors.Count == 0)
        {
            try
            {
                var plan = LayoutPlanner.Plan(config, profile.Name.Trim());
                foreach (var m in config.Machines)
                {
                    int virtuals = plan.VirtualDisplaysOf(m.Id).Count(), shows = plan.StreamsTo(m.Id).Count();
                    var name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name;
                    lines.Add(new SummaryLine(
                        $"{name}: creates {virtuals} virtual display{(virtuals == 1 ? "" : "s")} and sends {virtuals} stream{(virtuals == 1 ? "" : "s")}; "
                        + $"shows {shows} stream{(shows == 1 ? "" : "s")} from other machines.",
                        InfoBrush));
                }
                if (plan.Streams.Count > 0)
                    lines.Add(new SummaryLine($"Network: about {plan.Streams.Sum(s => s.BitrateMbps)} Mbps in total.", InfoBrush));
                lines.AddRange(plan.Warnings.Select(w => new SummaryLine("⚠ " + w, WarningBrush)));
            }
            catch (ConfigException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }
        lines.AddRange(errors.Select(e => new SummaryLine("✖ " + e, ErrorBrush)));
        _vm.Summary = lines;
    }

    private void AssignmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawLayout();

    // ------------------------------------------------------------------ profile bar

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new ProfileVm { Name = UniqueProfileName("new profile") };
        profile.Sync(_vm.Model.Machines);
        _vm.Model.Profiles.Add(profile);
        _vm.SelectedProfile = profile;
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } source)
            return;
        var copy = new ProfileVm { Name = UniqueProfileName(source.Name + " copy") };
        copy.Sync(_vm.Model.Machines);
        foreach (var (from, to) in source.Assignments.Zip(copy.Assignments))
        {
            to.UsedBy = from.UsedBy;
            to.Resolution = from.Resolution;
            to.Refresh = from.Refresh;
            to.Bitrate = from.Bitrate;
        }
        _vm.Model.Profiles.Add(copy);
        _vm.SelectedProfile = copy;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } profile)
            return;
        if (MessageBox.Show(this, $"Delete profile '{profile.Name}'?", "DeskBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        int index = _vm.Model.Profiles.IndexOf(profile);
        _vm.Model.Profiles.Remove(profile);
        _vm.SelectedProfile = _vm.Model.Profiles.ElementAtOrDefault(Math.Min(index, _vm.Model.Profiles.Count - 1));
    }

    private string UniqueProfileName(string baseName)
    {
        var name = baseName;
        for (int i = 2; _vm.Model.Profiles.Any(p => p.Name == name); i++)
            name = $"{baseName} {i}";
        return name;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveConfig();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } profile || !SaveConfig())
            return;
        var config = CurrentConfig(showErrors: false)!;
        var local = CurrentLocalSettings(showErrors: true);
        if (local is null)
            return;

        var button = (Button)sender;
        button.IsEnabled = false;
        _vm.Status = $"Applying '{profile.Name}'...";
        try
        {
            var results = await Coordinator.SwitchProfileAsync(config, local, profile.Name.Trim());
            foreach (var r in results)
            {
                AddLog($"{r.Machine}: {(r.Ok ? "" : "ERROR ")}{r.Message}");
                foreach (var w in r.Warnings)
                    AddLog($"    warning: {w}");
            }
            var failed = results.Where(r => !r.Ok).Select(r => r.Machine).ToList();
            _vm.Status = failed.Count == 0
                ? $"Profile '{profile.Name}' applied on {results.Count} machines (simulated)."
                : $"Profile '{profile.Name}' failed on: {string.Join(", ", failed)}. See the log in 'This PC'.";
        }
        catch (Exception ex) when (ex is ControlException or ConfigException)
        {
            _vm.Status = ex.Message;
            AddLog(ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ machines tab

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        int n = _vm.Model.Machines.Count + 1;
        var id = $"machine{n}";
        while (_vm.Model.Machines.Any(m => m.Id == id))
            id = $"machine{++n}";
        var machine = new MachineVm { Id = id, Name = $"Machine {n}", Address = "192.168.1.x" };
        machine.Displays.Add(new DisplayVm());
        _vm.Model.Machines.Add(machine);
        _vm.SelectedMachine = machine;
    }

    private void RemoveMachine_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMachine is not { } machine)
            return;
        if (MessageBox.Show(this, $"Remove '{machine}' and its displays from every profile?", "DeskBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _vm.Model.Machines.Remove(machine);
        _vm.SelectedMachine = _vm.Model.Machines.FirstOrDefault();
    }

    private void AddDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMachine is not { } machine)
            return;
        int n = machine.Displays.Count + 1;
        while (machine.Displays.Any(d => d.Id == n.ToString()))
            n++;
        int right = machine.Displays.Count == 0 ? 0 : machine.Displays.Max(d => d.X + d.Width);
        machine.Displays.Add(new DisplayVm { Id = n.ToString(), X = right });
    }

    private void RemoveDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMachine is { } machine && DisplayGrid.SelectedItem is DisplayVm display)
            machine.Displays.Remove(display);
    }

    private async void DetectDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMachine is not { } machine)
            return;

        IReadOnlyList<DisplayConfig> detected;
        if (machine.Id == _vm.LocalMachine.Trim())
        {
            detected = new WindowsDisplayDetector().DetectDisplays();
        }
        else
        {
            var local = CurrentLocalSettings(showErrors: true);
            if (local is null)
                return;
            var config = _vm.Model.ToConfig([]);
            _vm.Status = $"Asking '{machine.Id}' for its displays...";
            try
            {
                detected = await Coordinator.GetDisplaysAsync(config, local, machine.Id);
            }
            catch (ControlException ex)
            {
                _vm.Status = ex.Message;
                MessageBox.Show(this, ex.Message, "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Update in place so profiles keep their settings for displays that still exist.
        foreach (var d in detected)
        {
            var existing = machine.Displays.FirstOrDefault(x => x.Id == d.Id);
            if (existing is null)
                machine.Displays.Add(DisplayVm.From(d));
            else
                existing.CopyFrom(d);
        }
        var removed = machine.Displays.Where(x => detected.All(d => d.Id != x.Id)).ToList();
        foreach (var gone in removed)
            machine.Displays.Remove(gone);

        _vm.Status = $"'{machine}': {detected.Count} display(s) detected"
            + (removed.Count > 0 ? $", {removed.Count} removed." : ".") + " Save to keep them.";
    }

    // ------------------------------------------------------------------ this PC tab

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "A new key means pasting it again on every other machine. Continue?", "DeskBridge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.LocalKey = ControlAuth.NewKey();
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_vm.LocalKey);
        _vm.Status = "Shared key copied to the clipboard.";
    }

    private async void SaveLocal_Click(object sender, RoutedEventArgs e)
    {
        var local = CurrentLocalSettings(showErrors: true);
        if (local is null)
            return;
        local.Save(_options.LocalPath);
        _vm.Status = $"Saved {_options.LocalPath}";
        await RestartAgentAsync(local);
    }

    // ------------------------------------------------------------------ development snapshots

    private void SaveSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { DrawLayout(); UpdateSummary(); });
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });

            var root = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(dir, $"tab{i}.png"));
            encoder.Save(file);
        }
        Close();
    }
}
