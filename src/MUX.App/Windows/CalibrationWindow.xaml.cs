using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using MUX.App.Services;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Windows;

public partial class CalibrationWindow : Window
{
    private const double ReferenceInches = 6.0;
    private readonly DisplayProfile _display;
    private readonly double _originalScale;
    private bool _hasAppliedCorrection;

    public CalibrationWindow(DisplayProfile display)
    {
        _display = display;
        _originalScale = display.CalibrationScale;
        InitializeComponent();
        DisplayLabel.Text = $"{display.FriendlyName}  ·  {display.WidthPx} × {display.HeightPx} px  ·  {display.DiagonalInches:0.##} in diagonal";
        SourceInitialized += (_, _) => ScreenWindowService.FillDisplay(this, _display);
        LineCanvas.SizeChanged += (_, _) => UpdateLine();
        Loaded += (_, _) => UpdateLine();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void UpdateLine()
    {
        if (LineCanvas.ActualWidth < 100 || _display.WidthPx < 1)
        {
            return;
        }

        // Per-monitor DPI awareness gives the true screen-pixel <-> WPF DIP transform.
        // The reference length is deliberately not calculated from a system-wide DPI.
        var dipPerPixel = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11
                          ?? ActualWidth / _display.WidthPx;
        var requestedPixels = ReferenceInches * DisplayGeometry.PixelsPerInch(_display);
        var widthDip = Math.Clamp(requestedPixels * dipPerPixel, 24, LineCanvas.ActualWidth - 40);
        var x = (LineCanvas.ActualWidth - widthDip) / 2;
        var y = Math.Max(16, LineCanvas.ActualHeight / 2);

        MeasureLine.Width = widthDip;
        Canvas.SetLeft(MeasureLine, x);
        Canvas.SetTop(MeasureLine, y);
        Canvas.SetLeft(LeftTick, x);
        Canvas.SetTop(LeftTick, y - 12);
        Canvas.SetLeft(RightTick, x + widthDip - 2);
        Canvas.SetTop(RightTick, y - 12);
    }

    private double GetRenderedReferencePixels()
    {
        // PointToScreen reports device pixels, not WPF DIPs. This also handles
        // displays on the left of the primary monitor and fractional Windows scaling.
        var left = Canvas.GetLeft(MeasureLine);
        var y = Canvas.GetTop(MeasureLine);
        var first = LineCanvas.PointToScreen(new Point(left, y));
        var last = LineCanvas.PointToScreen(new Point(left + MeasureLine.Width, y));
        return Math.Abs(last.X - first.X);
    }

    private bool TryReadMeasurement(out double inches)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var entered = (MeasuredBox.Text ?? "").Trim();
        if (!double.TryParse(entered, NumberStyles.Float, CultureInfo.CurrentCulture, out inches) &&
            !double.TryParse(entered, NumberStyles.Float, CultureInfo.InvariantCulture, out inches))
        {
            ShowError("Enter the ruler measurement in inches, such as 6.25.");
            return false;
        }

        if (!double.IsFinite(inches) || inches < 1 || inches > 48)
        {
            ShowError("Enter a measured length between 1 and 48 inches.");
            return false;
        }

        return true;
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadMeasurement(out var measured))
        {
            return;
        }

        var referencePixels = GetRenderedReferencePixels();
        if (!double.IsFinite(referencePixels) || referencePixels < 50)
        {
            ShowError("The reference did not render correctly. Check the selected display.");
            return;
        }

        var newScale = DisplayGeometry.CalibrationScaleFromReference(_display, referencePixels, measured);
        if (!double.IsFinite(newScale) || newScale < 0.2 || newScale > 5.0)
        {
            ShowError("That measurement is inconsistent with the selected screen. Check the ruler and display.");
            return;
        }

        _display.CalibrationScale = newScale;
        _hasAppliedCorrection = true;
        SaveCalibrationButton.IsEnabled = true;
        ApplyButton.Content = "Adjust again";
        StepLabel.Text = "STEP 2 / 2  ·  VERIFY";
        InstructionLabel.Text = "Measure the corrected line again. It should be approximately 6.00 inches.";
        StatusLabel.Text = $"Calibrated to {DisplayGeometry.PixelsPerInch(_display):0.0} physical pixels per inch. Enter the new measurement to verify.";
        MeasuredBox.Text = ReferenceInches.ToString("0.00", CultureInfo.CurrentCulture);
        UpdateLine();
        MeasuredBox.Focus();
        MeasuredBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasAppliedCorrection || !TryReadMeasurement(out var verification))
        {
            return;
        }

        // Reject a visibly wrong correction; allow normal ruler/antialias tolerance.
        if (Math.Abs(verification - ReferenceInches) > 0.18)
        {
            ShowError("The verification differs by over 3%. Enter the new measured length and select Adjust again.");
            return;
        }

        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _display.CalibrationScale = 1.0;
        _hasAppliedCorrection = false;
        SaveCalibrationButton.IsEnabled = false;
        ApplyButton.Content = "Apply correction";
        StepLabel.Text = "STEP 1 / 2  ·  MEASURE";
        InstructionLabel.Text = "Enter the actual length of the line, not the expected length.";
        StatusLabel.Text = "Reference reset to the physical display diagonal. Measure the line between the ticks.";
        MeasuredBox.Text = ReferenceInches.ToString("0.00", CultureInfo.CurrentCulture);
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateLine();
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

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
        {
            _display.CalibrationScale = _originalScale;
        }
        base.OnClosed(e);
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
