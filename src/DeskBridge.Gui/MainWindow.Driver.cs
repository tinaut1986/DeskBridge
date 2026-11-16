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
        // Only virtual displays that are really shown elsewhere (sending from the coordinator is not available yet).
        bool needed = here is not null && here != plan.Coordinator
                      && plan.Streams.Any(s => s.SourceMachine == here && s.SinkMachine == plan.Coordinator);
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
            if (!_driverNoticeShown)
            {
                _driverNoticeShown = true;
                Notify(L.T("This PC needs the Virtual Display Driver to create the displays the coordinator shows. Open DeskBridge to install it."));
            }
        });
    }

    private void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // A visible console: winget shows the licence and progress, and Windows asks for administrator rights.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k winget install --id={DriverWingetId} -e") { UseShellExecute = true });
            AddLog(L.T("Installing the Virtual Display Driver with winget..."));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            OpenExternal(DriverPage);
        }
        WatchForDriver();
    }

    private void DriverInfo_Click(object sender, RoutedEventArgs e) => OpenExternal(DriverPage);

    private void DriverLater_Click(object sender, RoutedEventArgs e) => DriverBanner.Visibility = Visibility.Collapsed;

    /// <summary>Checks every few seconds (for 15 minutes) until the driver answers, then puts the profile back in place.</summary>
    private void WatchForDriver()
    {
        _driverWatch?.Stop();
        var until = DateTime.UtcNow.AddMinutes(15);
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
        foreach (var (key, sender) in _videoSenders.ToArray())
        {
            _videoSenders.TryRemove(key, out _);
            await sender.DisposeAsync();
        }
    }
}
