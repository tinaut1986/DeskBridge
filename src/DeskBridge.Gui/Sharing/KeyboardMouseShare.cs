using DeskBridge.Core.Control;
using DeskBridge.Core.Input;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Platform.Windows.Input;
using DeskBridge.Platform.Windows.Video;

namespace DeskBridge.Gui.Sharing;

/// <summary>
/// The coordinator's keyboard and mouse sharing on Windows: global hooks watch the cursor; when it touches
/// the edge on the side where a connected machine is, the cursor "moves" there: local input is swallowed and
/// sent to that machine (in order, mouse moves merged) until it comes back, Ctrl+Alt+Home is pressed, or the
/// connection drops.
/// </summary>
public sealed class KeyboardMouseShare : IDisposable
{
    private const int VkControl = 0x11, VkLControl = 0xA2, VkRControl = 0xA3;
    private const int VkMenu = 0x12, VkLMenu = 0xA4, VkRMenu = 0xA5, VkHome = 0x24;

    private abstract record Item;
    private sealed record EnterItem(InputEnterMessage Message) : Item;
    private sealed record EventItem(InputEvent Event) : Item;
    private sealed record ReleaseItem : Item;

    private readonly object _gate = new();
    private readonly CoordinatorHandover _logic;
    private readonly Func<string, ControlSession?> _sessionOf;
    private readonly Action<string> _log;
    private readonly Action<string?> _remoteChanged;
    private readonly List<Item> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<int> _localDown = [];
    private readonly HashSet<int> _modifiersDown = [];
    private readonly GlobalInputHook _hook;
    private readonly Task _sender;
    private string? _target; // where queued items go
    private int _localButtons; // mouse buttons held on this PC (bit per button)
    private long _settleUntil;
    private int _parkedX, _parkedY; // where the local cursor waits while the other machine has it
    private int _settleFromX, _settleFromY;
    private Task _pointerChain = Task.CompletedTask;

    /// <summary>
    /// Hides or shows this PC's pointer off the hook's thread (swapping system cursors takes a while, and the hook
    /// must answer at once), in the order asked.
    /// </summary>
    private void Pointer(bool hide)
    {
        lock (_gate)
            _pointerChain = _pointerChain.ContinueWith(_ =>
            {
                if (hide)
                    CursorHider.Hide();
                else
                    CursorHider.Show();
            }, TaskScheduler.Default);
    }

    /// <summary>The cursor came back: ignore, for a moment, moves that still come from where it was parked.</summary>
    private void Settle(int parkX, int parkY)
    {
        _settleFromX = parkX;
        _settleFromY = parkY;
        _settleUntil = Environment.TickCount64 + 150;
    }

    public KeyboardMouseShare(
        IReadOnlyList<DisplayRect> displays,
        IReadOnlyList<InputTarget> targets,
        Func<string, ControlSession?> sessionOf,
        Action<string> log,
        Action<string?> remoteChanged)
    {
        _logic = new CoordinatorHandover(displays, targets)
        {
            WindowsOverVideo = () => VideoWindow.WindowsAbove().Select(w => new DisplayRect(w.X, w.Y, w.Width, w.Height)).ToList(),
        };
        _sessionOf = sessionOf;
        _log = log;
        _remoteChanged = remoteChanged;
        _hook = new GlobalInputHook(OnMouse, OnKey);
        _sender = Task.Run(SendLoopAsync);
    }

    /// <summary>New displays or machines (called periodically).</summary>
    public void Update(IReadOnlyList<DisplayRect> displays, IReadOnlyList<InputTarget> targets)
    {
        string? lost = null;
        lock (_gate)
        {
            var before = _logic.Remote;
            _logic.Update(displays, targets);
            if (before is not null && _logic.Remote is null)
                lost = before;
        }
        if (lost is not null)
            CameBack(lost, reason: null);
    }

    private bool OnMouse(HookedMouse m)
    {
        if (m.Injected)
            return false;
        lock (_gate)
        {
            if (m.Message == NativeMessages.MouseMove)
            {
                // Just back here: moves still in flight from the old park point (where the cursor waited, maybe over
                // a display showing the other machine) must not send it straight back there.
                if (_logic.Remote is null && Environment.TickCount64 < _settleUntil
                    && Math.Abs(m.X - _settleFromX) < 64 && Math.Abs(m.Y - _settleFromY) < 64)
                    return true;
                // A button held here (a window or a file being dragged on this PC): the cursor stays here, over
                // displays showing another machine too, so the drag can go across them or be dropped on them.
                // Also over a window of this PC that is on top of the video there (one dropped on that display, the
                // taskbar): it stays usable from here; only over the video does the cursor go to the other machine.
                bool stay = _logic.Remote is null
                            && (_localButtons != 0 || (_logic.IsOverLent(m.X, m.Y) && !VideoWindow.IsVideoAt(m.X, m.Y)));
                switch (_logic.OnMouseMove(m.X, m.Y, dragging: stay))
                {
                    case MouseDecision.Enter enter:
                        InputInjector.SetCursor(enter.ParkX, enter.ParkY);
                        (_parkedX, _parkedY) = (enter.ParkX, enter.ParkY);
                        _target = enter.Machine;
                        Enqueue(new EnterItem(new InputEnterMessage(enter.Side, enter.Fraction, enter.X, enter.Y, enter.Peers, enter.Area, enter.SpeedX, enter.SpeedY, enter.Holes)));
                        var how = enter.Area is null ? "past the edge" : "onto its display (on the desk, or shown here)";
                        Task.Run(() => _log($"[input] keyboard and mouse to {enter.Machine} at ({enter.X}, {enter.Y}), {how}"));
                        Pointer(hide: true); // not left in sight here while the other machine has the cursor
                        _remoteChanged(enter.Machine);
                        return true;
                    case MouseDecision.Forward forward:
                        EnqueueMove(forward.Dx, forward.Dy);
                        return true;
                    default:
                        return _logic.Remote is not null; // a move to the park point itself
                }
            }
            if (_logic.Remote is null)
            {
                _localButtons = m.Message switch
                {
                    NativeMessages.LeftDown => _localButtons | 1,
                    NativeMessages.LeftUp => _localButtons & ~1,
                    NativeMessages.RightDown => _localButtons | 2,
                    NativeMessages.RightUp => _localButtons & ~2,
                    NativeMessages.MiddleDown => _localButtons | 4,
                    NativeMessages.MiddleUp => _localButtons & ~4,
                    _ => _localButtons,
                };
                return false;
            }

            var e = m.Message switch
            {
                NativeMessages.LeftDown or NativeMessages.LeftUp => new InputEvent(InputKind.MouseButton, Code: 0, Down: m.Message == NativeMessages.LeftDown),
                NativeMessages.RightDown or NativeMessages.RightUp => new InputEvent(InputKind.MouseButton, Code: 1, Down: m.Message == NativeMessages.RightDown),
                NativeMessages.MiddleDown or NativeMessages.MiddleUp => new InputEvent(InputKind.MouseButton, Code: 2, Down: m.Message == NativeMessages.MiddleDown),
                NativeMessages.XDown or NativeMessages.XUp => new InputEvent(InputKind.MouseButton, Code: (m.Data >> 16) == 1 ? 3 : 4, Down: m.Message == NativeMessages.XDown),
                NativeMessages.Wheel => new InputEvent(InputKind.Wheel, X: (short)(m.Data >> 16)),
                NativeMessages.HWheel => new InputEvent(InputKind.Wheel, X: (short)(m.Data >> 16), Extended: true),
                _ => null,
            };
            if (e is not null)
                Enqueue(new EventItem(e));
            return true;
        }
    }

    private bool OnKey(HookedKey k)
    {
        if (k.Injected)
            return false;
        bool isModifier = k.VirtualKey is VkControl or VkLControl or VkRControl or VkMenu or VkLMenu or VkRMenu;
        string? cameBack = null;
        lock (_gate)
        {
            if (isModifier)
            {
                if (k.Down)
                    _modifiersDown.Add(k.VirtualKey);
                else
                    _modifiersDown.Remove(k.VirtualKey);
            }

            if (_logic.Remote is null)
            {
                if (k.Down)
                    _localDown.Add(k.VirtualKey);
                else
                    _localDown.Remove(k.VirtualKey);
                return false;
            }

            // Ctrl+Alt+Home: take the cursor back, whatever the other machine does.
            bool ctrl = _modifiersDown.Overlaps([VkControl, VkLControl, VkRControl]);
            bool alt = _modifiersDown.Overlaps([VkMenu, VkLMenu, VkRMenu]);
            if (k.Down && k.VirtualKey == VkHome && ctrl && alt)
            {
                cameBack = _logic.Remote;
                var (x, y) = _logic.ReturnHere();
                Settle(_parkedX, _parkedY);
                InputInjector.SetCursor(x, y);
                Pointer(hide: false);
                Enqueue(new ReleaseItem());
            }
            else if (!k.Down && _localDown.Remove(k.VirtualKey))
            {
                return false; // pressed here before the cursor left: its release belongs here too
            }
            else
            {
                Enqueue(new EventItem(new InputEvent(InputKind.Key, Code: k.ScanCode, Down: k.Down, Extended: k.Extended)));
                return true;
            }
        }
        if (cameBack is not null)
            _remoteChanged(null);
        return true;
    }

    /// <summary>The machine that had the cursor sent it back (it reached the edge facing this PC).</summary>
    public void OnLeave(string from, InputLeaveMessage leave)
    {
        lock (_gate)
        {
            if (_logic.Remote != from)
                return;
            switch (_logic.Back(leave.Fraction, leave.X, leave.Y))
            {
                case MouseDecision.Enter next:
                    // Straight on to another display placed on the desk (maybe of the same machine).
                    _target = next.Machine;
                    Enqueue(new EnterItem(new InputEnterMessage(next.Side, next.Fraction, next.X, next.Y, next.Peers, next.Area, next.SpeedX, next.SpeedY, next.Holes)));
                    Task.Run(() => _log($"[input] keyboard and mouse from {from} on to {next.Machine} at ({next.X}, {next.Y})"));
                    if (next.Machine != from)
                        _remoteChanged(next.Machine);
                    return;
                case MouseDecision.Here here:
                    Settle(_parkedX, _parkedY);
                    InputInjector.SetCursor(here.X, here.Y);
                    Pointer(hide: false);
                    Task.Run(() => _log($"[input] keyboard and mouse back from {from}, at ({here.X}, {here.Y})"));
                    break;
            }
        }
        _remoteChanged(null);
    }

    private void CameBack(string machine, string? reason)
    {
        if (reason is not null)
            _log(reason);
        lock (_gate)
            _logic.ForceLocal();
        Pointer(hide: false);
        _remoteChanged(null);
    }

    private void EnqueueMove(int dx, int dy)
    {
        if (_queue.Count > 0 && _queue[^1] is EventItem { Event: { Kind: InputKind.MouseMove } last })
            _queue[^1] = new EventItem(last with { X = last.X + dx, Y = last.Y + dy });
        else
            _queue.Add(new EventItem(new InputEvent(InputKind.MouseMove, X: dx, Y: dy)));
        _signal.Release();
    }

    private void Enqueue(Item item)
    {
        _queue.Add(item);
        _signal.Release();
    }

    private async Task SendLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            List<Item> items;
            string? target;
            lock (_gate)
            {
                items = [.. _queue];
                _queue.Clear();
                target = _target;
            }
            if (target is null)
                continue;

            var session = _sessionOf(target);
            if (session is null)
            {
                if (_logic.Remote == target)
                    CameBack(target, L.T("The connection with {machine} was lost: the keyboard and mouse are back on this PC.", ("machine", target)));
                continue;
            }

            try
            {
                var events = new List<InputEvent>();
                foreach (var item in items)
                {
                    if (item is EventItem ev)
                    {
                        events.Add(ev.Event);
                        continue;
                    }
                    if (events.Count > 0)
                    {
                        await session.NotifyAsync(new InputEventsMessage([.. events]), ct);
                        events.Clear();
                    }
                    await session.NotifyAsync(item is EnterItem enter ? enter.Message : new InputReleaseMessage(), ct);
                }
                if (events.Count > 0)
                    await session.NotifyAsync(new InputEventsMessage(events), ct);
                await Task.Delay(3, ct); // at most a few hundred messages a second; moves merge meanwhile
            }
            catch (ControlException)
            {
                CameBack(target, L.T("The connection with {machine} was lost: the keyboard and mouse are back on this PC.", ("machine", target)));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        string? remote;
        lock (_gate)
            remote = _logic.Remote;
        if (remote is not null && _sessionOf(remote) is { } session)
            _ = session.NotifyAsync(new InputReleaseMessage());
        _hook.Dispose();
        Pointer(hide: false);
        _stop.Cancel();
        try
        {
            _sender.Wait(1000);
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}
