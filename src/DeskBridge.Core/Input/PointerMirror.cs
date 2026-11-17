using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;

namespace DeskBridge.Core.Input;

/// <summary>
/// The coordinator's copy of where the other machine puts the cursor: the same sums the other machine does
/// (<see cref="ClientHandover.Move"/>) on the same relative moves, so the pointer can be drawn over the video at
/// once instead of a network round trip later, the way Remote Desktop draws it on the client. Corrected with
/// the real position when the mouse rests (a program or the other machine's touchpad may have moved it).
/// </summary>
public sealed class PointerMirror
{
    private IReadOnlyList<DisplayRect> _displays = [];
    private IReadOnlyList<DisplayRect> _peers = [];
    private IReadOnlyList<DisplayRect>? _holes, _desk;
    private double _speedX = 1, _speedY = 1, _restX, _restY;
    private int _x, _y;

    /// <summary>Where the cursor is over there (that machine's coordinates, as the coordinator's layout has them), or null.</summary>
    public (int X, int Y)? Position { get; private set; }

    /// <summary>The cursor entered that machine (the values of the enter message sent to it).</summary>
    public void Enter(int x, int y, IReadOnlyList<DisplayRect> area, IReadOnlyList<DisplayRect> peers,
        double speedX, double speedY, IReadOnlyList<DisplayRect>? holes, IReadOnlyList<DisplayRect>? areaDesk)
    {
        if (area.Count == 0)
        {
            Clear();
            return;
        }
        _displays = area;
        _peers = peers;
        _holes = holes;
        _desk = areaDesk?.Count == area.Count ? areaDesk : null;
        (_speedX, _speedY) = (speedX, speedY);
        (_restX, _restY) = (0, 0);
        (_x, _y) = EdgeGeometry.ClampToDisplays(area, x, y);
        Position = (_x, _y);
    }

    /// <summary>A relative move sent over (coordinator pixels). False once it takes the cursor back out.</summary>
    /// <param name="dragging">A mouse button is held over there (see <see cref="ClientHandover.Move(IReadOnlyList{DisplayRect}, IReadOnlyList{DisplayRect}, int, int, int, int, IReadOnlyList{DisplayRect}?, IReadOnlyList{DisplayRect}?, bool)"/>).</param>
    public bool Move(int dx, int dy, bool dragging = false)
    {
        if (Position is null)
            return false;
        _restX += dx * _speedX;
        _restY += dy * _speedY;
        int mx = (int)Math.Truncate(_restX), my = (int)Math.Truncate(_restY);
        _restX -= mx;
        _restY -= my;
        var move = ClientHandover.Move(_displays, _peers, _x, _y, mx, my, _holes, _desk, dragging);
        if (move.Left)
        {
            Clear();
            return false;
        }
        (_x, _y) = (move.X, move.Y);
        Position = (_x, _y);
        return true;
    }

    /// <summary>The other machine reports the cursor somewhere else: carry on from there.</summary>
    public void Correct(int x, int y)
    {
        if (Position is null)
            return;
        (_x, _y) = EdgeGeometry.ClampToDisplays(_displays, x, y);
        Position = (_x, _y);
    }

    public void Clear() => Position = null;
}
