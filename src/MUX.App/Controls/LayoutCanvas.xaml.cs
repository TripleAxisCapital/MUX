using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Controls;

public partial class LayoutCanvas : UserControl
{
    private DisplayProfile? _display;
    private LayoutProfile? _layout;
    private VirtualMonitorZone? _selectedZone;
    private Border? _dragElement;
    private Point _dragStart;
    private double _startX;
    private double _startY;
    private Rect _displayRect;
    private double _scale;
    private VirtualMonitorZone? _resizingZone;
    private Border? _resizingElement;
    private PixelRect _resizeOrigin;
    private Point _resizePointerOrigin;
    private int _resizeHorizontalEdge;
    private int _resizeVerticalEdge;

    public event EventHandler<VirtualMonitorZone?>? SelectedZoneChanged;
    public event EventHandler? LayoutChanged;

    public LayoutCanvas()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
        MouseLeftButtonDown += (_, _) => SelectZone(null);
    }

    public VirtualMonitorZone? SelectedZone => _selectedZone;

    public void SetModel(DisplayProfile? display, LayoutProfile? layout)
    {
        _display = display;
        _layout = layout;
        _selectedZone = null;
        Render();
        SelectedZoneChanged?.Invoke(this, null);
    }

    public void Refresh() => Render();

    public void SelectZone(VirtualMonitorZone? zone)
    {
        _selectedZone = zone;
        Render();
        SelectedZoneChanged?.Invoke(this, zone);
    }

    private void Render()
    {
        HostCanvas.Children.Clear();

        if (_display is null || _layout is null || ActualWidth <= 20 || ActualHeight <= 20 || _display.WidthPx <= 0 || _display.HeightPx <= 0)
        {
            return;
        }

        const double margin = 42;
        var availableWidth = Math.Max(1, ActualWidth - margin * 2);
        var availableHeight = Math.Max(1, ActualHeight - margin * 2);
        _scale = Math.Min(availableWidth / _display.WidthPx, availableHeight / _display.HeightPx);

        var displayWidth = _display.WidthPx * _scale;
        var displayHeight = _display.HeightPx * _scale;
        _displayRect = new Rect(
            (ActualWidth - displayWidth) / 2,
            (ActualHeight - displayHeight) / 2,
            displayWidth,
            displayHeight);

        var surface = new Border
        {
            Width = displayWidth,
            Height = displayHeight,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 48, 54)),
            BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(surface, _displayRect.Left);
        Canvas.SetTop(surface, _displayRect.Top);
        HostCanvas.Children.Add(surface);

        var label = new TextBlock
        {
            Text = $"{_display.DiagonalInches:0.#}\"  ·  {_display.WidthPx} × {_display.HeightPx}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(126, 126, 136)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, _displayRect.Left + 14);
        Canvas.SetTop(label, _displayRect.Top + 11);
        HostCanvas.Children.Add(label);

        foreach (var zone in _layout.Zones)
        {
            AddZone(zone);
        }
    }

    private void AddZone(VirtualMonitorZone zone)
    {
        if (_display is null)
        {
            return;
        }

        var px = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
        var isSelected = _selectedZone?.Id == zone.Id;

        var border = new Border
        {
            Tag = zone,
            Width = Math.Max(12, px.Width * _scale),
            Height = Math.Max(12, px.Height * _scale),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(isSelected ? Color.FromArgb(235, 245, 245, 247) : Color.FromArgb(230, 34, 34, 38)),
            BorderBrush = new SolidColorBrush(isSelected ? Color.FromRgb(255, 255, 255) : Color.FromRgb(76, 76, 84)),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Cursor = Cursors.SizeAll,
            Child = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{zone.DiagonalInches:0.#}\"",
                                FontSize = 20,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(10, 10, 11) : Color.FromRgb(245, 245, 247)),
                                HorizontalAlignment = HorizontalAlignment.Center
                            },
                            new TextBlock
                            {
                                Text = zone.AspectLabel,
                                Margin = new Thickness(0, 2, 0, 0),
                                FontSize = 11,
                                Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(80, 80, 86) : Color.FromRgb(155, 155, 165)),
                                HorizontalAlignment = HorizontalAlignment.Center
                            }
                        }
                    }
                }
            }
        };

        AddResizeHandles((Grid)border.Child, zone, border, isSelected);
        Canvas.SetLeft(border, _displayRect.Left + px.Left * _scale);
        Canvas.SetTop(border, _displayRect.Top + px.Top * _scale);

        border.MouseLeftButtonDown += ZoneMouseDown;
        border.MouseMove += ZoneMouseMove;
        border.MouseLeftButtonUp += ZoneMouseUp;
        HostCanvas.Children.Add(border);
    }

    private void AddResizeHandles(Grid grid, VirtualMonitorZone zone, Border border, bool selected)
    {
        var grips = new (int X, int Y, HorizontalAlignment Horizontal, VerticalAlignment Vertical, Cursor Cursor)[]
        {
            (-1, -1, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE),
            ( 0, -1, HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS),
            ( 1, -1, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW),
            (-1,  0, HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE),
            ( 1,  0, HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE),
            (-1,  1, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW),
            ( 0,  1, HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS),
            ( 1,  1, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE)
        };

        foreach (var grip in grips)
        {
            var thumb = new Thumb
            {
                Width = grip.X == 0 ? 24 : 13,
                Height = grip.Y == 0 ? 24 : 13,
                HorizontalAlignment = grip.Horizontal,
                VerticalAlignment = grip.Vertical,
                Cursor = grip.Cursor,
                Opacity = selected ? 0.95 : 0.55,
                Background = new SolidColorBrush(Color.FromRgb(190, 190, 197)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                BorderThickness = new Thickness(1),
                ToolTip = "Drag to resize · freeform aspect ratio"
            };
            thumb.DragStarted += (_, _) => BeginResize(zone, border, grip.X, grip.Y);
            thumb.DragDelta += (_, _) => UpdateResize();
            thumb.DragCompleted += (_, _) => CompleteResize();
            Panel.SetZIndex(thumb, 10);
            grid.Children.Add(thumb);
        }
    }

    private void BeginResize(VirtualMonitorZone zone, Border border, int horizontal, int vertical)
    {
        if (_display is null)
            return;

        _selectedZone = zone;
        SelectedZoneChanged?.Invoke(this, zone);
        _resizingZone = zone;
        _resizingElement = border;
        _resizeOrigin = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
        _resizePointerOrigin = Mouse.GetPosition(HostCanvas);
        _resizeHorizontalEdge = horizontal;
        _resizeVerticalEdge = vertical;
    }

    private void UpdateResize()
    {
        if (_display is null || _resizingZone is null || _resizingElement is null || _scale <= 0)
            return;

        var pointer = Mouse.GetPosition(HostCanvas);
        var deltaX = (int)Math.Round((pointer.X - _resizePointerOrigin.X) / _scale);
        var deltaY = (int)Math.Round((pointer.Y - _resizePointerOrigin.Y) / _scale);
        var resized = DisplayGeometry.ResizeZoneRectangle(
            _resizeOrigin, deltaX, deltaY, _resizeHorizontalEdge, _resizeVerticalEdge,
            _display.WidthPx, _display.HeightPx);
        DisplayGeometry.SetZonePixelBounds(_display, _resizingZone, resized);
        _resizingElement.Width = Math.Max(12, resized.Width * _scale);
        _resizingElement.Height = Math.Max(12, resized.Height * _scale);
        Canvas.SetLeft(_resizingElement, _displayRect.Left + resized.Left * _scale);
        Canvas.SetTop(_resizingElement, _displayRect.Top + resized.Top * _scale);
    }

    private void CompleteResize()
    {
        if (_resizingZone is null)
            return;

        _resizingZone = null;
        _resizingElement = null;
        Render();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoneMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not VirtualMonitorZone zone)
        {
            return;
        }

        // A Thumb captures its own drag; do not rebuild the visual underneath it.
        if (_resizingZone is not null || e.OriginalSource is Thumb ||
            e.OriginalSource is FrameworkElement { TemplatedParent: Thumb })
            return;

        e.Handled = true;
        _selectedZone = zone;
        SelectedZoneChanged?.Invoke(this, zone);
        Render();

        _dragElement = HostCanvas.Children
            .OfType<Border>()
            .FirstOrDefault(element => element.Tag is VirtualMonitorZone candidate && candidate.Id == zone.Id);
        if (_dragElement is null)
        {
            return;
        }

        _dragStart = e.GetPosition(HostCanvas);
        _startX = zone.XInches;
        _startY = zone.YInches;
        _dragElement.CaptureMouse();
    }

    private void ZoneMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement is null || _display is null || _selectedZone is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(HostCanvas);
        var deltaPxX = (point.X - _dragStart.X) / _scale;
        var deltaPxY = (point.Y - _dragStart.Y) / _scale;
        var ppi = DisplayGeometry.PixelsPerInch(_display);

        _selectedZone.XInches = _startX + deltaPxX / ppi;
        _selectedZone.YInches = _startY + deltaPxY / ppi;
        DisplayGeometry.ClampZoneToDisplay(_display, _selectedZone);

        var px = DisplayGeometry.ZoneToPixels(_display, _selectedZone, includeDisplayOffset: false);
        Canvas.SetLeft(_dragElement, _displayRect.Left + px.Left * _scale);
        Canvas.SetTop(_dragElement, _displayRect.Top + px.Top * _scale);
    }

    private void ZoneMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement is null)
        {
            return;
        }

        _dragElement.ReleaseMouseCapture();
        _dragElement = null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
