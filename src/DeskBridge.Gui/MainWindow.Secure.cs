using System.IO;
using System.Windows;
using DeskBridge.Core.Localization;
using DeskBridge.Gui.Secure;
using DeskBridge.Gui.Updates;

namespace DeskBridge.Gui;

/// <summary>
/// The option to install the Windows service that reaches the secure desktop (lock screen, administrator
/// prompts): see <see cref="SecureService"/>. Its state is the service itself, not a setting.
/// </summary>
public partial class MainWindow
{
    private SecureLink? _secureLink;
    private bool _secureInstalled;

    private void InitSecure()
    {
        if (_options.SnapshotDir is not null)
            return;
        _secureInstalled = SecureService.IsInstalled();
        UpdateSecureUi();
        if (_secureInstalled && Elevation.IsElevated && Environment.ProcessPath is { } exe)
        {
            // A new version of DeskBridge (an update) refreshes the service's copy, then talks to it.
            _ = Task.Run(() =>
            {
                SecureService.RefreshIfOutdated(exe, AppInfo.Version.ToString(), AddLog);
                Dispatcher.BeginInvoke(StartSecureLink);
            });
        }
    }

    private void StartSecureLink()
    {
        _secureLink ??= new SecureLink(AddLog);
        _secureLink.DesktopChanged += desktop =>
        {
            AddLog($"[secure] input desktop: {desktop}");
            Dispatcher.BeginInvoke(() =>
            {
                UpdateSecureUi();
                UpdateSecureVideo();
            });
        };
    }

    private readonly Dictionary<Platform.Windows.Video.VideoSender, SecureVideoRelay> _secureRelays = [];

    /// <summary>
    /// While the secure desktop has the input, every display this PC sends comes from the service's helper (see
    /// <see cref="SecureVideoRelay"/>); DeskBridge's own capture takes over again afterwards.
    /// </summary>
    private void UpdateSecureVideo()
    {
        bool secure = _secureLink is { OnSecureDesktop: true };
        var live = _videoSenders.Values.ToHashSet();
        foreach (var (sender, relay) in _secureRelays.ToArray())
        {
            if (!secure || !live.Contains(sender))
            {
                _secureRelays.Remove(sender);
                _ = relay.DisposeAsync().AsTask();
            }
        }
        if (secure && _secureLink is { } link)
            foreach (var sender in live)
                if (!_secureRelays.ContainsKey(sender))
                    _secureRelays[sender] = new SecureVideoRelay(link, sender, AddLog);
    }

    private void UpdateSecureUi()
    {
        SecureBox.IsChecked = _secureInstalled;
        SecureStatusText.Text = !_secureInstalled ? ""
            : !Elevation.IsElevated ? L.T("Installed, but DeskBridge must run as administrator to use it.")
            : _secureLink?.Desktop is null ? L.T("Installed; waiting for the service.")
            : L.T("Installed and working.");
    }

    private void Secure_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = SecureBox.IsChecked == true;
        if (Environment.ProcessPath is not { } exe)
            return;
        if (wanted && !Elevation.IsElevated)
        {
            // Only an elevated DeskBridge may talk to the helper (see SecureHelper): without that it would be useless.
            AddLog(L.T("First turn on \"Run as administrator\": only DeskBridge running as administrator can use the service."));
            SecureBox.IsChecked = false;
            return;
        }
        SecureBox.IsEnabled = false;
        _ = Task.Run(() =>
        {
            try
            {
                if (wanted)
                    SecureService.Install(exe, AddLog);
                else
                {
                    _secureLink?.Dispose();
                    _secureLink = null;
                    SecureService.Uninstall(AddLog);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddLog($"[secure] {ex.Message}");
            }
            Dispatcher.BeginInvoke(() =>
            {
                _secureInstalled = SecureService.IsInstalled();
                if (_secureInstalled)
                    StartSecureLink();
                SecureBox.IsEnabled = true;
                UpdateSecureUi();
            });
        });
    }

    /// <summary>The last lines the service and its helper wrote (they log as SYSTEM, in ProgramData), for remote debugging.</summary>
    private static string SecureLogTail(int lines = 80)
    {
        try
        {
            var file = Directory.Exists(FileLog.SecureDirectory)
                ? Directory.EnumerateFiles(FileLog.SecureDirectory, "secure-*.log").OrderBy(File.GetLastWriteTime).LastOrDefault()
                : null;
            if (file is null)
                return "No log from the service yet.";
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return string.Join('\n', reader.ReadToEnd().Split('\n').TakeLast(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
