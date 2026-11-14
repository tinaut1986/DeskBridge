using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Gui.ViewModels;

namespace DeskBridge.Gui;

/// <summary>Dragging virtual displays in the Layout map to place them by hand.</summary>
public partial class MainWindow
{
    /// <summary>How a machine's desktop maps onto the canvas while a drag lasts (frozen, so nothing jumps).</summary>
    private sealed record DragFrame(double Left, double Top, int MinX, int MinY, double Scale)
    {
        public double ToCanvasX(int x) => Left + (x - MinX) * Scale + 3;
        public double ToCanvasY(int y) => Top + (y - MinY) * Scale + 3;
        public int ToDesktopX(double canvasX) => (int)Math.Round((canvasX - 3 - Left) / Scale) + MinX;
        public int ToDesktopY(double canvasY) => (int)Math.Round((canvasY - 3 - Top) / Scale) + MinY;
    }

    /// <summary>Snap distance on screen, in pixels of the map.</summary>
    private const double SnapPixels = 24;

    private Border? _dragging;

    private void MakeDraggable(Border rect, AssignmentVm assignment, VirtualDisplaySpec spec, IReadOnlyList<DisplayRect> others, DragFrame frame)
    {
        rect.Cursor = Cursors.SizeAll;
        Point grab = default, start = default;
        bool moved = false;
        DisplayRect target = new(spec.X, spec.Y, spec.Width, spec.Height);

        rect.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = rect;
            grab = e.GetPosition(rect);
            start = e.GetPosition(LayoutCanvas);
            moved = false;
            rect.CaptureMouse();
            Panel.SetZIndex(rect, 10);
            e.Handled = true;
        };

        rect.MouseMove += (_, e) =>
        {
            if (_dragging != rect)
                return;
            var p = e.GetPosition(LayoutCanvas);
            if (!moved && (p - start).Length < 4)
                return; // a click, not a drag
            moved = true;

            var dropped = new DisplayRect(frame.ToDesktopX(p.X - grab.X), frame.ToDesktopY(p.Y - grab.Y), spec.Width, spec.Height);
            target = Arrangement.Snap(dropped, others, (int)Math.Ceiling(SnapPixels / frame.Scale));
            Canvas.SetLeft(rect, frame.ToCanvasX(target.X));
            Canvas.SetTop(rect, frame.ToCanvasY(target.Y));
            rect.ToolTip = $"({target.X}, {target.Y})";
        };

        rect.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragging != rect)
                return;
            _dragging = null;
            rect.ReleaseMouseCapture();
            e.Handled = true;
            if (moved)
                assignment.Position = new VirtualPosition(target.X, target.Y); // redraws the map and the plan
        };

        var automatic = new MenuItem { Header = L.T("Place automatically"), IsEnabled = assignment.Position is not null };
        automatic.Click += (_, _) => assignment.Position = null;
        rect.ContextMenu = new ContextMenu { Items = { automatic } };
    }
}
