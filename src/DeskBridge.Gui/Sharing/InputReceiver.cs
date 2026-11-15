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
    private int _x, _y;

    public async Task HandleAsync(string from, ControlMessage message)
    {
        double? leave = null;
        lock (_gate)
        {
            switch (message)
            {
                case InputEnterMessage enter when enabled():
                    _displays = displays();
                    if (_displays.Count == 0)
                        return;
                    _from = from;
                    _side = enter.Side;
                    (_x, _y) = ClientHandover.Enter(_displays, enter.Side, enter.Fraction);
                    InputInjector.MoveTo(_x, _y);
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
                        var move = ClientHandover.Move(_displays, _side, _x, _y, e.X, e.Y);
                        if (move.Left)
                        {
                            leave = move.Fraction;
                            _injector.ReleaseAll();
                            _from = null;
                            break;
                        }
                        (_x, _y) = (move.X, move.Y);
                        InputInjector.MoveTo(_x, _y);
                    }
                    break;

                case InputReleaseMessage when _from == from:
                    _injector.ReleaseAll();
                    _from = null;
                    break;

                default:
                    return;
            }
        }

        activeChanged(_from);
        if (leave is { } fraction && sessionOf(from) is { } session)
        {
            try
            {
                await session.NotifyAsync(new InputLeaveMessage(fraction));
            }
            catch (ControlException)
            {
                // the coordinator takes the cursor back by itself when the connection drops
            }
        }
    }
}
