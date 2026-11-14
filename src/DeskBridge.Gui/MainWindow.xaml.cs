using System.Diagnostics;
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
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Core.Simulation;
using DeskBridge.Core.Updates;
using DeskBridge.Gui.Updates;
using DeskBridge.Gui.ViewModels;
using DeskBridge.Platform.Windows;

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

    private static Brush ErrorBrush => ThemeManager.Resource("ErrorText");
    private static Brush WarningBrush => ThemeManager.Resource("WarningText");
    private static Brush InfoBrush => ThemeManager.Resource("InfoText");

    private readonly StartupOptions _options;
    private readonly MainViewModel _vm = new();
    private readonly Updater _updater;
    private UserSettings _settings;
    private AgentHost? _agent;
    private string? _activeProfile;
    private bool _redrawPending;

    public MainWindow(StartupOptions options, UserSettings settings)
    {
        _options = options;
        _settings = settings;
        InitializeComponent();
        DataContext = _vm;

        ObservableObject.AnyChanged += ScheduleRedraw;
        L.Changed += OnLanguageChanged;
        LoadConfig();
        LoadLocalSettings();
        InitSharing();
        BuildLanguageMenu();
        BuildThemeMenu();
        ThemeManager.Changed += ScheduleRedraw;
        InitTray();
        UpdateChannelMenu();

        _updater = new Updater(WantsBetas, () => L.Current, () => this, beforeInstall: () => _quitting = true);
        if (options.SnapshotDir is null)
            _updater.Start();

        UpdateAgentStatus();
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
            if (!File.Exists(_options.ConfigPath) && _options.SnapshotDir is null)
            {
                ConfigLoader.Save(_options.ConfigPath, config);
                AddLog(L.T("Created {path} from this PC's displays.", ("path", _options.ConfigPath)));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                L.T("Could not read {path}:", ("path", _options.ConfigPath)) + "\n" + ex.Message + "\n\n"
                + L.T("Starting with a new configuration (the file is not overwritten until you save)."),
                "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            config = CreateDefaultConfig();
        }

        ShowConfig(config, _options.Profile);
    }

    /// <summary>Replaces what is being edited, keeping the selected profile and machine when they still exist.</summary>
    private void ShowConfig(DeskBridgeConfig config, string? profile = null)
    {
        profile ??= _vm.SelectedProfile?.Name;
        var machine = _vm.SelectedMachine?.Id;
        _vm.Model = EditorModel.FromConfig(config);
        _vm.Model.Machines.CollectionChanged += (_, _) => _vm.RaiseMachineIds();
        _vm.SelectedProfile = _vm.Model.Profiles.FirstOrDefault(p => p.Name == profile) ?? _vm.Model.Profiles.FirstOrDefault();
        _vm.SelectedMachine = _vm.Model.Machines.FirstOrDefault(m => m.Id == machine) ?? _vm.Model.Machines.FirstOrDefault();
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
            // The laptop takes every desktop display except the first one (at most two).
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
                AddLog(L.T("Created {path} with a new shared key.", ("path", _options.LocalPath)));
            }
        }
        _vm.LocalMachine = local.Machine;
        _vm.LocalKey = local.SharedKey;
        _vm.LocalPort = local.ControlPort.ToString();
        _vm.LocalConnectTo = local.ConnectTo ?? "";
    }

    private LocalSettings? CurrentLocalSettings(bool showErrors)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(_vm.LocalMachine))
            error = L.T("Choose which machine this PC is.");
        else if (!int.TryParse(_vm.LocalPort, out var port) || port is < 1 or > 65535)
            error = L.T("The control port must be a number between 1 and 65535.");
        else
        {
            try
            {
                ControlAuth.DecodeKey(_vm.LocalKey.Trim());
                return new LocalSettings(_vm.LocalMachine.Trim(), _vm.LocalKey.Trim(), port,
                    string.IsNullOrWhiteSpace(_vm.LocalConnectTo) ? null : _vm.LocalConnectTo.Trim());
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
            MessageBox.Show(this, L.T("Fix these first:") + "\n\n" + string.Join("\n", errors.Select(e => "• " + e)),
                "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    private bool SaveConfig()
    {
        var config = CurrentConfig(showErrors: true);
        if (config is null)
            return false;
        ConfigLoader.Save(_options.ConfigPath, config);
        _vm.Status = L.T("Saved {path}", ("path", _options.ConfigPath));
        return true;
    }

    private void SaveUserSettings(UserSettings settings)
    {
        _settings = settings;
        try
        {
            settings.Save(_options.SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(L.T("Could not save {path}: {error}", ("path", _options.SettingsPath), ("error", ex.Message)));
        }
    }

    // ------------------------------------------------------------------ language and updates

    private void BuildLanguageMenu()
    {
        // As in NSA and llm-browser: "System" and each language by its own name, so it can be
        // found even when the current language is not understood.
        LanguageMenu.Items.Clear();
        var system = new MenuItem { Header = L.T("System"), IsCheckable = true, IsChecked = _settings.Language == L.System };
        system.Click += (_, _) => SetLanguage(L.System);
        LanguageMenu.Items.Add(system);
        LanguageMenu.Items.Add(new Separator());
        foreach (var language in L.Languages)
        {
            var item = new MenuItem { Header = language.Name, IsCheckable = true, IsChecked = _settings.Language == language.Code };
            item.Click += (_, _) => SetLanguage(language.Code);
            LanguageMenu.Items.Add(item);
        }
    }

    private void BuildThemeMenu()
    {
        ThemeMenu.Items.Clear();
        foreach (var (choice, label) in new[] { (ThemeChoice.System, L.T("System")), (ThemeChoice.Light, L.T("Light")), (ThemeChoice.Dark, L.T("Dark")) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _settings.Theme == choice };
            item.Click += (_, _) =>
            {
                SaveUserSettings(_settings with { Theme = choice });
                ThemeManager.Apply(choice);
                BuildThemeMenu();
            };
            ThemeMenu.Items.Add(item);
        }
    }

    private void SetLanguage(string setting)
    {
        SaveUserSettings(_settings with { Language = L.Codes.Contains(setting) ? setting : L.System });
        L.SetLanguage(L.Resolve(_settings.Language, L.SystemLocales()));
        BuildLanguageMenu();
    }

    private void OnLanguageChanged()
    {
        _vm.Model.Relabel();
        _vm.RaiseComputedTexts();
        _placementRowsStale = true;
        BuildLanguageMenu();
        BuildThemeMenu();
        UpdateAgentStatus();
        _updater.LanguageChanged();
        ScheduleRedraw();
    }

    /// <summary>Without a user choice, an installed beta keeps offering betas.</summary>
    private bool WantsBetas() => _settings.Betas ?? AppInfo.Channel == "beta";

    private void UpdateChannelMenu()
    {
        StableMenu.IsChecked = !WantsBetas();
        BetaMenu.IsChecked = WantsBetas();
    }

    private void SetBetas(bool betas)
    {
        SaveUserSettings(_settings with { Betas = betas });
        UpdateChannelMenu();
        _updater.BetasChanged();
    }

    private void Stable_Click(object sender, RoutedEventArgs e) => SetBetas(false);
    private void Beta_Click(object sender, RoutedEventArgs e) => SetBetas(true);
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _updater.CheckNowAsync();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"DeskBridge {AppInfo.DisplayVersion}\n\n"
            + L.T("Use several computers with one keyboard and mouse, and share their displays.") + "\n\n"
            + ReleaseFeed.ProjectPage,
            L.T("About DeskBridge"), MessageBoxButton.OK, MessageBoxImage.Information);

    private void ProjectPage_Click(object sender, RoutedEventArgs e) => OpenExternal(ReleaseFeed.ProjectPage);

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Directory);
        OpenExternal(AppPaths.Directory);
    }

    private static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    // ------------------------------------------------------------------ agent

    private void RestartAgent() => _ = RestartAgentAsync(CurrentLocalSettings(showErrors: false));

    private async Task RestartAgentAsync(LocalSettings? local)
    {
        if (_agent is not null)
        {
            await _agent.DisposeAsync();
            _agent = null;
        }
        _activeProfile = null;
        if (local is null)
        {
            UpdateAgentStatus();
            return;
        }

        var agent = CreateAgent(local);
        agent.ProfileApplied += profile => Dispatcher.BeginInvoke(() =>
        {
            _activeProfile = profile;
            UpdateAgentStatus();
        });
        try
        {
            agent.Start();
            _agent = agent;
            agent.Directory.Changed += () => Dispatcher.BeginInvoke(() => UpdateAgentStatus());
            if (agent.Reverse is { } reverse)
                reverse.StateChanged += () => Dispatcher.BeginInvoke(() => UpdateAgentStatus());
        }
        catch (SocketException ex)
        {
            AddLog(L.T("Could not start the agent on port {port}: {error}", ("port", local.ControlPort), ("error", ex.Message)));
        }
        UpdateAgentStatus(local.ControlPort);
    }

    private void UpdateAgentStatus(int? failedPort = null)
    {
        if (_agent is null)
        {
            _vm.AgentStatus = failedPort is { } port
                ? L.T("Agent stopped: port {port} is busy", ("port", port))
                : L.T("Agent stopped: check This PC");
            return;
        }

        var parts = new List<string> { L.T("Agent {machine}", ("machine", _agent.Machine)) };
        parts.Add(_agent.Port is { } listening ? L.T("port {port}", ("port", listening)) : L.T("outgoing only"));
        if (_agent.Reverse is { } reverse)
        {
            parts.Add(reverse.ConnectedTo is { } coordinator
                ? L.T("connected to {machine}", ("machine", MachineName(coordinator)))
                : L.T("connecting to {address}…", ("address", reverse.Target)));
        }
        var incoming = _agent.Directory.Connected.Where(m => m != _agent.Reverse?.ConnectedTo).ToList();
        if (incoming.Count > 0)
            parts.Add(L.T("connected: {machines}", ("machines", string.Join(", ", incoming.Select(MachineName)))));
        parts.Add(_activeProfile is null ? L.T("simulated") : L.T("active: {profile}", ("profile", _activeProfile)));
        _vm.AgentStatus = string.Join(" · ", parts);
    }

    protected override void OnClosed(EventArgs e)
    {
        ObservableObject.AnyChanged -= ScheduleRedraw;
        L.Changed -= OnLanguageChanged;
        ThemeManager.Changed -= ScheduleRedraw;
        _clipboard?.Dispose();
        DisposeTray();
        // The agent's loops run on the thread pool, so blocking briefly here cannot deadlock.
        _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _updater.InstallOnExit();
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
            _vm.RaiseDisplaysTitle(); // the selected machine may have been renamed
        });
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLayout();

    private Color ColorOf(MachineVm machine)
    {
        int index = _vm.Model.Machines.IndexOf(machine);
        return MachineColors[Math.Max(index, 0) % MachineColors.Length];
    }

    /// <summary>The plan of the selected profile as edited right now, or null while it has errors.</summary>
    private LayoutPlan? CurrentPlan()
    {
        if (_vm.SelectedProfile is not { } profile)
            return null;
        var errors = new List<string>();
        var config = _vm.Model.ToConfig(errors);
        if (errors.Count > 0)
            return null;
        try
        {
            return LayoutPlanner.Plan(config, profile.Name.Trim());
        }
        catch (ConfigException)
        {
            return null;
        }
    }

    private void DrawLayout()
    {
        var canvas = LayoutCanvas;
        canvas.Children.Clear();
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        var machines = _vm.Model.Machines.Where(m => m.Displays.Count > 0).ToList();
        if (width <= 0 || height <= 0 || machines.Count == 0)
            return;

        // Virtual displays each machine creates in this profile, where they will sit in its desktop.
        var plan = CurrentPlan();
        var virtuals = machines.ToDictionary(m => m, m => plan?.VirtualDisplaysOf(m.Id.Trim()).ToList() ?? []);

        const double gap = 60, labelHeight = 28;
        var boxes = machines.Select(m =>
        {
            var rects = m.Displays.Select(d => (d.X, d.Y, d.Width, d.Height))
                .Concat(virtuals[m].Select(v => (v.X, v.Y, v.Width, v.Height))).ToList();
            return (
                Machine: m,
                MinX: rects.Min(r => r.X),
                MinY: rects.Min(r => r.Y),
                Width: (double)(rects.Max(r => r.X + r.Width) - rects.Min(r => r.X)),
                Height: (double)(rects.Max(r => r.Y + r.Height) - rects.Min(r => r.Y)));
        }).ToList();

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
                Text = box.Machine + (box.Machine.HasInput ? "   (" + L.T("keyboard and mouse") + ")" : ""),
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
                    Text = lent ? $"→ {user}" : L.T("own"),
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
                    ToolTip = L.T("Display {display} of {machine}: {mode}", ("display", display.Id), ("machine", box.Machine), ("mode", display.Summary))
                              + "\n" + L.T("Used by {machine}", ("machine", user)),
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

            foreach (var v in virtuals[box.Machine])
            {
                var lender = _vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == v.ForMachine);
                var lenderColor = lender is null ? Colors.Gray : ColorOf(lender);
                var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock
                {
                    Text = L.T("virtual"),
                    FontSize = 11,
                    Foreground = InfoBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                text.Children.Add(new TextBlock
                {
                    Text = $"← {lender} {v.ForDisplay}",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(lenderColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                var rect = new Border
                {
                    Width = Math.Max(v.Width * scale - 6, 10),
                    Height = Math.Max(v.Height * scale - 6, 10),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(0x10, color.R, color.G, color.B)),
                    BorderBrush = DashedBrush(color),
                    BorderThickness = new Thickness(1.5),
                    Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) },
                    ToolTip = L.T("Virtual display of {machine} at ({x}, {y}): shown on display {display} of {lender}",
                        ("machine", box.Machine), ("x", v.X), ("y", v.Y), ("display", v.ForDisplay), ("lender", lender)),
                };
                Canvas.SetLeft(rect, x + (v.X - box.MinX) * scale + 3);
                Canvas.SetTop(rect, labelHeight + (v.Y - box.MinY) * scale + 3);
                canvas.Children.Add(rect);

                var assignment = _vm.SelectedProfile?.Assignments.FirstOrDefault(a =>
                    a.Owner.Id.Trim() == v.ForMachine && a.Display.Id.Trim() == v.ForDisplay);
                if (assignment is not null)
                {
                    var others = box.Machine.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height))
                        .Concat(virtuals[box.Machine].Where(o => o.Key != v.Key).Select(o => new DisplayRect(o.X, o.Y, o.Width, o.Height)))
                        .ToList();
                    MakeDraggable(rect, assignment, v, others, new DragFrame(x, labelHeight, box.MinX, box.MinY, scale));
                }
            }

            x += box.Width * scale + gap;
        }
    }

    /// <summary>A dashed outline (Border has no dash style, so the brush draws the dashes).</summary>
    private static Brush DashedBrush(Color color)
    {
        var brush = new DrawingBrush
        {
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile,
            Drawing = new GeometryDrawing(new SolidColorBrush(color), null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }),
        };
        brush.Freeze();
        return brush;
    }

    private static string StreamLabel(AssignmentVm a)
    {
        string res = AssignmentVm.IsNative(a.Resolution) ? $"{a.Display.Width}x{a.Display.Height}" : a.Resolution;
        string hz = AssignmentVm.IsNative(a.Refresh) ? a.Display.RefreshHz.ToString() : a.Refresh;
        string mbps = AssignmentVm.IsAuto(a.Bitrate) ? L.T("auto") : $"{a.Bitrate} Mbps";
        return $"{res} @ {hz} · {mbps}";
    }

    private void UpdateSummary()
    {
        var lines = new List<SummaryLine>();
        var profile = _vm.SelectedProfile;
        if (profile is null)
        {
            _vm.PlacementRows = [];
            _vm.Summary = [new SummaryLine(L.T("No profile selected. Create one with New."), InfoBrush)];
            return;
        }

        var errors = new List<string>();
        var config = _vm.Model.ToConfig(errors);
        UpdatePlacementRows(profile);
        if (errors.Count == 0)
        {
            try
            {
                var plan = LayoutPlanner.Plan(config, profile.Name.Trim());
                foreach (var m in config.Machines)
                {
                    lines.Add(new SummaryLine(
                        L.T("{machine}: virtual displays to create and send: {created} · streams shown from other machines: {shown}",
                            ("machine", string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name),
                            ("created", plan.VirtualDisplaysOf(m.Id).Count()),
                            ("shown", plan.StreamsTo(m.Id).Count())),
                        InfoBrush));
                }
                if (plan.Streams.Count > 0)
                {
                    lines.Add(new SummaryLine(L.T("Network: about {mbps} Mbps in total.", ("mbps", plan.Streams.Sum(s => s.BitrateMbps))), InfoBrush));
                    lines.Add(new SummaryLine(L.T("Drag a dashed (virtual) display on the map to place it by hand; right-click it to place it automatically again."),
                        ThemeManager.Resource("MutedText")));
                }
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

    /// <summary>Rebuilt only when the set of borrowing machines changes, so open combo boxes are left alone.</summary>
    private void UpdatePlacementRows(ProfileVm profile)
    {
        var borrowers = _vm.Model.Machines
            .Where(m => profile.Assignments.Any(a => a.IsLent && a.UsedBy == m))
            .ToList();
        bool same = ReferenceEquals(_placementRowsProfile, profile)
                    && _vm.PlacementRows.Select(r => r.Machine).SequenceEqual(borrowers);
        if (same && !_placementRowsStale)
            return;
        _placementRowsProfile = profile;
        _placementRowsStale = false;
        _vm.PlacementRows = borrowers.Select(m => new PlacementRowVm(profile, m)).ToList();
    }

    private ProfileVm? _placementRowsProfile;
    private bool _placementRowsStale;

    private void AssignmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawLayout();

    // ------------------------------------------------------------------ profile bar

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new ProfileVm { Name = UniqueProfileName(L.T("new profile")) };
        profile.Sync(_vm.Model.Machines);
        _vm.Model.Profiles.Add(profile);
        _vm.SelectedProfile = profile;
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } source)
            return;
        var copy = new ProfileVm { Name = UniqueProfileName(L.T("{profile} copy", ("profile", source.Name))) };
        copy.Sync(_vm.Model.Machines);
        foreach (var (from, to) in source.Assignments.Zip(copy.Assignments))
        {
            to.UsedBy = from.UsedBy;
            to.Resolution = from.Resolution;
            to.Refresh = from.Refresh;
            to.Bitrate = from.Bitrate;
            to.Position = from.Position;
        }
        foreach (var (machine, side) in source.Placement)
            copy.Placement[machine] = side;
        _vm.Model.Profiles.Add(copy);
        _vm.SelectedProfile = copy;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } profile)
            return;
        if (MessageBox.Show(this, L.T("Delete profile {profile}?", ("profile", profile.Name)), "DeskBridge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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

    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplySelectedProfileAsync();

    private async Task ApplySelectedProfileAsync()
    {
        if (_vm.SelectedProfile is not { } profile || !SaveConfig())
            return;
        var config = CurrentConfig(showErrors: false)!;
        var local = CurrentLocalSettings(showErrors: true);
        if (local is null)
            return;

        var button = ApplyButton;
        button.IsEnabled = false;
        _vm.Status = L.T("Applying {profile}...", ("profile", profile.Name));
        try
        {
            var results = await Coordinator.SwitchProfileAsync(config, local, profile.Name.Trim(), _agent?.Directory);
            foreach (var r in results)
            {
                AddLog($"{r.Machine}: {(r.Ok ? "" : L.T("ERROR") + " ")}{r.Message}");
                foreach (var w in r.Warnings)
                    AddLog("    " + L.T("warning: {text}", ("text", w)));
            }
            var failed = results.Where(r => !r.Ok).Select(r => r.Machine).ToList();
            _vm.Status = failed.Count == 0
                ? L.T("Profile {profile} applied on {count} machines (simulated).", ("profile", profile.Name), ("count", results.Count))
                : L.T("Profile {profile} failed on: {machines}. See the log in This PC.", ("profile", profile.Name), ("machines", string.Join(", ", failed)));
            Notify(_vm.Status);
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
        var machine = new MachineVm { Id = id, Name = L.T("Machine {number}", ("number", n)), Address = "192.168.1.x" };
        machine.Displays.Add(new DisplayVm());
        _vm.Model.Machines.Add(machine);
        _vm.SelectedMachine = machine;
    }

    private void RemoveMachine_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMachine is not { } machine)
            return;
        if (MessageBox.Show(this, L.T("Remove {machine} and its displays from every profile?", ("machine", machine)), "DeskBridge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            _vm.Status = L.T("Asking {machine} for its displays...", ("machine", machine));
            try
            {
                detected = await Coordinator.GetDisplaysAsync(config, local, machine.Id, _agent?.Directory);
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

        _vm.Status = L.T("{machine}: {count} displays detected, {removed} removed. Save to keep them.",
            ("machine", machine), ("count", detected.Count), ("removed", removed.Count));
    }

    // ------------------------------------------------------------------ this PC tab

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.T("A new key means pasting it again on every other machine. Continue?"), "DeskBridge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.LocalKey = ControlAuth.NewKey();
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_vm.LocalKey);
        _vm.Status = L.T("Shared key copied to the clipboard.");
    }

    private async void SaveLocal_Click(object sender, RoutedEventArgs e)
    {
        var local = CurrentLocalSettings(showErrors: true);
        if (local is null)
            return;
        local.Save(_options.LocalPath);
        _vm.Status = L.T("Saved {path}", ("path", _options.LocalPath));
        await RestartAgentAsync(local);
    }

    // ------------------------------------------------------------------ development snapshots

    private async void SaveSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { DrawLayout(); UpdateSummary(); });
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
            Render(this, Path.Combine(dir, $"tab{i}.png"));
        }

        // With DESKBRIDGE_UPDATER_DEMO set: the update window once the simulated download is ready.
        if (_updater.Enabled)
        {
            await _updater.CheckNowAsync();
            for (int wait = 0; wait < 200 && _updater.Phase != UpdatePhase.Ready; wait++)
                await Task.Delay(100);
            await Task.Delay(300);
            if (_updater.OpenWindow is { } update)
            {
                update.Left = -32000;
                update.UpdateLayout();
                Render(update, Path.Combine(dir, "update.png"));
                update.Close();
            }
        }
        Close();
    }

    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
