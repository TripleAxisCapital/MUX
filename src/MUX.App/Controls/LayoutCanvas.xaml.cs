using System.Windows;
using System.Windows.Controls;
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

        Canvas.SetLeft(border, _displayRect.Left + px.Left * _scale);
        Canvas.SetTop(border, _displayRect.Top + px.Top * _scale);

        border.MouseLeftButtonDown += ZoneMouseDown;
        border.MouseMove += ZoneMouseMove;
        border.MouseLeftButtonUp += ZoneMouseUp;
        HostCanvas.Children.Add(border);
    }

    private void ZoneMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not VirtualMonitorZone zone)
        {
            return;
        }

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
