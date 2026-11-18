using System.Collections.Concurrent;
using System.Text.Json;
using DeskBridge.Core.Configuration;
using DeskBridge.Core.Control;
using DeskBridge.Core.Input;
using DeskBridge.Core.Planning;
using DeskBridge.Platform.Windows.Video;

namespace DeskBridge.Gui;

/// <summary>
/// A machine's own windows on top of the coordinator's picture on its display (the laptop panel showing the
/// desktop): they stay where they are, and the cursor goes to that machine over them, as it comes back to the
/// coordinator over the coordinator's windows on top of the laptop's picture there. That machine draws its own
/// pointer over them, so they can be used, dragged and closed in place.
/// <para>
/// The machine that shows the picture tells the coordinator where they are (<see cref="WindowsOverVideoMessage"/>);
/// the coordinator turns each into an island: a place on its own display (the one it sends there) where the cursor
/// goes to that machine, and comes back past its edges.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>On a machine that shows screens of the coordinator: each stream, its coordinator and where it is shown here.</summary>
    private readonly ConcurrentDictionary<string, (string Coordinator, int X, int Y)> _coordinatorScreens = new();

    /// <summary>What was last sent for each of them (sent again only when it changes).</summary>
    private readonly ConcurrentDictionary<string, string> _coveredSent = new();

    /// <summary>On the coordinator: the windows each machine has over the picture of each stream it shows.</summary>
    private readonly ConcurrentDictionary<(string Machine, string Stream), WindowsOverVideoMessage> _islands = new();

    /// <summary>
    /// On a machine that shows screens of the coordinator (from the timer that watches covered displays): its
    /// windows on top of each of them, to the coordinator, when they changed.
    /// </summary>
    private void ReportWindowsOverVideo()
    {
        var (ox, oy) = DeskOffset();
        foreach (var (stream, (coordinator, x, y)) in _coordinatorScreens)
        {
            if (VideoWindow.CoveredOn(x, y) is not { } covered)
                continue;
            // In this PC's original arrangement, as the coordinator's layout has its displays.
            var parts = covered.Parts
                .Select(p => new CoveredPart(
                    new DisplayRect(p.Here.X - ox, p.Here.Y - oy, p.Here.W, p.Here.H),
                    new DisplayRect(p.Picture.X, p.Picture.Y, p.Picture.W, p.Picture.H)))
                .ToList();
            var message = new WindowsOverVideoMessage(stream, covered.PictureWidth, covered.PictureHeight, parts);
            string text = JsonSerializer.Serialize(message, ConfigLoader.JsonOptions);
            if (_coveredSent.TryGetValue(stream, out var sent) && sent == text)
                continue;
            if (SessionOf(coordinator) is not { } session)
                continue;
            _coveredSent[stream] = text;
            _ = NotifyCoveredAsync(session, message, stream);
        }
    }

    private async Task NotifyCoveredAsync(ControlSession session, WindowsOverVideoMessage message, string stream)
    {
        try
        {
            await session.NotifyAsync(message);
        }
        catch (ControlException)
        {
            _coveredSent.TryRemove(stream, out _); // sent again next time
        }
    }

    /// <summary>A stream of the coordinator is shown here on the display at (x, y), or no longer (null).</summary>
    private void TrackCoordinatorScreen(string stream, string coordinator, (int X, int Y)? at)
    {
        _coveredSent.TryRemove(stream, out _);
        if (at is { } p)
            _coordinatorScreens[stream] = (coordinator, p.X, p.Y);
        else
            _coordinatorScreens.TryRemove(stream, out _);
    }

    /// <summary>On the coordinator (agent thread): where a machine's windows are over the picture it shows.</summary>
    private void OnWindowsOverVideo(string from, WindowsOverVideoMessage message)
    {
        int before = _islands.TryGetValue((from, message.Stream), out var old) ? old.Parts.Count : 0;
        if (message.Parts.Count == 0)
            _islands.TryRemove((from, message.Stream), out _);
        else
            _islands[(from, message.Stream)] = message;
        if (before != message.Parts.Count)
            AddLog($"[input] {from}: {message.Parts.Count} of its windows over the picture of {message.Stream} (the cursor goes to it over them)");
        Dispatcher.BeginInvoke(RefreshInputTargets);
    }

    /// <summary>
    /// The islands, for the cursor: each window of another machine over the picture of a stream this PC sends to it,
    /// placed on this PC's display that the stream sends (where it is now).
    /// </summary>
    private List<InputTarget> IslandTargets(HashSet<string> connected)
    {
        var result = new List<InputTarget>();
        string here = _vm.LocalMachine.Trim();
        var all = Platform.Windows.WindowsDisplayDetector.DetectAll();
        foreach (var ((machine, key), covered) in _islands)
        {
            if (!connected.Contains(machine) || covered.PictureWidth <= 0 || covered.PictureHeight <= 0)
                continue;
            if (_streamPlan?.Streams.FirstOrDefault(s => s.Key == key && s.SinkMachine == machine && s.SourceMachine == here) is not { } stream)
                continue; // not sent any more
            if (SendPosition(stream.SourceVirtualDisplay) is not { Mirror: false } at
                || all.FirstOrDefault(d => d.X == at.X && d.Y == at.Y) is not { } display)
                continue;
            double sx = (double)display.Width / covered.PictureWidth, sy = (double)display.Height / covered.PictureHeight;
            foreach (var part in covered.Parts)
            {
                var p = part.Picture;
                var onThisPc = new DisplayRect(at.X + (int)Math.Round(p.X * sx), at.Y + (int)Math.Round(p.Y * sy),
                    Math.Max(1, (int)Math.Round(p.Width * sx)), Math.Max(1, (int)Math.Round(p.Height * sy)));
                if (part.Here.Width > 0 && part.Here.Height > 0)
                    result.Add(InputLayout.Lent(machine, onThisPc, part.Here, island: true));
            }
        }
        return result;
    }
}
