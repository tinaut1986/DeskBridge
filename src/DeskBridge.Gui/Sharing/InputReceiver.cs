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
public sealed class InputReceiver(
    Func<bool> enabled,
    Func<IReadOnlyList<DisplayRect>> displays,
    Func<string, ControlSession?> sessionOf,
    Action<string?> activeChanged)
{
    private readonly InputInjector _injector = new();
    private readonly object _gate = new();
    private string? _from;
    private PlacementSide _side;
    private IReadOnlyList<DisplayRect> _displays = [];
    private IReadOnlyList<DisplayRect>? _peers; // the coordinator's displays here (null: older coordinator, by side)
    private int _x, _y;
    private double _speedX = 1, _speedY = 1, _restX, _restY;

    // In a Remote Desktop session the pointer DeskBridge moves is not drawn by the client: draw our own.
    private PointerOverlay? _overlay;

    private void ShowPointer()
    {
        if (_overlay is null && PointerOverlay.Needed)
            _overlay = new PointerOverlay();
        _overlay?.MoveTo(_x, _y);
    }

    private void HidePointer() => _overlay?.Hide();

    public async Task HandleAsync(string from, ControlMessage message)
    {
        InputLeaveMessage? leave = null;
        lock (_gate)
        {
            switch (message)
            {
                case InputEnterMessage enter when enabled():
                    // Only the display shown on the coordinator, when it entered through one; otherwise all of them.
                    _displays = enter.Area is { Count: > 0 } area ? area : displays();
                    if (_displays.Count == 0)
                        return;
                    _from = from;
                    _side = enter.Side;
                    _peers = enter.Peers;
                    // Moves arrive in the coordinator's pixels: scaled to this display (keeping the fractions).
                    (_speedX, _speedY) = (enter.SpeedX ?? 1, enter.SpeedY ?? 1);
                    (_restX, _restY) = (0, 0);
                    (_x, _y) = enter is { X: { } ex, Y: { } ey, Peers: not null }
                        ? EdgeGeometry.ClampToDisplays(_displays, ex, ey)
                        : ClientHandover.Enter(_displays, enter.Side, enter.Fraction);
                    InputInjector.MoveTo(_x, _y);
                    ShowPointer();
                    break;

                case InputEventsMessage events when _from == from:
                    // Start from where the cursor really is (the touchpad may have moved it meanwhile).
                    (_x, _y) = InputInjector.CursorPosition();
                    foreach (var e in events.Events)
                    {
                        if (e.Kind != InputKind.MouseMove)
                        {
                            _injector.Play(e);
                            continue;
                        }
                        _restX += e.X * _speedX;
                        _restY += e.Y * _speedY;
                        int mx = (int)Math.Truncate(_restX), my = (int)Math.Truncate(_restY);
                        _restX -= mx;
                        _restY -= my;
                        var move = _peers is { } peers
                            ? ClientHandover.Move(_displays, peers, _x, _y, mx, my)
                            : ClientHandover.Move(_displays, _side, _x, _y, mx, my);
                        if (move.Left)
                        {
                            leave = _peers is null ? new InputLeaveMessage(move.Fraction) : new InputLeaveMessage(move.Fraction, move.X, move.Y);
                            _injector.ReleaseAll();
                            _from = null;
                            HidePointer();
                            break;
                        }
                        (_x, _y) = (move.X, move.Y);
                        InputInjector.MoveTo(_x, _y);
                        ShowPointer();
                    }
                    break;

                case InputReleaseMessage when _from == from:
                    _injector.ReleaseAll();
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
