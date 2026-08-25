using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using MUX.App.Services;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Windows;

public partial class CalibrationWindow : Window
{
    private const double TargetInches = 10.0;
    private readonly DisplayProfile _display;
    private readonly double _originalScale;

    public CalibrationWindow(DisplayProfile display)
    {
        _display = display;
        _originalScale = display.CalibrationScale;
        InitializeComponent();
        SourceInitialized += (_, _) => ScreenWindowService.FillDisplay(this, _display);
        SizeChanged += (_, _) => UpdateLine();
        LineCanvas.SizeChanged += (_, _) => UpdateLine();
        Loaded += (_, _) => UpdateLine();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void UpdateLine()
    {
        if (ActualWidth <= 1 || _display.WidthPx <= 0)
        {
            return;
        }

        var ppi = DisplayGeometry.PixelsPerInch(_display);
        var linePixels = TargetInches * ppi;
        var lineWidth = linePixels * ActualWidth / _display.WidthPx;
        var canvasWidth = Math.Max(1, LineCanvas.ActualWidth);
        var left = (canvasWidth - lineWidth) / 2;
        var centerY = Math.Max(0, (LineCanvas.ActualHeight - 2) / 2);

        MeasureLine.Width = lineWidth;
        Canvas.SetLeft(MeasureLine, left);
        Canvas.SetTop(MeasureLine, centerY);
        Canvas.SetLeft(LeftTick, left);
        Canvas.SetTop(LeftTick, centerY - 11);
        Canvas.SetLeft(RightTick, left + lineWidth - 2);
        Canvas.SetTop(RightTick, centerY - 11);
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(MeasuredBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var measured) || measured <= 0.5 || measured > 50)
        {
            ErrorText.Text = "Enter the length you measured in inches.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _display.CalibrationScale *= TargetInches / measured;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _display.CalibrationScale = 1.0;
        UpdateLine();
        MeasuredBox.Text = "10.00";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _display.CalibrationScale = _originalScale;
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _display.CalibrationScale = _originalScale;
            DialogResult = false;
        }
    }
}
