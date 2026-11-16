using System.ComponentModel;
using System.Windows;
using DeskBridge.Core.Localization;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DeskBridge.Gui;

/// <summary>
/// Notification area icon: closing the window keeps DeskBridge (and its agent) running there, which is
/// how it lives on the laptop. Also "Start with Windows" (per-user Run key, started minimized).
/// </summary>
public partial class MainWindow
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DeskBridge";

    private Forms.NotifyIcon? _tray;
    private bool _quitting;
    private bool _trayHintShown;

    private void InitTray()
    {
        if (_options.SnapshotDir is not null)
            return;
        _tray = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "DeskBridge",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
        _adminTask = Elevation.TaskExists();
        FollowAutostart();
        UpdateAdminUi();
        CloseToTrayMenu.IsChecked = _settings.CloseToTray;
    }

    /// <summary>The sign-in task that starts DeskBridge as administrator exists (it replaces "Start with Windows").</summary>
    private bool _adminTask;

    private void UpdateAdminUi()
    {
        RunAsAdminBox.IsChecked = _adminTask;
        AdminStatusText.Text = Elevation.IsElevated
            ? L.T("DeskBridge is running as administrator now.")
            : L.T("DeskBridge is running without administrator rights now.");
        // The task starts it at sign-in already: no second start from the Run key.
        StartWithWindowsMenu.IsChecked = _adminTask || StartsWithWindows();
        StartWithWindowsMenu.IsEnabled = !_adminTask;
    }

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = RunAsAdminBox.IsChecked == true;
        if (Environment.ProcessPath is not { } exe)
            return;
        AddLog(wanted ? "[admin] creating the sign-in task (Windows asks for permission on this PC)"
                      : "[admin] deleting the sign-in task (Windows asks for permission on this PC)");
        bool started = wanted ? Elevation.Enable(exe, AddLog) : Elevation.Disable(exe, AddLog);
        if (!started)
        {
            RunAsAdminBox.IsChecked = _adminTask;
            return;
        }
        if (wanted)
            RemoveRunKey();
        else
            SetRunKey(exe);
        // The task (or Explorer) starts the new copy in a few seconds, once this one has closed.
        Quit();
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
        }
    }

    private static void SetRunKey(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{exe}\" --minimized");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
        }
    }

    /// <summary>
    /// "Start with Windows" follows the app to wherever it now runs from: after an update installed by the
    /// coordinator (a new file under %LOCALAPPDATA%\DeskBridge\bin), or when the registered file is gone.
    /// A copy started by hand from elsewhere does not take it over.
    /// </summary>
    private void FollowAutostart()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        if (_adminTask)
        {
            RemoveRunKey(); // started by the sign-in task instead
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is not string command || command.Contains(exe, StringComparison.OrdinalIgnoreCase))
                return;
            var registered = command.StartsWith('"') ? command[1..].Split('"')[0] : command.Split(' ')[0];
            bool afterUpdate = Environment.GetCommandLineArgs().Contains("--after-update");
            if (afterUpdate || !System.IO.File.Exists(registered))
                key.SetValue(RunValue, $"\"{exe}\" --minimized");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
        }
    }

    private void BuildTrayMenu()
    {
        var menu = _tray!.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(L.T("Open DeskBridge"), null, (_, _) => ShowFromTray());
        menu.Items[0].Font = new Drawing.Font(menu.Items[0].Font, Drawing.FontStyle.Bold);

        // Profiles are applied from the coordinator only.
        if (IsCoordinatorHere() && _vm.Model.Profiles.Count > 0)
        {
            var apply = new Forms.ToolStripMenuItem(L.T("Apply profile"));
            foreach (var profile in _vm.Model.Profiles)
            {
                var p = profile;
                apply.DropDownItems.Add(p.Name, null, (_, _) =>
                {
                    _vm.SelectedProfile = p;
                    _ = ApplySelectedProfileAsync();
                });
            }
            menu.Items.Add(apply);
        }

        var send = new Forms.ToolStripMenuItem(L.T("Send files to"));
        bool ready = PeerContext() is not null;
        foreach (var machine in OtherMachines())
        {
            var m = machine;
            var item = send.DropDownItems.Add(m.ToString(), null, async (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("Send files to {machine}", ("machine", m)) };
                if (dialog.ShowDialog() == true)
                    await SendFilesAsync(m, dialog.FileNames);
            });
            item.Enabled = ready;
            item.ToolTipText = ready ? null : L.T("Set up Connection and security first.");
        }
        send.Enabled = send.DropDownItems.Count > 0;
        menu.Items.Add(send);

        // Always reachable, even when a full-screen stream covers the display with DeskBridge's window.
        var stopVideo = new Forms.ToolStripMenuItem(L.T("Stop showing other machines' screens"), null, (_, _) =>
        {
            ForgetApplied();
            _ = StopProfileStreamsAsync().ContinueWith(_ => Dispatcher.InvokeAsync(RestorePrimary), TaskScheduler.Default);
        })
        {
            Enabled = !_videoReceivers.IsEmpty,
        };
        menu.Items.Add(stopVideo);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Quit"), null, (_, _) => Quit());
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closing hides the window when "close to the notification area" is on; Quit really quits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting && _tray is not null && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Notify(L.T("DeskBridge keeps running in the notification area."));
            }
            return;
        }
        base.OnClosing(e);
    }

    private void Quit()
    {
        _quitting = true;
        Close();
        Application.Current.Shutdown();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    private void DisposeTray()
    {
        if (_tray is null)
            return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    /// <summary>Tells the user about something that happened while the window may be hidden.</summary>
    private void Notify(string text)
    {
        if (_tray is not null && (!IsVisible || WindowState == WindowState.Minimized))
            _tray.ShowBalloonTip(4000, "DeskBridge", text, Forms.ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ settings

    private void CloseToTray_Click(object sender, RoutedEventArgs e) =>
        SaveUserSettings(_settings with { CloseToTray = CloseToTrayMenu.IsChecked == true });

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindowsMenu.IsChecked == true && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\" --minimized");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            AddLog(ex.Message);
        }
        StartWithWindowsMenu.IsChecked = StartsWithWindows();
    }

    /// <summary>Registered for this very exe (a copy elsewhere does not count).</summary>
    private static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string command && Environment.ProcessPath is { } exe
               && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The app's icon at the size the notification area uses (the .ico has one drawn for each size).</summary>
    private static Drawing.Icon CreateIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/deskbridge.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
