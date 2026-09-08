using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MUX.App.Services;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private EdgeCoverService? _edgeCoverService;
    private Button? _edgeCoverButton;
    private Rectangle? _edgeCoverGlyph;
    private DispatcherTimer? _edgeCoverVisualTimer;
    private IntPtr _lastEdgeCoverVisualTarget;

    private void InitializeEdgeCoverControls()
    {
        if (_edgeCoverService is null)
        {
            _edgeCoverService = new EdgeCoverService();
            _edgeCoverService.Changed += EdgeCoverService_Changed;
        }

        EnsureEdgeCoverButton();

        if (_edgeCoverVisualTimer is null)
        {
            _edgeCoverVisualTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _edgeCoverVisualTimer.Tick += EdgeCoverVisualTimer_Tick;
            _edgeCoverVisualTimer.Start();
        }

        _lastEdgeCoverVisualTarget = _targetHwnd;
        UpdateEdgeCoverVisual();
    }

    private void EnsureEdgeCoverButton()
    {
        if (_edgeCoverButton is not null || MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        // Keep the pill's existing footprint. The current-size readout gives up a few pixels
        // so both compact icon buttons fit cleanly in the same 78-DIP control cluster.
        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });

        var currentSizeBorder = controlGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 0);
        if (currentSizeBorder is not null)
        {
            currentSizeBorder.Padding = new Thickness(2, 0, 2, 0);
        }

        MagnetButton.Width = 17;
        MagnetButton.Padding = new Thickness(1);
        Grid.SetColumn(MagnetButton, 2);

        _edgeCoverGlyph = new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 1.2,
            RadiusY = 1.2,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromRgb(156, 156, 164)),
            StrokeThickness = 1.8,
            SnapsToDevicePixels = true
        };

        var glyphViewbox = new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = _edgeCoverGlyph
        };

        _edgeCoverButton = new Button
        {
            Width = 17,
            Height = 34,
            Padding = new Thickness(1),
            ToolTip = "Window edge covers",
            Content = glyphViewbox
        };
        _edgeCoverButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        _edgeCoverButton.Click += EdgeCoverButton_Click;
        Grid.SetColumn(_edgeCoverButton, 4);
        controlGrid.Children.Add(_edgeCoverButton);
    }

    private void EdgeCoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_edgeCoverService is null)
        {
            InitializeEdgeCoverControls();
        }

        if (_edgeCoverService is null || _targetHwnd == IntPtr.Zero)
        {
            if (_edgeCoverButton is not null)
            {
                _edgeCoverButton.ToolTip = "Move the pointer to the window you want to frame, then click this square.";
            }
            return;
        }

        _edgeCoverService.ToggleWindow(_targetHwnd);
        _lastEdgeCoverVisualTarget = _targetHwnd;
        UpdateEdgeCoverVisual();

        // The new black bars are topmost overlays. Reassert the pill last so its controls stay
        // accessible even when the top cover is dragged down across the target caption.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!IsVisible)
                {
                    return;
                }

                Topmost = false;
                Topmost = true;
            }));
    }

    private void EdgeCoverVisualTimer_Tick(object? sender, EventArgs e)
    {
        if (_lastEdgeCoverVisualTarget == _targetHwnd)
        {
            return;
        }

        _lastEdgeCoverVisualTarget = _targetHwnd;
        UpdateEdgeCoverVisual();
    }

    private void EdgeCoverService_Changed(object? sender, EventArgs e)
    {
        UpdateEdgeCoverVisual();
    }

    private void UpdateEdgeCoverVisual()
    {
        if (_edgeCoverButton is null || _edgeCoverGlyph is null)
        {
            return;
        }

        var active = _edgeCoverService?.IsEnabledForWindow(_targetHwnd) == true;
        _edgeCoverButton.Background = new SolidColorBrush(
            active
                ? Color.FromRgb(62, 62, 69)
                : Color.FromRgb(31, 31, 35));
        _edgeCoverButton.Opacity = _targetHwnd == IntPtr.Zero ? 0.45 : active ? 1.0 : 0.72;
        _edgeCoverGlyph.Stroke = new SolidColorBrush(
            active
                ? Color.FromRgb(245, 245, 247)
                : Color.FromRgb(156, 156, 164));

        _edgeCoverButton.ToolTip = _targetHwnd == IntPtr.Zero
            ? "Window edge covers · Point at a window first"
            : active
                ? "Window edge covers · On for this window · Drag any black edge inward"
                : "Window edge covers · Off for this window";
    }

    private void DisposeEdgeCoverControls()
    {
        if (_edgeCoverVisualTimer is not null)
        {
            _edgeCoverVisualTimer.Stop();
            _edgeCoverVisualTimer.Tick -= EdgeCoverVisualTimer_Tick;
            _edgeCoverVisualTimer = null;
        }

        if (_edgeCoverService is not null)
        {
            _edgeCoverService.Changed -= EdgeCoverService_Changed;
            _edgeCoverService.Dispose();
            _edgeCoverService = null;
        }

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Click -= EdgeCoverButton_Click;
        }
    }
}
