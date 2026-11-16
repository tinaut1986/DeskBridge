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
    private IReadOnlyList<DisplayRect>? _areaDesk;

    public async Task HandleAsync(string from, ControlMessage message)
    {
        InputLeaveMessage? leave = null;
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
                    foreach (var e in events.Events)
                    {
                        if (e.Kind != InputKind.MouseMove)
                        {
                            Play(e);
                            continue;
                        }
                        _restX += e.X * _speedX;
                        _restY += e.Y * _speedY;
                        int mx = (int)Math.Truncate(_restX), my = (int)Math.Truncate(_restY);
                        _restX -= mx;
                        _restY -= my;
                        var move = _peers is { } peers
                            ? ClientHandover.Move(_displays, peers, _x, _y, mx, my, _holes, _areaDesk)
                            : ClientHandover.Move(_displays, _side, _x, _y, mx, my);
                        if (move.Left)
                        {
                            leave = _peers is null
                                ? new InputLeaveMessage(move.Fraction)
                                : new InputLeaveMessage(move.Fraction, move.X - _offset.X, move.Y - _offset.Y);
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

                default:
                    return;
            }
        }

        activeChanged(_from);
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
