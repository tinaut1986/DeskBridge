using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBridge.Core.Planning;
using DeskBridge.Gui.Sharing;
using DeskBridge.Platform;

namespace DeskBridge.Gui;

/// <summary>
/// "Mark each display with the colour of its machine" (General): a display that shows another machine gets a
/// border in that machine's colour, drawn by its video window; every other display of this PC, one in this
/// PC's colour. The colours are those of the Layout map (the same on every machine: the shared layout's order).
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<(int X, int Y), ScreenFrame> _frames = new();
    private DispatcherTimer? _framesTimer;

    private void StartFrames()
    {
        if (_options.SnapshotDir is not null)
            return;
        _framesTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => UpdateFrames(), Dispatcher);
        _framesTimer.Start();
        // Whatever comes to the front (the taskbar when it is clicked puts itself above other always-on-top
        // windows) leaves the borders under it: back on top at once, and again a moment later, once it has settled.
        _foregroundProc = OnForegroundChanged; // kept in a field: Windows calls it for as long as the hook lasts
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _foregroundProc, 0, 0, WineventOutOfContext);
        UpdateFrames();
    }

    private nint _foregroundHook;
    private WinEventProc? _foregroundProc;
    private DispatcherTimer? _raiseAgain;

    private void RaiseFrames()
    {
        foreach (var frame in _frames.Values)
            frame.Raise();
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_frames.Count == 0)
            return;
        RaiseFrames();
        _raiseAgain ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _raiseAgain!.Stop();
            RaiseFrames();
        }, Dispatcher);
        _raiseAgain.Stop();
        _raiseAgain.Start();
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EventSystemForeground = 0x3, WineventOutOfContext = 0x0;

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    private void UpdateFrames()
    {
        // Only while another machine is connected: on its own, this PC has nothing to tell its displays apart from.
        bool on = _settings.ShowFrames && _options.SnapshotDir is null && OthersConnected();
        var covered = new HashSet<(int X, int Y)>();

        // Displays showing another machine: their video windows draw the border.
        foreach (var receiver in _videoReceivers.Values)
        {
            receiver.FrameColor = on ? Floats(ColorOfId(receiver.RemoteMachine)) : null;
            if (receiver.Monitor is { } m)
                covered.Add((m.X, m.Y));
        }

        var wanted = on
            ? PlatformServices.DisplayDetector().DetectDisplays()
                .Where(d => !covered.Contains((d.X, d.Y)))
                .Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height))
                .ToList()
            : [];
        var color = ColorOfId(_vm.LocalMachine.Trim());
        foreach (var gone in _frames.Keys.Where(k => wanted.All(d => (d.X, d.Y) != k)).ToList())
        {
            _frames[gone].Dispose();
            _frames.Remove(gone);
        }
        foreach (var display in wanted)
        {
            if (!_frames.TryGetValue((display.X, display.Y), out var frame))
                _frames[(display.X, display.Y)] = frame = new ScreenFrame();
            frame.Place(display, color);
        }
    }

    private void ShowFrames_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SaveUserSettings(_settings with { ShowFrames = ShowFramesBox.IsChecked == true });
        UpdateFrames();
    }

    private void VideoAtOnce_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SaveUserSettings(_settings with { VideoAtOnce = VideoAtOnceBox.IsChecked == true });
        Platform.Windows.Video.VideoWindow.Immediate = _settings.VideoAtOnce;
    }

    private void CloseFrames()
    {
        _framesTimer?.Stop();
        _raiseAgain?.Stop();
        if (_foregroundHook != 0)
            UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
        foreach (var frame in _frames.Values)
            frame.Dispose();
        _frames.Clear();
    }

    private Color ColorOfId(string machine) =>
        _vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == machine) is { } vm ? ColorOf(vm) : MachineColors[0];

    private static (float R, float G, float B) Floats(Color c) => (c.R / 255f, c.G / 255f, c.B / 255f);
}
