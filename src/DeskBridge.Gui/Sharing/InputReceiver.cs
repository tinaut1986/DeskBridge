using DeskBridge.Core.Control;
using DeskBridge.Core.Input;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Platform.Windows.Input;

namespace DeskBridge.Gui.Sharing;

/// <summary>
/// The machine that gets the cursor: plays the coordinator's keyboard and mouse here and, when the cursor
/// goes back past the edge facing the coordinator, gives it back (releasing everything still held).
/// </summary>
/// <param name="offset">
/// Where this PC's original arrangement's (0,0) is now (moved when DeskBridge made another display primary): the
/// coordinator's coordinates are in the original arrangement, as its layout has this PC's displays.
/// </param>
public sealed class InputReceiver(
    Func<bool> enabled,
    Func<IReadOnlyList<DisplayRect>> displays,
    Func<string, ControlSession?> sessionOf,
    Action<string?> activeChanged,
    Func<(int X, int Y)>? offset = null,
    Func<Secure.SecureLink?>? secure = null)
{
    /// <summary>The lock screen or an administrator prompt has the input, and the service's helper can play there.</summary>
    private Secure.SecureLink? SecureDesktop() => secure?.Invoke() is { OnSecureDesktop: true } link ? link : null;

    private void MoveCursor(int x, int y)
    {
        if (SecureDesktop() is { } link)
            link.MoveTo(x, y);
        else
            InputInjector.MoveTo(x, y);
    }

    private void Play(InputEvent e)
    {
        if (SecureDesktop() is { } link)
            link.Play(e);
        else
            _injector.Play(e);
    }

    private void ReleaseEverything()
    {
        _injector.ReleaseAll();
        secure?.Invoke()?.ReleaseAll();
    }

    /// <summary>
    /// The button held for a drag was let go on the coordinator (or the coordinator is gone): let go of it here,
    /// a little inside the edge where the cursor left, so Windows does not snap or maximize the window there.
    /// </summary>
    private void DropAway()
    {
        _heldFor = null;
        _buttons = 0;
        if (_displays.Count > 0)
        {
            var d = _displays.FirstOrDefault(r => _x >= r.X && _x < r.Right && _y >= r.Y && _y < r.Bottom);
            if (d.Width > 2 * EdgeInset && d.Height > 2 * EdgeInset)
                MoveCursor(Math.Clamp(_x, d.X + EdgeInset, d.Right - 1 - EdgeInset), Math.Clamp(_y, d.Y + EdgeInset, d.Bottom - 1 - EdgeInset));
        }
        ReleaseEverything();
    }

    /// <summary>How far from the edge a drag is dropped when its button is let go on the coordinator.</summary>
    private const int EdgeInset = 24;

    /// <summary>
    /// The coordinator is no longer connected: let go of everything it held here (it cannot send the release).
    /// </summary>
    public void ReleaseIfGone(Func<string, bool> connected)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_heldFor is { } held && !connected(held))
                DropAway();
            if (_from is { } from && !connected(from))
            {
                ReleaseEverything();
                _from = null;
                _buttons = 0;
                HidePointer();
                changed = true;
            }
        }
        if (changed)
            activeChanged(null);
    }

    private (int X, int Y) _offset;

    private IReadOnlyList<DisplayRect>? Shift(IReadOnlyList<DisplayRect>? rects) =>
        rects is null || _offset == (0, 0) ? rects : rects.Select(r => r with { X = r.X + _offset.X, Y = r.Y + _offset.Y }).ToList();

    private readonly InputInjector _injector = new();
    private readonly object _gate = new();
    private string? _from;
    private PlacementSide _side;
    private IReadOnlyList<DisplayRect> _displays = [];
    private IReadOnlyList<DisplayRect>? _peers; // the coordinator's displays here (null: older coordinator, by side)
    private int _x, _y;
    private double _speedX = 1, _speedY = 1, _restX, _restY;
    private IReadOnlyList<DisplayRect>? _holes; // parts the coordinator keeps (its windows over the video)
    private int _buttons; // mouse buttons the coordinator holds here (bit per button): a drag jumps gaps
    private bool _holdsDrag; // the coordinator keeps a drag going when the cursor goes back mid-drag
    private string? _heldFor; // the cursor went back mid-drag: buttons kept held for that coordinator

    // In a Remote Desktop session the pointer DeskBridge moves is not drawn by the client: draw our own.
    private PointerOverlay? _overlay;

    private void ShowPointer()
    {
        if (CursorHider.Hidden)
            CursorHider.Show();
        if (_overlay is null && PointerOverlay.Needed)
            _overlay = new PointerOverlay();
        _overlay?.MoveTo(_x, _y);
    }

    /// <summary>
    /// The cursor went back to the coordinator: this PC's pointer is hidden too (it would stay frozen where it
    /// was, in sight on the coordinator's video of this display), until it is used again here or there.
    /// </summary>
    private void HidePointer()
    {
        _overlay?.Hide();
        if (PointerOverlay.Needed)
            return; // a Remote Desktop session draws no pointer of ours anyway
        CursorHider.Hide();
        // Someone using this PC's own mouse or touchpad gets the pointer back at once.
        _localWatch ??= new GlobalInputHook(m =>
        {
            if (!m.Injected && CursorHider.Hidden)
                CursorHider.Show();
            return false;
        }, _ => false);
    }

    private GlobalInputHook? _localWatch;
    private long? _lastSeq;
    private long _lastReport;

    /// <summary>At most this often the coordinator hears where the cursor really is (see InputCursorMessage).</summary>
    private const int ReportMs = 100;
    private IReadOnlyList<DisplayRect>? _areaDesk;

    public async Task HandleAsync(string from, ControlMessage message)
    {
        InputLeaveMessage? leave = null;
        InputCursorMessage? report = null;
        lock (_gate)
        {
            switch (message)
            {
                case InputEnterMessage enter when enabled():
                    _offset = offset?.Invoke() ?? (0, 0);
                    // Only the display shown on the coordinator, when it entered through one; otherwise all of them.
                    _displays = Shift(enter.Area) is { Count: > 0 } area ? area : displays();
                    if (_displays.Count == 0)
                        return;
                    _from = from;
                    _side = enter.Side;
                    _peers = Shift(enter.Peers);
                    // Moves arrive in the coordinator's pixels: scaled to this display (keeping the fractions).
                    (_speedX, _speedY) = (enter.SpeedX ?? 1, enter.SpeedY ?? 1);
                    _holes = Shift(enter.Holes);
                    _areaDesk = enter.Area is { Count: > 0 } && enter.AreaDesk?.Count == enter.Area.Count ? enter.AreaDesk : null;
                    (_restX, _restY) = (0, 0);
                    _holdsDrag = enter.HoldsDrag;
                    // Back mid-drag: the buttons are still held, the window still follows the cursor.
                    if (_heldFor != from)
                        _buttons = 0;
                    _heldFor = null;
                    (_x, _y) = enter is { X: { } ex, Y: { } ey, Peers: not null }
                        ? EdgeGeometry.ClampToDisplays(_displays, ex + _offset.X, ey + _offset.Y)
                        : ClientHandover.Enter(_displays, enter.Side, enter.Fraction);
                    MoveCursor(_x, _y);
                    ShowPointer();
                    break;

                case InputEventsMessage events when _from == from:
                    // Start from where the cursor really is (the touchpad may have moved it meanwhile); on the secure
                    // desktop this process cannot read it: from where it was put.
                    if (SecureDesktop() is null)
                        (_x, _y) = InputInjector.CursorPosition();
                    // Where the previous batch really left it (its moves are played by now), for the coordinator's pointer.
                    if (_lastSeq is long done && Environment.TickCount64 - _lastReport >= ReportMs)
                    {
                        _lastReport = Environment.TickCount64;
                        report = new InputCursorMessage(done, _x - _offset.X, _y - _offset.Y);
                    }
                    if (events.Seq is not null)
                        _lastSeq = events.Seq;
                    foreach (var e in events.Events)
                    {
                        if (e.Kind != InputKind.MouseMove)
                        {
                            if (e.Kind == InputKind.MouseButton)
                                _buttons = e.Down ? _buttons | (1 << e.Code) : _buttons & ~(1 << e.Code);
                            Play(e);
                            continue;
                        }
                        _restX += e.X * _speedX;
                        _restY += e.Y * _speedY;
                        int mx = (int)Math.Truncate(_restX), my = (int)Math.Truncate(_restY);
                        _restX -= mx;
                        _restY -= my;
                        var move = _peers is { } peers
                            ? ClientHandover.Move(_displays, peers, _x, _y, mx, my, _holes, _areaDesk, dragging: _buttons != 0)
                            : ClientHandover.Move(_displays, _side, _x, _y, mx, my);
                        if (move.Left)
                        {
                            leave = _peers is null
                                ? new InputLeaveMessage(move.Fraction)
                                : new InputLeaveMessage(move.Fraction, move.X - _offset.X, move.Y - _offset.Y);
                            if (_buttons != 0 && _holdsDrag)
                                _heldFor = from; // the coordinator lets go of it later, or brings the cursor back
                            else
                                ReleaseEverything();
                            _from = null;
                            HidePointer();
                            break;
                        }
                        (_x, _y) = (move.X, move.Y);
                        MoveCursor(_x, _y);
                        ShowPointer();
                    }
                    break;

                case InputReleaseMessage when _from == from:
                    ReleaseEverything();
                    _from = null;
                    HidePointer();
                    break;

                case InputReleaseMessage when _heldFor == from:
                    DropAway();
                    break;

                default:
                    return;
            }
        }

        activeChanged(_from);
        if (report is not null && leave is null && sessionOf(from) is { } reportTo)
        {
            try
            {
                await reportTo.NotifyAsync(report);
            }
            catch (ControlException)
            {
            }
        }
        if (leave is not null && sessionOf(from) is { } session)
        {
            try
            {
                await session.NotifyAsync(leave);
            }
            catch (ControlException)
            {
                // the coordinator takes the cursor back by itself when the connection drops
            }
        }
    }
}
