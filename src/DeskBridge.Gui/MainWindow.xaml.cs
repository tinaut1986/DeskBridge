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
using DeskBridge.Platform;

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
        FileLog.Enabled = options.SnapshotDir is null;
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
        {
            RestartAgent();
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.InvokeAsync(RestoreAppliedAsync), TaskScheduler.Default);
        }
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

    /// <summary>
    /// First run: only this PC, named after Windows' computer name (so two machines never share an id).
    /// The others appear by themselves when they connect to it.
    /// </summary>
    private static DeskBridgeConfig CreateDefaultConfig()
    {
        var displays = PlatformServices.DisplayDetector().DetectDisplays();
        if (displays.Count == 0)
            displays = [new DisplayConfig("1", 1920, 1080)];

        var self = new MachineConfig(DefaultMachineId(), Environment.MachineName, LocalIPv4() ?? "127.0.0.1", displays, HasInput: true);
        return new DeskBridgeConfig([self], [new ProfileConfig("personal", [])]);
    }

    /// <summary>This PC's id by default: its computer name, lower case.</summary>
    private static string DefaultMachineId() => Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// This PC's address on the real network: an IPv4 of an adapter that is up and has a gateway
    /// (virtual adapters of WSL, Hyper-V or VirtualBox have none), wired first.
    /// </summary>
    private static string? LocalIPv4()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => (Adapter: n, Props: n.GetIPProperties()))
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .OrderBy(x => x.Adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? 0 : 1)
                .SelectMany(x => x.Props.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
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
            local = new LocalSettings(DefaultMachineId(), ControlAuth.NewKey(), _vm.Model.ControlPort);
            if (_options.SnapshotDir is null)
            {
                local.Save(_options.LocalPath);
                AddLog(L.T("Created {path} with a new shared key.", ("path", _options.LocalPath)));
            }
        }
        // A machine's id is its computer name; settings from older versions ("desktop", "laptop") move over.
        var id = DefaultMachineId();
        if (local.Machine != id)
        {
            var old = local.Machine;
            local = local with { Machine = id };
            if (_options.SnapshotDir is null)
                local.Save(_options.LocalPath);
            if (_vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == old) is { } entry && _vm.Model.Machines.All(m => m.Id.Trim() != id))
            {
                entry.Id = id; // profiles refer to the machine itself, so they follow the rename
                if (_options.SnapshotDir is null && CurrentConfig(showErrors: false) is { } renamed)
                    ConfigLoader.Save(_options.ConfigPath, renamed);
            }
            AddLog(L.T("This PC is now identified by its computer name, {machine} (it was {old}).", ("machine", id), ("old", old)));
        }

        _vm.LocalMachine = local.Machine;
        _vm.LocalKey = local.SharedKey;
        _vm.LocalPort = local.ControlPort.ToString();
        _vm.LocalConnectTo = local.ConnectTo ?? "";
    }

    private LocalSettings? CurrentLocalSettings(bool showErrors)
    {
        string? error = null;
        if (!int.TryParse(_vm.LocalPort, out var port) || port is < 1 or > 65535)
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
        if (_agent is not null && IsCoordinatorHere())
            _ = Coordinator.SyncConfigAsync(config, _agent.Directory);
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

    /// <summary>Set while the language and theme lists are filled, so selecting the current one saves nothing.</summary>
    private bool _fillingChoices;

    private void BuildLanguageMenu()
    {
        // As in NSA and llm-browser: "System" and each language by its own name, so it can be
        // found even when the current language is not understood.
        _fillingChoices = true;
        LanguageMenu.Items.Clear();
        LanguageMenu.Items.Add(new ComboBoxItem { Content = L.T("System"), Tag = L.System });
        foreach (var language in L.Languages)
            LanguageMenu.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
        LanguageMenu.SelectedItem = LanguageMenu.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.Language)
                                    ?? LanguageMenu.Items[0];
        _fillingChoices = false;
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingChoices && LanguageMenu.SelectedItem is ComboBoxItem { Tag: string code } && code != _settings.Language)
            SetLanguage(code);
    }

    private void BuildThemeMenu()
    {
        _fillingChoices = true;
        ThemeMenu.Items.Clear();
        foreach (var (choice, label) in new[] { (ThemeChoice.System, L.T("System")), (ThemeChoice.Light, L.T("Light")), (ThemeChoice.Dark, L.T("Dark")) })
        {
            var item = new ComboBoxItem { Content = label, Tag = choice };
            ThemeMenu.Items.Add(item);
            if (_settings.Theme == choice)
                ThemeMenu.SelectedItem = item;
        }
        _fillingChoices = false;
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingChoices || ThemeMenu.SelectedItem is not ComboBoxItem { Tag: ThemeChoice choice } || choice == _settings.Theme)
            return;
        SaveUserSettings(_settings with { Theme = choice });
        ThemeManager.Apply(choice);
    }

    // ------------------------------------------------------------------ navigation and the profile menu

    private void GoToLayout_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private void ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        ProfileMenu.PlacementTarget = ProfileMenuButton;
        ProfileMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ProfileMenu.IsOpen = true;
    }

    /// <summary>Rename: the profile list turns into a box with its name until Enter, Escape or leaving it.</summary>
    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is null)
            return;
        ProfileNameBox.Visibility = Visibility.Visible;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void ProfileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            ProfileNameBox.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void ProfileNameBox_LostFocus(object sender, RoutedEventArgs e) => ProfileNameBox.Visibility = Visibility.Collapsed;

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

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FileLog.Directory);
        OpenExternal(FileLog.Directory);
    }

    private void CopyLogSelection_Click(object sender, RoutedEventArgs e) => CopyLog(LogList.SelectedItems.Cast<string>());

    private void CopyLogAll_Click(object sender, RoutedEventArgs e) => CopyLog(_vm.Log);

    private void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyLog(LogList.SelectedItems.Cast<string>());
            e.Handled = true;
        }
    }

    private void CopyLog(IEnumerable<string> lines)
    {
        // In display order, whatever order they were selected in.
        var text = string.Join(Environment.NewLine, _vm.Log.Where(lines.ToHashSet().Contains));
        if (text.Length == 0)
            return;
        Clipboard.SetText(text);
        _vm.Status = L.T("Log copied to the clipboard.");
    }

    private static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    // ------------------------------------------------------------------ agent

    private void RestartAgent() => _ = RestartAgentAsync(CurrentLocalSettings(showErrors: false));

    private async Task RestartAgentAsync(LocalSettings? local)
    {
        StopInputSharing();
        if (_agent is not null)
        {
            await StopAllVideoAsync();
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
            UpdateAppliedIndicator();
        });
        try
        {
            agent.Start();
            _agent = agent;
            agent.Directory.Changed += () => Dispatcher.BeginInvoke(OnPeersChanged);
            if (agent.Reverse is { } reverse)
                reverse.StateChanged += () => Dispatcher.BeginInvoke(() => UpdateAgentStatus());
        }
        catch (SocketException ex)
        {
            AddLog(L.T("Could not start the agent on port {port}: {error}", ("port", local.ControlPort), ("error", ex.Message)));
        }
        UpdateAgentStatus(local.ControlPort);
        UpdateInputSharing();
    }

    private void UpdateAgentStatus(int? failedPort = null)
    {
        UpdateRoleUi();
        if (_agent is null)
        {
            _vm.AgentStatus = failedPort is { } port
                ? L.T("Agent stopped: port {port} is busy", ("port", port))
                : L.T("Agent stopped: check Connection and security");
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
        var incoming = _agent.Directory.Connected.Where(m => m != _agent.Reverse?.ConnectedTo && m != _agent.Machine).ToList();
        if (incoming.Count > 0)
            parts.Add(L.T("connected: {machines}", ("machines", string.Join(", ", incoming.Select(MachineName)))));
        parts.Add(_activeProfile is null ? L.T("simulated") : L.T("active: {profile}", ("profile", _activeProfile)));
        if (InputStatus() is { } input)
            parts.Add(input);
        _vm.AgentStatus = string.Join(" · ", parts);
    }

    /// <summary>This PC connects to a coordinator: profiles and machines are edited and applied there.</summary>
    private bool IsClientHere() => !string.IsNullOrWhiteSpace(_vm.LocalConnectTo);

    /// <summary>
    /// On a client, what only the coordinator can do is disabled (with the reason as a tooltip) instead of
    /// answering with a message after the click.
    /// </summary>
    private void UpdateRoleUi()
    {
        bool client = IsClientHere();
        string? reason = client ? L.T("This PC connects to a coordinator: profiles and machines are edited and applied there.") : null;
        foreach (var control in new Control[] { NewProfileButton, DuplicateProfileButton, RenameProfileButton, DeleteProfileButton, SaveButton, ApplyButton, AddMachineButton, RemoveMachineButton })
        {
            control.IsEnabled = !client;
            control.ToolTip = reason;
            ToolTipService.SetShowOnDisabled(control, true);
        }
        VideoTestButton.IsEnabled = !client;
        VideoTestButton.ToolTip = client ? L.T("Only the coordinator can show another machine's screen.") : null;
        ToolTipService.SetShowOnDisabled(VideoTestButton, true);
    }

    protected override void OnClosed(EventArgs e)
    {
        ObservableObject.AnyChanged -= ScheduleRedraw;
        L.Changed -= OnLanguageChanged;
        ThemeManager.Changed -= ScheduleRedraw;
        _clipboard?.Dispose();
        StopInputSharing();
        DisposeTray();
        // The agent's loops run on the thread pool, so blocking briefly here cannot deadlock.
        StopAllVideoAsync().Wait(TimeSpan.FromSeconds(2));
        RestorePrimary(); // the displays show this PC again
        _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _updater.InstallOnExit();
        base.OnClosed(e);
    }

    private void AddLog(string message)
    {
        foreach (var line in message.Split('\n'))
            FileLog.Write(line.TrimEnd());

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
            UpdateAppliedIndicator();
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
        foreach (var (machine, display) in source.PrimaryDisplays)
            copy.PrimaryDisplays[machine] = display;
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
        if (IsClientHere())
            return; // the buttons are disabled: profiles are applied from the coordinator
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
            if (results.Any(r => r.Ok))
            {
                RememberApplied(profile.Name.Trim(), config);
                await StartProfileStreamsAsync(LayoutPlanner.Plan(config, profile.Name.Trim()));
            }
            foreach (var r in results)
            {
                AddLog($"{r.Machine}: {(r.Ok ? "" : L.T("ERROR") + " ")}{r.Message}");
                foreach (var w in r.Warnings)
                    AddLog("    " + L.T("warning: {text}", ("text", w)));
            }
            var failed = results.Where(r => !r.Ok).Select(r => r.Machine).ToList();
            _vm.Status = failed.Count == 0
                ? L.T("Profile {profile} applied on {count} machines.", ("profile", profile.Name), ("count", results.Count))
                : L.T("Profile {profile} failed on: {machines}. See the Log.", ("profile", profile.Name), ("machines", string.Join(", ", failed)));
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
            // In the original arrangement's coordinates, even while DeskBridge has made another display primary.
            var (ox, oy) = DeskOffset();
            detected = PlatformServices.DisplayDetector().DetectDisplays().Select(d => d with { X = d.X - ox, Y = d.Y - oy }).ToList();
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

        int removed = MergeDisplays(machine, detected);
        _vm.Status = L.T("{machine}: {count} displays detected, {removed} removed. Save to keep them.",
            ("machine", machine), ("count", detected.Count), ("removed", removed));
    }

    /// <summary>Updates a machine's displays in place, so profiles keep their settings for displays that still exist.</summary>
    /// <returns>How many displays were removed.</returns>
    private static int MergeDisplays(MachineVm machine, IReadOnlyList<DisplayConfig> detected)
    {
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
        return removed.Count;
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
