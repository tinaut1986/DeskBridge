using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeskBridge.Core.Agent;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Simulation;
using DeskBridge.Core.Transfer;
using DeskBridge.Gui.Sharing;
using DeskBridge.Gui.ViewModels;
using DeskBridge.Platform;

namespace DeskBridge.Gui;

/// <summary>Clipboard and file sharing between machines.</summary>
public partial class MainWindow
{
    private FileReceiver _files = null!;
    private ClipboardSync? _clipboard;

    private void InitSharing()
    {
        _files = new FileReceiver(() => _settings.AcceptFiles ? _settings.EffectiveFilesFolder : null);
        _files.FileReceived += (from, path) => Dispatcher.BeginInvoke(() =>
        {
            var text = L.T("Received {file} from {machine}.", ("file", Path.GetFileName(path)), ("machine", MachineName(from)));
            AddLog(text);
            _vm.Status = text;
            Notify(text);
        });

        ClipboardSendBox.IsChecked = _settings.ClipboardSend;
        ClipboardReceiveBox.IsChecked = _settings.ClipboardReceive;
        AcceptFilesBox.IsChecked = _settings.AcceptFiles;
        FilesFolderBox.Text = _settings.EffectiveFilesFolder;

        if (_options.SnapshotDir is null)
            SourceInitialized += (_, _) => _clipboard = new ClipboardSync(this, SendClipboardAsync, AddLog) { SendEnabled = _settings.ClipboardSend };
    }

    private AgentHost CreateAgent(LocalSettings local)
    {
        var agent = new AgentHost(local, Simulated.Applier(AddLog), PlatformServices.DisplayDetector(), AddLog, _files);
        agent.ClipboardHandler = (from, message) => Dispatcher.InvokeAsync(() =>
        {
            bool applied = _settings.ClipboardReceive && _clipboard?.Apply(message) == true;
            if (applied)
                AddLog(L.T("Clipboard ({kind}) received from {machine}.", ("kind", ClipboardKind(message)), ("machine", MachineName(from))));
            return applied;
        }).Task;
        agent.DiagnosticsProvider = OwnDiagnostics;
        agent.ConfigReceived += config => Dispatcher.BeginInvoke(() => OnConfigReceived(config));
        // The coordinator takes profiles only from itself; a machine that connects to no one but is not
        // the coordinator (reached directly) takes them from the coordinator named in the profile.
        var defaultAccept = agent.AcceptProfilesFrom;
        agent.AcceptProfilesFrom = from => from == local.Machine
                                           || (defaultAccept(from) && Dispatcher.Invoke(() => !IsCoordinatorHere()));
        return agent;
    }

    /// <summary>What kind of content, for the log (never the content itself).</summary>
    private static string ClipboardKind(ClipboardMessage m) =>
        m.Text is { } text ? L.T("text, {count} characters", ("count", text.Length)) : L.T("image");

    private string MachineName(string id) =>
        _vm.Model.Machines.FirstOrDefault(m => m.Id == id)?.ToString() ?? id;

    /// <summary>Who the other machines are and this machine's key, or null if that is not set up yet.</summary>
    private (DeskBridgeConfig Config, LocalSettings Local)? PeerContext()
    {
        var local = CurrentLocalSettings(showErrors: false);
        if (local is null)
            return null;
        var config = _vm.Model.ToConfig([]);
        return config.Machines.Any(m => m.Id == local.Machine) ? (config, local) : null;
    }

    /// <summary>
    /// A machine that is not the coordinator learns the layout (and the other machines' addresses)
    /// from the profiles the coordinator applies: it keeps that copy, so it can share with them too.
    /// </summary>
    private void OnConfigReceived(DeskBridgeConfig config)
    {
        if (config.Machines.FirstOrDefault(m => m.HasInput)?.Id == _vm.LocalMachine.Trim())
            return; // the coordinator itself: its own config, already here
        if (_options.SnapshotDir is null)
            ConfigLoader.Save(_options.ConfigPath, config);
        ShowConfig(config);
    }

    private async Task SendClipboardAsync(ClipboardMessage message)
    {
        if (PeerContext() is not { } context)
            return;
        var directory = _agent?.Directory;
        var results = await Task.Run(() => Peers.SendClipboardAsync(context.Config, context.Local, message, directory));
        foreach (var r in results.Where(r => !r.Ok))
            AddLog(L.T("Clipboard not sent to {machine}: {error}", ("machine", MachineName(r.Machine)), ("error", r.Message)));
        var sentTo = results.Where(r => r.Ok).Select(r => MachineName(r.Machine)).ToList();
        if (sentTo.Count > 0)
            AddLog(L.T("Clipboard ({kind}) sent to {machines}.", ("kind", ClipboardKind(message)), ("machines", string.Join(", ", sentTo))));
    }

    private void Sharing_Click(object sender, RoutedEventArgs e)
    {
        SaveUserSettings(_settings with
        {
            ClipboardSend = ClipboardSendBox.IsChecked == true,
            ClipboardReceive = ClipboardReceiveBox.IsChecked == true,
            AcceptFiles = AcceptFilesBox.IsChecked == true,
        });
        if (_clipboard is not null)
            _clipboard.SendEnabled = _settings.ClipboardSend;
    }

    private void ChooseFilesFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.EffectiveFilesFolder };
        if (dialog.ShowDialog(this) != true)
            return;
        SaveUserSettings(_settings with { FilesFolder = dialog.FolderName });
        FilesFolderBox.Text = _settings.EffectiveFilesFolder;
    }

    private void OpenFilesFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.EffectiveFilesFolder);
        Process.Start(new ProcessStartInfo(_settings.EffectiveFilesFolder) { UseShellExecute = true })?.Dispose();
    }

    // ------------------------------------------------------------------ sending files

    /// <summary>The machines files can be sent to: every other one.</summary>
    private IReadOnlyList<MachineVm> OtherMachines() =>
        _vm.Model.Machines.Where(m => m.Id.Trim() != _vm.LocalMachine.Trim()).ToList();

    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        SendFilesMenu.Items.Clear();
        foreach (var machine in OtherMachines())
        {
            var item = new MenuItem { Header = machine.ToString() };
            item.Click += async (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("Send files to {machine}", ("machine", machine)) };
                if (dialog.ShowDialog(this) == true)
                    await SendFilesAsync(machine, dialog.FileNames);
            };
            SendFilesMenu.Items.Add(item);
        }
        if (SendFilesMenu.Items.Count == 0)
            SendFilesMenu.Items.Add(new MenuItem { Header = L.T("There are no other machines"), IsEnabled = false });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped files: a menu asks which machine they go to (never sent without that click).</summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped)
            return;
        var files = dropped.Where(File.Exists).ToArray();
        if (files.Length < dropped.Length)
            AddLog(L.T("Folders are not sent; send the files inside them."));
        if (files.Length == 0)
            return;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = L.T("Send {count} files to:", ("count", files.Length)), IsEnabled = false });
        foreach (var machine in OtherMachines())
        {
            var item = new MenuItem { Header = machine.ToString() };
            item.Click += async (_, _) => await SendFilesAsync(machine, files);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 1)
            menu.Items.Add(new MenuItem { Header = L.T("There are no other machines"), IsEnabled = false });
        menu.IsOpen = true;
    }

    private async Task SendFilesAsync(MachineVm machine, IReadOnlyList<string> files)
    {
        if (PeerContext() is not { } context)
        {
            MessageBox.Show(this, L.T("Set up This PC first."), "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var progress = new Progress<(string File, long Done, long Total)>(p =>
            _vm.Status = L.T("Sending {file} to {machine}: {percent}%",
                ("file", p.File), ("machine", machine), ("percent", p.Total == 0 ? 100 : p.Done * 100 / p.Total)));
        try
        {
            var saved = await Peers.SendFilesAsync(context.Config, context.Local, machine.Id.Trim(), files, progress, _agent?.Directory);
            var text = L.T("Sent {count} files to {machine}.", ("count", saved.Count), ("machine", machine));
            _vm.Status = text;
            AddLog(text);
        }
        catch (Exception ex) when (ex is ControlException or IOException or UnauthorizedAccessException)
        {
            _vm.Status = ex.Message;
            AddLog(ex.Message);
            MessageBox.Show(this, ex.Message, "DeskBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
