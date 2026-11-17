using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Platform.Windows.VirtualDisplays;

namespace DeskBridge.Gui;

/// <summary>
/// The Virtual Display Driver: when the applied profile asks this machine for virtual displays that another
/// machine shows, and the driver is missing, a banner (and a notification) offers to install it with winget.
/// Once it is there, the profile is put in place again here and the streams start over with the real virtual
/// displays.
/// </summary>
public partial class MainWindow
{
    private const string DriverWingetId = "VirtualDrivers.Virtual-Display-Driver";
    private const string DriverPage = "https://github.com/VirtualDrivers/Virtual-Display-Driver";

    private DispatcherTimer? _driverWatch;
    private bool _driverNoticeShown;

    /// <summary>After a profile is applied here (from the agent's thread): is the driver needed and missing?</summary>
    private void CheckVirtualDisplayDriver(LayoutPlan plan)
    {
        var here = _agent?.Machine;
        // Virtual displays of this PC that another machine shows: a client's on the coordinator, or the
        // coordinator's on another machine (e.g. a laptop used as one more monitor of the desktop).
        bool needed = here is not null && plan.Streams.Any(s => s.SourceMachine == here && s.SinkMachine != here);
        if (!needed)
        {
            Dispatcher.BeginInvoke(() => DriverBanner.Visibility = Visibility.Collapsed);
            return;
        }
        if (MttVirtualDisplays.IsInstalled())
            return;
        Dispatcher.BeginInvoke(() =>
        {
            DriverBanner.Visibility = Visibility.Visible;
            WatchForDriver(); // installed, enabled again or restarted by hand: noticed within seconds
            if (!_driverNoticeShown)
            {
                _driverNoticeShown = true;
                Notify(here == plan.Coordinator
                    ? L.T("This PC needs the Virtual Display Driver to create the displays the other machines show. Open DeskBridge to install it.")
                    : L.T("This PC needs the Virtual Display Driver to create the displays the coordinator shows. Open DeskBridge to install it."));
            }
        });
    }

    /// <summary>
    /// The winget package is the driver's control app (VDD Control, portable); the driver itself is installed from
    /// it with its Install Driver button. So: download it if needed, then open it.
    /// </summary>
    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        WatchForDriver();
        if (FindDriverControl() is { } existing)
        {
            OpenDriverControl(existing);
            return;
        }
        try
        {
            // A visible console: winget shows the licence and progress; it stays open only if something fails.
            using var winget = Process.Start(new ProcessStartInfo("cmd.exe", $"/c winget install --id={DriverWingetId} -e || pause")
            {
                UseShellExecute = true,
            });
            AddLog(L.T("Downloading the Virtual Display Driver's control app with winget..."));
            if (winget is not null)
                await winget.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        if (FindDriverControl() is { } downloaded)
            OpenDriverControl(downloaded);
        else
            OpenExternal(DriverPage); // no winget, or it failed: the project page has the downloads
    }

    private void OpenDriverControl(string exe)
    {
        AddLog(L.T("Opening VDD Control: press Install Driver there (Windows asks for administrator rights)."));
        _vm.Status = L.T("In VDD Control, press Install Driver.");
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AddLog(ex.Message);
        }
    }

    /// <summary>VDD Control as winget leaves it (a portable app under %LOCALAPPDATA%\Microsoft\WinGet\Packages).</summary>
    private static string? FindDriverControl()
    {
        var packages = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        try
        {
            return System.IO.Directory.Exists(packages)
                ? System.IO.Directory.EnumerateDirectories(packages, DriverWingetId + "*")
                    .SelectMany(d => System.IO.Directory.EnumerateFiles(d, "*.exe", System.IO.SearchOption.AllDirectories))
                    .OrderByDescending(f => System.IO.Path.GetFileName(f).Contains("Control", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => System.IO.Path.GetFileName(f).Contains("VDD", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DriverInfo_Click(object sender, RoutedEventArgs e) => OpenExternal(DriverPage);

    private void DriverLater_Click(object sender, RoutedEventArgs e) => DriverBanner.Visibility = Visibility.Collapsed;

    /// <summary>Checks every few seconds (for 15 minutes) until the driver answers, then puts the profile back in place.</summary>
    private void WatchForDriver()
    {
        _driverWatch?.Stop();
        var until = DateTime.UtcNow.AddHours(12);
        _driverWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _driverWatch.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow > until)
            {
                _driverWatch?.Stop();
                return;
            }
            if (!await Task.Run(MttVirtualDisplays.IsInstalled))
                return;
            _driverWatch?.Stop();
            DriverBanner.Visibility = Visibility.Collapsed;
            AddLog(L.T("The Virtual Display Driver is installed: creating the virtual displays."));
            await DriverReadyAsync();
        };
        _driverWatch.Start();
    }

    /// <summary>
    /// Puts the applied profile in place again (now with real virtual displays) and stops what this machine sends:
    /// the coordinator asks for those streams again a moment later, now of the virtual displays.
    /// </summary>
    private async Task DriverReadyAsync()
    {
        if (_agent is not { } agent || !await agent.ReapplyAsync())
            return;
        if (IsCoordinatorHere())
        {
            // Its own streams to the other machines: each one stopped is started again by itself a moment later
            // (see TryStartOutgoing), now of the virtual display.
            foreach (var sender in _videoSenders.Values.ToArray())
                await sender.DisposeAsync();
            return;
        }
        foreach (var (key, sender) in _videoSenders.ToArray())
        {
            _videoSenders.TryRemove(key, out _);
            await sender.DisposeAsync();
        }
    }
}
