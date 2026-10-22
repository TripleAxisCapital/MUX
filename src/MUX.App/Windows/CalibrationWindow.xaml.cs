using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MUX.App.Services;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Windows;

/// <summary>
/// Screen-physical 10-inch rectangular calibration. We read the actual corner
/// coordinates through PointToScreen, so DPI scaling and WPF DIPs never enter
/// the persisted pixels-per-inch calculation. The second step renders a different
/// physical diagonal as an independent check before any calibration is committed.
/// </summary>
public partial class CalibrationWindow : Window
{
    private const double CalibrationDiagonalInches = 10.0;
    private const double DesiredCheckDiagonalInches = 15.0;
    private const double AspectWidth = 16.0;
    private const double AspectHeight = 9.0;
    private const double MinimumFrameDiagonalPx = 125.0;
    private const double MaximumVerificationErrorFraction = 0.015;

    private readonly DisplayProfile _display;
    private readonly double _originalScale;
    private bool _calibrating;
    private double _frameDiagonalPx;
    private double _verificationInches = CalibrationDiagonalInches;
    private double _lastUserMatchedDiagonalPx;

    public CalibrationWindow(DisplayProfile display)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _originalScale = display.CalibrationScale;
        InitializeComponent();
        DisplayLabel.Text = $"{display.FriendlyName} · {display.WidthPx} × {display.HeightPx} physical pixels";
        SourceInitialized += (_, _) => ScreenWindowService.FillDisplay(this, _display);
        Loaded += (_, _) =>
        {
            _frameDiagonalPx = CalibrationDiagonalInches * DisplayGeometry.PixelsPerInch(_display);
            RenderFrame();
        };
        ReferenceCanvas.SizeChanged += (_, _) =>
        {
            if (_frameDiagonalPx > 0) RenderFrame();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                CancelAndClose();
                args.Handled = true;
            }
        };
    }

    private (double X, double Y) PixelsToDips()
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        if (!(transform.M11 > 0) || !(transform.M22 > 0))
            throw new InvalidOperationException("Windows did not provide the physical display DPI transform.");
        return (transform.M11, transform.M22);
    }

    private double MaximumFrameDiagonalPixels()
    {
        var (xScale, yScale) = PixelsToDips();
        var availableW = Math.Max(1, ReferenceCanvas.ActualWidth - 90) / xScale;
        var availableH = Math.Max(1, ReferenceCanvas.ActualHeight - 90) / yScale;
        var aspectHyp = Math.Sqrt(AspectWidth * AspectWidth + AspectHeight * AspectHeight);
        return Math.Min(availableW * aspectHyp / AspectWidth, availableH * aspectHyp / AspectHeight);
    }

    private void RenderFrame()
    {
        if (ReferenceCanvas.ActualWidth < 150 || ReferenceCanvas.ActualHeight < 150)
            return;

        var (xScale, yScale) = PixelsToDips();
        var maximum = MaximumFrameDiagonalPixels();
        _frameDiagonalPx = Math.Clamp(_frameDiagonalPx, MinimumFrameDiagonalPx, Math.Max(MinimumFrameDiagonalPx, maximum));
        var hyp = Math.Sqrt(AspectWidth * AspectWidth + AspectHeight * AspectHeight);
        var widthDip = _frameDiagonalPx * AspectWidth / hyp * xScale;
        var heightDip = _frameDiagonalPx * AspectHeight / hyp * yScale;
        ReferenceFrame.Width = widthDip;
        ReferenceFrame.Height = heightDip;

        var left = Math.Max(14, (ReferenceCanvas.ActualWidth - widthDip) / 2);
        var top = Math.Max(14, (ReferenceCanvas.ActualHeight - heightDip) / 2);
        Canvas.SetLeft(ReferenceFrame, left);
        Canvas.SetTop(ReferenceFrame, top);
        Canvas.SetLeft(ResizeHandle, left + widthDip - ResizeHandle.Width / 2);
        Canvas.SetTop(ResizeHandle, top + heightDip - ResizeHandle.Height / 2);
        DiagonalLine.X1 = 1;
        DiagonalLine.Y1 = 1;
        DiagonalLine.X2 = Math.Max(2, widthDip - 1);
        DiagonalLine.Y2 = Math.Max(2, heightDip - 1);

        PixelDimensionsText.Text = _calibrating
            ? $"Verification target: {_verificationInches:0.0} inches diagonal"
            : "Drag ↘ to resize · Fixed 16:9 shape · Free physical measurement";
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_calibrating) return;
        var (xScale, yScale) = PixelsToDips();
        var hyp = Math.Sqrt(AspectWidth * AspectWidth + AspectHeight * AspectHeight);
        var changeInPhysicalDiagonalPixels =
            (e.HorizontalChange / xScale * AspectWidth +
             e.VerticalChange / yScale * AspectHeight) / hyp;
        _frameDiagonalPx = Math.Clamp(_frameDiagonalPx + changeInPhysicalDiagonalPixels,
            MinimumFrameDiagonalPx, Math.Max(MinimumFrameDiagonalPx, MaximumFrameDiagonalPixels()));
        RenderFrame();
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Get the actual native Windows pixels between OUTER border corners.</summary>
    private (double WidthPx, double HeightPx) MeasureActualScreenPixels()
    {
        ReferenceFrame.UpdateLayout();
        var first = ReferenceFrame.PointToScreen(new Point(0, 0));
        var last = ReferenceFrame.PointToScreen(
            new Point(ReferenceFrame.ActualWidth, ReferenceFrame.ActualHeight));
        return (Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y));
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrating)
        {
            // Reopen adjustment without committing an unverified candidate.
            _display.CalibrationScale = _originalScale;
            _calibrating = false;
            _frameDiagonalPx = _lastUserMatchedDiagonalPx > 0
                ? _lastUserMatchedDiagonalPx : CalibrationDiagonalInches * DisplayGeometry.PixelsPerInch(_display);
            ResizeHandle.Visibility = Visibility.Visible;
            FrameLabel.Text = "10-INCH REFERENCE";
            StepText.Text = "01 / Set 10 inches";
            InstructionText.Text = "Drag the white corner handle until the OUTER diagonal of the rectangle measures exactly 10 inches with a ruler.";
            ActionButton.Content = "Match 10-inch reference";
            SaveButton.Visibility = Visibility.Collapsed;
            VerificationLabel.Visibility = Visibility.Collapsed;
            VerificationBox.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            RenderFrame();
            return;
        }

        try
        {
            var (widthPixels, heightPixels) = MeasureActualScreenPixels();
            var measuredPixels = Math.Sqrt(widthPixels * widthPixels + heightPixels * heightPixels);
            if (measuredPixels < MinimumFrameDiagonalPx ||
                measuredPixels > Math.Sqrt((double)_display.WidthPx * _display.WidthPx +
                                           (double)_display.HeightPx * _display.HeightPx))
            {
                ShowError("Unable to measure the rectangle reliably on this monitor.");
                return;
            }

            var scale = DisplayGeometry.CalibrationScaleFromRectangle(
                _display, widthPixels, heightPixels, CalibrationDiagonalInches);
            if (!double.IsFinite(scale) || scale is < 0.2 or > 5.0)
            {
                ShowError("The reference size is outside the supported calibration range.");
                return;
            }

            _lastUserMatchedDiagonalPx = measuredPixels;
            _display.CalibrationScale = scale;
            _calibrating = true;
            ResizeHandle.Visibility = Visibility.Collapsed;
            _verificationInches = CalculateVerificationDiagonal();
            _frameDiagonalPx = _verificationInches * DisplayGeometry.PixelsPerInch(_display);
            FrameLabel.Text = $"{_verificationInches:0.#}-INCH VERIFICATION";
            StepText.Text = "02 / Verify physical size";
            InstructionText.Text = $"Measure the NEW rectangle diagonally. It should now be {_verificationInches:0.#} inches. Enter its actual measured diagonal below.";
            VerificationLabel.Visibility = Visibility.Visible;
            VerificationBox.Visibility = Visibility.Visible;
            VerificationBox.Text = "";
            ActionButton.Content = "Adjust reference again";
            SaveButton.Visibility = Visibility.Visible;
            RenderFrame();
            VerificationBox.Focus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowError("Calibration could not determine physical pixel dimensions: " + ex.Message);
        }
    }

    private double CalculateVerificationDiagonal()
    {
        var maxPhysicalDiagonal = MaximumFrameDiagonalPixels() /
                                  DisplayGeometry.PixelsPerInch(_display);
        var test = Math.Min(DesiredCheckDiagonalInches, Math.Floor((maxPhysicalDiagonal - 0.4) * 2) / 2);
        return test > CalibrationDiagonalInches + 0.5 ? test : CalibrationDiagonalInches;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_calibrating) return;
        if (!double.TryParse(VerificationBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var actual)
            && !double.TryParse(VerificationBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out actual))
        {
            ShowError("Enter the diagonal you measured using your tape or ruler.");
            return;
        }

        if (!double.IsFinite(actual) || actual <= 0)
        {
            ShowError("Please enter a valid positive diagonal measurement.");
            return;
        }

        var relativeError = Math.Abs(actual / _verificationInches - 1.0);
        if (relativeError > MaximumVerificationErrorFraction)
        {
            ShowError($"Verification is {relativeError:P1} off the target. Calibration was NOT saved. Select Adjust reference again and try once more.");
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelAndClose();

    private void CancelAndClose()
    {
        _display.CalibrationScale = _originalScale;
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            _display.CalibrationScale = _originalScale;
        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
