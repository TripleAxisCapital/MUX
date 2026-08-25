using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MUX.App.Services;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Windows;

public partial class LayoutOverlayWindow : Window
{
    private readonly DisplayProfile _display;
    private readonly LayoutProfile _layout;
    private Border? _dragElement;
    private VirtualMonitorZone? _dragZone;
    private Point _dragStart;
    private double _startX;
    private double _startY;

    public LayoutOverlayWindow(DisplayProfile display, LayoutProfile layout)
    {
        _display = display;
        _layout = layout;
        InitializeComponent();

        DisplayLabel.Text = $"{display.DiagonalInches:0.#}\" · {display.WidthPx} × {display.HeightPx} · {_layout.Name}";
        SourceInitialized += (_, _) => ScreenWindowService.FillDisplay(this, _display);
        Loaded += (_, _) => RenderZones();
        SizeChanged += (_, _) => RenderZones();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    private void RenderZones()
    {
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        ZoneCanvas.Children.Clear();
        foreach (var zone in _layout.Zones)
        {
            var rect = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
            var border = BuildZone(zone);
            border.Width = rect.Width * ActualWidth / _display.WidthPx;
            border.Height = rect.Height * ActualHeight / _display.HeightPx;
            Canvas.SetLeft(border, rect.Left * ActualWidth / _display.WidthPx);
            Canvas.SetTop(border, rect.Top * ActualHeight / _display.HeightPx);
            ZoneCanvas.Children.Add(border);
        }
    }

    private Border BuildZone(VirtualMonitorZone zone)
    {
        var border = new Border
        {
            Tag = zone,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(238, 245, 245, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            Child = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{zone.DiagonalInches:0.#}\"",
                                Foreground = new SolidColorBrush(Color.FromRgb(10, 10, 11)),
                                FontSize = 26,
                                FontWeight = FontWeights.SemiBold,
                                HorizontalAlignment = HorizontalAlignment.Center
                            },
                            new TextBlock
                            {
                                Text = $"{zone.Name} · {zone.AspectLabel}",
                                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 86)),
                                FontSize = 11,
                                Margin = new Thickness(0, 3, 0, 0),
                                HorizontalAlignment = HorizontalAlignment.Center
                            }
                        }
                    }
                }
            }
        };

        border.MouseLeftButtonDown += Zone_MouseLeftButtonDown;
        border.MouseMove += Zone_MouseMove;
        border.MouseLeftButtonUp += Zone_MouseLeftButtonUp;
        return border;
    }

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not VirtualMonitorZone zone)
        {
            return;
        }

        _dragElement = border;
        _dragZone = zone;
        _dragStart = e.GetPosition(ZoneCanvas);
        _startX = zone.XInches;
        _startY = zone.YInches;
        border.CaptureMouse();
        Panel.SetZIndex(border, 100);
        e.Handled = true;
    }

    private void Zone_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement is null || _dragZone is null || e.LeftButton != MouseButtonState.Pressed || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var point = e.GetPosition(ZoneCanvas);
        var deltaPhysicalPxX = (point.X - _dragStart.X) * _display.WidthPx / ActualWidth;
        var deltaPhysicalPxY = (point.Y - _dragStart.Y) * _display.HeightPx / ActualHeight;
        var ppi = DisplayGeometry.PixelsPerInch(_display);

        _dragZone.XInches = _startX + deltaPhysicalPxX / ppi;
        _dragZone.YInches = _startY + deltaPhysicalPxY / ppi;
        SnapZone(_dragZone);
        DisplayGeometry.ClampZoneToDisplay(_display, _dragZone);

        var rect = DisplayGeometry.ZoneToPixels(_display, _dragZone, includeDisplayOffset: false);
        Canvas.SetLeft(_dragElement, rect.Left * ActualWidth / _display.WidthPx);
        Canvas.SetTop(_dragElement, rect.Top * ActualHeight / _display.HeightPx);
    }

    private void Zone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement is null)
        {
            return;
        }

        Panel.SetZIndex(_dragElement, 0);
        _dragElement.ReleaseMouseCapture();
        _dragElement = null;
        _dragZone = null;
        e.Handled = true;
    }

    private void SnapZone(VirtualMonitorZone zone)
    {
        const double snapPixels = 12;
        var ppi = DisplayGeometry.PixelsPerInch(_display);
        var thresholdInches = snapPixels / ppi;
        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        var zoneSize = DisplayGeometry.PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);

        zone.XInches = SnapValue(zone.XInches, 0, thresholdInches);
        zone.YInches = SnapValue(zone.YInches, 0, thresholdInches);
        zone.XInches = SnapValue(zone.XInches, displaySize.Width - zoneSize.Width, thresholdInches);
        zone.YInches = SnapValue(zone.YInches, displaySize.Height - zoneSize.Height, thresholdInches);

        foreach (var other in _layout.Zones.Where(z => z.Id != zone.Id))
        {
            var otherSize = DisplayGeometry.PhysicalSizeFromDiagonal(other.DiagonalInches, other.AspectWidth, other.AspectHeight);
            zone.XInches = SnapValue(zone.XInches, other.XInches + otherSize.Width, thresholdInches);
            zone.XInches = SnapValue(zone.XInches + zoneSize.Width, other.XInches, thresholdInches) - zoneSize.Width;
            zone.YInches = SnapValue(zone.YInches, other.YInches + otherSize.Height, thresholdInches);
            zone.YInches = SnapValue(zone.YInches + zoneSize.Height, other.YInches, thresholdInches) - zoneSize.Height;
        }
    }

    private static double SnapValue(double value, double target, double threshold)
        => Math.Abs(value - target) <= threshold ? target : value;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
