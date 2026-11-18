using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBridge.Core.Control;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Platform;
using DeskBridge.Gui.ViewModels;

namespace DeskBridge.Gui;

/// <summary>
/// Detect on the Layout tab: every machine's displays read again (this PC's from Windows, the others' from their
/// agents), and each real display shows for a few seconds which one it is, with its machine's name and colour,
/// the way Windows' Identify does (display 1 of one machine is never taken for display 1 of another).
/// </summary>
public partial class MainWindow
{
    private readonly List<Window> _identifyWindows = [];
    private DispatcherTimer? _identifyTimer;

    private async void DetectAll_Click(object sender, RoutedEventArgs e)
    {
        if (IsClientHere())
            return; // only the coordinator edits the layout
        var button = (Button)sender;
        button.IsEnabled = false;
        _vm.Status = L.T("Detecting the displays of every machine...");
        var failed = new List<string>();
        try
        {
            RefreshLocalDisplays();
            string here = _vm.LocalMachine.Trim();
            foreach (var machine in _vm.Model.Machines.Where(m => m.Id.Trim() != here).ToList())
            {
                if (SessionOf(machine.Id.Trim()) is not { } session)
                    continue; // not connected: it keeps the displays it had
                try
                {
                    if (await session.RequestAsync(new GetDisplaysMessage()) is DisplaysMessage displays)
                        MergeDisplays(machine, displays.Displays);
                }
                catch (Exception ex) when (ex is ControlException or IOException or TimeoutException or OperationCanceledException)
                {
                    failed.Add(machine.ToString());
                    AddLog($"{machine}: {ex.Message}");
                }
            }
            await SaveAndSyncAsync();
            await IdentifyEverywhereAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
        int off = _vm.Model.Machines.Sum(m => m.Displays.Count(d => d.Disconnected));
        _vm.Status = failed.Count > 0
            ? L.T("Displays detected; these machines did not answer: {machines}.", ("machines", string.Join(", ", failed)))
            : off > 0
                ? L.T("Displays detected. {count} not connected now: kept for when they come back.", ("count", off))
                : L.T("Displays detected.");
        ScheduleRedraw();
    }

    /// <summary>Each machine that is connected shows on each of its displays which one it is.</summary>
    private async Task IdentifyEverywhereAsync()
    {
        string here = _vm.LocalMachine.Trim();
        foreach (var machine in _vm.Model.Machines)
        {
            var labels = machine.Displays.Where(d => !d.Disconnected)
                .Select(d => new DisplayLabel(d.Id.Trim(), machine.ToString(), Hex(ColorOf(machine)))).ToList();
            if (labels.Count == 0)
                continue;
            var message = new IdentifyDisplaysMessage(labels);
            if (machine.Id.Trim() == here)
            {
                ShowIdentify(message);
                continue;
            }
            if (SessionOf(machine.Id.Trim()) is not { } session)
                continue;
            try
            {
                await session.RequestAsync(message);
            }
            catch (Exception ex) when (ex is ControlException or IOException or TimeoutException or OperationCanceledException)
            {
                AddLog($"{machine}: {ex.Message}"); // an older version: it only misses the numbers
            }
        }
    }

    /// <summary>On the UI thread: a number on each of this PC's displays, in its machine's colour, for a few seconds.</summary>
    private void ShowIdentify(IdentifyDisplaysMessage message)
    {
        CloseIdentify();
        var displays = PlatformServices.DisplayDetector().DetectDisplays(); // where they are now, in Windows' pixels
        foreach (var label in message.Labels)
        {
            if (displays.FirstOrDefault(d => d.Id == label.Display) is not { } display)
                continue;
            var color = ParseColor(label.Color) ?? Color.FromRgb(0x40, 0x40, 0x40);
            var text = new StackPanel { Margin = new Thickness(32, 14, 32, 18) };
            text.Children.Add(new TextBlock
            {
                Text = label.Machine,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            text.Children.Add(new TextBlock
            {
                Text = label.Display,
                FontSize = 120,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -16, 0, -8),
            });
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(color),
                Content = text,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Title = "DeskBridge",
            };
            window.Show();
            // Top-left of that display, in physical pixels (its size follows that display's scale once there).
            SetWindowPos(new WindowInteropHelper(window).Handle, -1, display.X + 48, display.Y + 48, 0, 0, 0x0001 | 0x0010 | 0x0040);
            _identifyWindows.Add(window);
        }
        _identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(message.Seconds, 1, 15)) };
        _identifyTimer.Tick += (_, _) => CloseIdentify();
        _identifyTimer.Start();
    }

    private void CloseIdentify()
    {
        _identifyTimer?.Stop();
        _identifyTimer = null;
        foreach (var window in _identifyWindows)
            window.Close();
        _identifyWindows.Clear();
    }
}
