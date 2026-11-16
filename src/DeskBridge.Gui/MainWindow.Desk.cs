using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBridge.Core.Input;
using DeskBridge.Core.Localization;
using DeskBridge.Core.Model;
using DeskBridge.Core.Planning;
using DeskBridge.Gui.ViewModels;

namespace DeskBridge.Gui;

/// <summary>
/// The Layout map: the coordinator's desk (its own monitors, and the displays of other machines placed on it,
/// which are used from it like more monitors) and, beside it, each other machine with the displays left apart.
/// Displays are dragged between the two. What each display shows in the selected profile is chosen in the
/// table below (and shown on each display).
/// </summary>
public partial class MainWindow
{
    /// <summary>How desk coordinates map onto the canvas (frozen while a drag lasts, so nothing jumps).</summary>
    private sealed record DeskFrame(double Left, double Top, int MinX, int MinY, double Scale)
    {
        public double ToCanvasX(int x) => Left + (x - MinX) * Scale;
        public double ToCanvasY(int y) => Top + (y - MinY) * Scale;
        public int ToDeskX(double canvasX) => (int)Math.Round((canvasX - Left) / Scale) + MinX;
        public int ToDeskY(double canvasY) => (int)Math.Round((canvasY - Top) / Scale) + MinY;
    }

    /// <summary>Snap distance on screen, in pixels of the map.</summary>
    private const double SnapPixels = 24;

    private Border? _deskDragging;

    private static DisplayRect RectOf(DisplayVm d) => new(d.X, d.Y, d.Width, d.Height);

    private void DrawLayout()
    {
        var canvas = LayoutCanvas;
        if (_deskDragging is not null)
            return; // redrawn when the drag ends
        canvas.Children.Clear();
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var coordinator = _vm.Model.Machines.FirstOrDefault(m => m.HasInput && m.Displays.Count > 0);
        if (coordinator is null)
        {
            var hint = new TextBlock
            {
                Text = L.T("Mark the machine with the keyboard and mouse, and detect its displays, to place the others around it."),
                Foreground = InfoBrush,
                TextWrapping = TextWrapping.Wrap,
                Width = width - 8,
            };
            canvas.Children.Add(hint);
            return;
        }

        // The desk: the coordinator's monitors and the displays of other machines placed on it.
        var desk = coordinator.Displays.Select(d => (Machine: coordinator, Display: d, Rect: RectOf(d))).ToList();
        var others = _vm.Model.Machines.Where(m => m != coordinator).ToList();
        foreach (var m in others)
            foreach (var d in m.Displays)
                if (m.DeskPositions.TryGetValue(d.Id.Trim(), out var p))
                    desk.Add((m, d, new DisplayRect(p.X, p.Y, d.Width, d.Height)));
        var deskBounds = InputLayout.Bounds(desk.Select(x => x.Rect).ToList());

        // Beside it, each machine with the displays left apart (in their own arrangement), one under another.
        int gap = Math.Max(deskBounds.Width, deskBounds.Height) / 12;
        int apartX = deskBounds.Right + gap * 2, apartY = deskBounds.Y;
        var apart = new List<(MachineVm Machine, DisplayVm Display, DisplayRect Rect)>();
        var apartLabels = new List<(MachineVm Machine, int X, int Y)>();
        foreach (var m in others)
        {
            var left = m.Displays.Where(d => !m.DeskPositions.ContainsKey(d.Id.Trim())).ToList();
            if (left.Count == 0)
                continue;
            var own = InputLayout.Bounds(left.Select(RectOf).ToList());
            int labelSpace = gap;
            apartLabels.Add((m, apartX, apartY));
            foreach (var d in left)
                apart.Add((m, d, new DisplayRect(apartX + d.X - own.X, apartY + labelSpace + d.Y - own.Y, d.Width, d.Height)));
            apartY += labelSpace + own.Height + gap;
        }

        var all = desk.Select(x => x.Rect).Concat(apart.Select(x => x.Rect)).ToList();
        var bounds = InputLayout.Bounds(all);
        const double labelHeight = 26;
        double scale = Math.Min((width - 8) / bounds.Width, (height - labelHeight - 8) / bounds.Height);
        if (scale <= 0)
            return;
        double left0 = (width - bounds.Width * scale) / 2;
        var frame = new DeskFrame(left0, labelHeight, bounds.X, bounds.Y, scale);

        // The desk's outline and title.
        var deskOutline = new Border
        {
            Width = deskBounds.Width * scale + 12,
            Height = deskBounds.Height * scale + 12,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(deskOutline, frame.ToCanvasX(deskBounds.X) - 6);
        Canvas.SetTop(deskOutline, frame.ToCanvasY(deskBounds.Y) - 6);
        canvas.Children.Add(deskOutline);
        AddLabel(canvas, L.T("{machine}: your desk (keyboard and mouse)", ("machine", coordinator)), ColorOf(coordinator),
            frame.ToCanvasX(deskBounds.X) - 4, 2);

        foreach (var (machine, x, y) in apartLabels)
            AddLabel(canvas, L.T("{machine}: apart", ("machine", machine)), ColorOf(machine),
                frame.ToCanvasX(x), frame.ToCanvasY(y));

        var selected = AssignmentGrid.SelectedItem as AssignmentVm;
        var deskRects = desk.Select(x => (x.Machine, x.Display, x.Rect)).ToList();
        foreach (var (machine, display, rect) in desk)
            AddDeskTile(canvas, frame, machine, display, rect, onDesk: true, coordinator, deskRects, deskBounds, selected);
        foreach (var (machine, display, rect) in apart)
            AddDeskTile(canvas, frame, machine, display, rect, onDesk: false, coordinator, deskRects, deskBounds, selected);
    }

    private static void AddLabel(Canvas canvas, string text, Color color, double x, double y)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private void AddDeskTile(Canvas canvas, DeskFrame frame, MachineVm machine, DisplayVm display, DisplayRect rect, bool onDesk,
        MachineVm coordinator, IReadOnlyList<(MachineVm Machine, DisplayVm Display, DisplayRect Rect)> desk, DisplayRect deskBounds,
        AssignmentVm? selected)
    {
        var color = ColorOf(machine);
        var assignment = _vm.SelectedProfile?.Assignments.FirstOrDefault(a => a.Owner == machine && a.Display == display);
        var shows = assignment?.UsedBy ?? machine;
        var showsColor = ColorOf(shows);
        bool foreign = machine != coordinator;
        bool isSelected = assignment is not null && assignment == selected;

        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = foreign ? $"{machine} · {display.Id}" : display.Id,
            FontSize = foreign ? 14 : 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (foreign && onDesk)
        {
            text.Children.Add(new TextBlock
            {
                Text = L.T("virtual on {machine}", ("machine", coordinator)),
                FontSize = 11,
                Foreground = InfoBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = shows == machine ? L.T("shows itself") : L.T("shows {machine}", ("machine", shows)),
            FontWeight = shows == machine ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = shows == machine ? InfoBrush : new SolidColorBrush(showsColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = shows != machine && assignment is not null ? StreamLabel(assignment) : $"{display.Width}x{display.Height}",
            FontSize = 11,
            Foreground = InfoBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var tile = new Border
        {
            Width = Math.Max(rect.Width * frame.Scale - 6, 10),
            Height = Math.Max(rect.Height * frame.Scale - 6, 10),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(shows == machine ? (byte)0x22 : (byte)0x55, showsColor.R, showsColor.G, showsColor.B)),
            BorderBrush = isSelected ? new SolidColorBrush(Colors.Black) : onDesk ? new SolidColorBrush(color) : DashedBrush(color),
            BorderThickness = new Thickness(isSelected ? 3 : 1.5),
            Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) },
            Cursor = foreign ? Cursors.SizeAll : Cursors.Hand,
            ToolTip = foreign
                ? (onDesk
                    ? L.T("Display {display} of {machine}, on the desk of {coordinator}: used from there like one more monitor. Drag it away to take it off.", ("display", display.Id), ("machine", machine), ("coordinator", coordinator))
                    : L.T("Display {display} of {machine}: drag it onto the desk of {coordinator} to use it from there.", ("display", display.Id), ("machine", machine), ("coordinator", coordinator)))
                : L.T("Display {display} of {machine}: {mode}", ("display", display.Id), ("machine", machine), ("mode", display.Summary)),
        };
        Canvas.SetLeft(tile, frame.ToCanvasX(rect.X) + 3);
        Canvas.SetTop(tile, frame.ToCanvasY(rect.Y) + 3);
        canvas.Children.Add(tile);

        if (foreign)
            MakeDeskDraggable(tile, machine, display, rect, onDesk, desk, deskBounds, frame, assignment);
        else
            tile.MouseLeftButtonDown += (_, _) => SelectAssignment(assignment);
    }

    private void SelectAssignment(AssignmentVm? assignment)
    {
        if (assignment is null)
            return;
        AssignmentGrid.SelectedItem = assignment;
        AssignmentGrid.ScrollIntoView(assignment);
    }

    private void MakeDeskDraggable(Border tile, MachineVm machine, DisplayVm display, DisplayRect start, bool onDesk,
        IReadOnlyList<(MachineVm Machine, DisplayVm Display, DisplayRect Rect)> desk, DisplayRect deskBounds, DeskFrame frame, AssignmentVm? assignment)
    {
        Point grab = default, down = default;
        bool moved = false;
        DisplayRect target = start;
        bool toDesk = onDesk;
        // What it snaps against: everything on the desk but itself.
        var fixedOnes = desk.Where(x => !(x.Machine == machine && x.Display == display)).Select(x => x.Rect).ToList();

        tile.MouseLeftButtonDown += (_, e) =>
        {
            _deskDragging = tile;
            grab = e.GetPosition(tile);
            down = e.GetPosition(LayoutCanvas);
            moved = false;
            tile.CaptureMouse();
            Panel.SetZIndex(tile, 10);
            e.Handled = true;
        };

        tile.MouseMove += (_, e) =>
        {
            if (_deskDragging != tile)
                return;
            var p = e.GetPosition(LayoutCanvas);
            if (!moved && (p - down).Length < 4)
                return; // a click, not a drag
            moved = true;
            var dropped = start with { X = frame.ToDeskX(p.X - grab.X - 3), Y = frame.ToDeskY(p.Y - grab.Y - 3) };
            // Near the desk (its centre within a display's size of it): snaps onto it, against its displays.
            int cx = dropped.X + dropped.Width / 2, cy = dropped.Y + dropped.Height / 2;
            toDesk = cx > deskBounds.X - dropped.Width / 2 && cx < deskBounds.Right + dropped.Width / 2
                     && cy > deskBounds.Y - dropped.Height / 2 && cy < deskBounds.Bottom + dropped.Height / 2;
            target = toDesk ? Arrangement.Snap(dropped, fixedOnes, (int)Math.Ceiling(SnapPixels / frame.Scale)) : dropped;
            if (toDesk && fixedOnes.Any(target.Overlaps))
                toDesk = false; // no free spot there
            Canvas.SetLeft(tile, frame.ToCanvasX(target.X) + 3);
            Canvas.SetTop(tile, frame.ToCanvasY(target.Y) + 3);
            tile.Opacity = toDesk ? 1 : 0.6;
        };

        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_deskDragging != tile)
                return;
            _deskDragging = null;
            tile.ReleaseMouseCapture();
            e.Handled = true;
            if (!moved)
            {
                SelectAssignment(assignment);
                DrawLayout();
                return;
            }
            // On the desk at that spot, or back apart.
            machine.PlaceOnDesk(display.Id.Trim(), toDesk ? new VirtualPosition(target.X, target.Y) : null); // redraws
            DrawLayout();
        };

        if (onDesk)
        {
            var remove = new MenuItem { Header = L.T("Take off the desk") };
            remove.Click += (_, _) => machine.PlaceOnDesk(display.Id.Trim(), null);
            tile.ContextMenu = new ContextMenu { Items = { remove } };
        }
    }
}
