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
/// The mouse map in the Machines tab: the coordinator's displays and, around them, every other machine's,
/// which can be dragged anywhere (the cursor crosses where they touch).
/// </summary>
public partial class MainWindow
{
    private Canvas? _mouseDragging;

    private void MouseCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMouseMap();

    private static List<DisplayRect> RectsOf(MachineVm m) => m.Displays.Select(d => new DisplayRect(d.X, d.Y, d.Width, d.Height)).ToList();

    private void DrawMouseMap()
    {
        var canvas = MouseCanvas;
        if (_mouseDragging is not null)
            return; // redrawn when the drag ends
        canvas.Children.Clear();
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var coordinator = _vm.Model.Machines.FirstOrDefault(m => m.HasInput && m.Displays.Count > 0);
        if (coordinator is null)
        {
            var hint = new TextBlock { Text = L.T("Mark the machine with the keyboard and mouse, and detect its displays, to place the others around it."), Foreground = InfoBrush };
            Canvas.SetLeft(hint, 4);
            Canvas.SetTop(hint, 4);
            canvas.Children.Add(hint);
            return;
        }

        var local = RectsOf(coordinator);
        var others = _vm.Model.Machines
            .Where(m => m != coordinator && m.Displays.Count > 0)
            .Select(m => (Machine: m, Target: InputLayout.Target(m.Id.Trim(), local, RectsOf(m), m.InputSide, m.InputPosition)))
            .ToList();

        var all = local.Concat(others.SelectMany(o => o.Target.Placed)).ToList();
        var bounds = InputLayout.Bounds(all);
        double scale = Math.Min((width - 8) / bounds.Width, (height - 8) / bounds.Height);
        if (scale <= 0)
            return;
        double left = (width - bounds.Width * scale) / 2 - 3, top = (height - bounds.Height * scale) / 2 - 3;
        var frame = new DragFrame(left, top, bounds.X, bounds.Y, scale);

        foreach (var (display, rect) in coordinator.Displays.Zip(local))
            canvas.Children.Add(MapDisplay(coordinator, display, rect, frame, origin: (0, 0), absolute: true));

        foreach (var (machine, target) in others)
        {
            var placed = InputLayout.Bounds(target.Placed);
            var group = new Canvas
            {
                Width = placed.Width * scale,
                Height = placed.Height * scale,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = L.T("{machine}: drag it anywhere around the coordinator’s displays", ("machine", machine)),
            };
            foreach (var (display, rect) in machine.Displays.Zip(target.Placed))
                group.Children.Add(MapDisplay(machine, display, rect, frame, origin: (placed.X, placed.Y), absolute: false));
            Canvas.SetLeft(group, frame.ToCanvasX(placed.X) - 3);
            Canvas.SetTop(group, frame.ToCanvasY(placed.Y) - 3);
            canvas.Children.Add(group);

            var fixedOnes = local.Concat(others.Where(o => o.Machine != machine).Select(o => InputLayout.Bounds(o.Target.Placed))).ToList();
            MakeMouseDraggable(group, machine, placed, fixedOnes, frame);
        }
    }

    /// <summary>One display box; inside a machine's group, positioned relative to the group's corner.</summary>
    private Border MapDisplay(MachineVm machine, DisplayVm display, DisplayRect rect, DragFrame frame, (int X, int Y) origin, bool absolute)
    {
        var color = ColorOf(machine);
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = machine.ToString(),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{display.Id} · {display.Width}x{display.Height}",
            FontSize = 11,
            Foreground = InfoBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var border = new Border
        {
            Width = Math.Max(rect.Width * frame.Scale - 6, 10),
            Height = Math.Max(rect.Height * frame.Scale - 6, 10),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(machine.HasInput ? (byte)0x22 : (byte)0x44, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1.5),
            Child = new Viewbox { Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(4) },
            IsHitTestVisible = absolute,
        };
        if (absolute)
        {
            Canvas.SetLeft(border, frame.ToCanvasX(rect.X));
            Canvas.SetTop(border, frame.ToCanvasY(rect.Y));
        }
        else
        {
            Canvas.SetLeft(border, (rect.X - origin.X) * frame.Scale + 3);
            Canvas.SetTop(border, (rect.Y - origin.Y) * frame.Scale + 3);
        }
        return border;
    }

    private void MakeMouseDraggable(Canvas group, MachineVm machine, DisplayRect placed, IReadOnlyList<DisplayRect> others, DragFrame frame)
    {
        Point grab = default, start = default;
        bool moved = false;
        var target = placed;

        group.MouseLeftButtonDown += (_, e) =>
        {
            _mouseDragging = group;
            grab = e.GetPosition(group);
            start = e.GetPosition(MouseCanvas);
            moved = false;
            group.CaptureMouse();
            Panel.SetZIndex(group, 10);
            e.Handled = true;
        };

        group.MouseMove += (_, e) =>
        {
            if (_mouseDragging != group)
                return;
            var p = e.GetPosition(MouseCanvas);
            if (!moved && (p - start).Length < 4)
                return; // a click, not a drag
            moved = true;
            var dropped = placed with { X = frame.ToDesktopX(p.X - grab.X + 3), Y = frame.ToDesktopY(p.Y - grab.Y + 3) };
            target = Arrangement.Snap(dropped, others, (int)Math.Ceiling(SnapPixels / frame.Scale));
            Canvas.SetLeft(group, frame.ToCanvasX(target.X) - 3);
            Canvas.SetTop(group, frame.ToCanvasY(target.Y) - 3);
            group.ToolTip = $"({target.X}, {target.Y})";
        };

        group.MouseLeftButtonUp += (_, e) =>
        {
            if (_mouseDragging != group)
                return;
            _mouseDragging = null;
            group.ReleaseMouseCapture();
            e.Handled = true;
            if (moved && (target.X, target.Y) != (placed.X, placed.Y))
                machine.InputPosition = new VirtualPosition(target.X, target.Y); // redraws
            else
                DrawMouseMap();
        };

        var automatic = new MenuItem
        {
            Header = L.T("Place again on its side ({side})", ("side", _vm.InputSideOptions.FirstOrDefault(o => o.Side == machine.InputSide)?.Label ?? "")),
            IsEnabled = machine.InputPosition is not null,
        };
        automatic.Click += (_, _) => machine.InputPosition = null;
        group.ContextMenu = new ContextMenu { Items = { automatic } };
    }
}
