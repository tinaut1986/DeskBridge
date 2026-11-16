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
        UpdateFrames();
    }

    private void UpdateFrames()
    {
        bool on = _settings.ShowFrames && _options.SnapshotDir is null;
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
            _frames[gone].Close();
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

    private void CloseFrames()
    {
        _framesTimer?.Stop();
        foreach (var frame in _frames.Values)
            frame.Close();
        _frames.Clear();
    }

    private Color ColorOfId(string machine) =>
        _vm.Model.Machines.FirstOrDefault(m => m.Id.Trim() == machine) is { } vm ? ColorOf(vm) : MachineColors[0];

    private static (float R, float G, float B) Floats(Color c) => (c.R / 255f, c.G / 255f, c.B / 255f);
}
