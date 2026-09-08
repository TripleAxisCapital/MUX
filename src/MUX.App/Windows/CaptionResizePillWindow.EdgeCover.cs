using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MUX.App.Services;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private EnhancedEdgeCoverService? _edgeCoverService;
    private Button? _edgeCoverButton;
    private Rectangle? _edgeCoverGlyph;
    private DispatcherTimer? _edgeCoverVisualTimer;
    private IntPtr _lastEdgeCoverVisualTarget;

    private void InitializeEdgeCoverControls()
    {
        if (_edgeCoverService is null)
        {
            try
            {
                // Edge-cover sessions are process-wide so disabling the caption pill on another
                // display cannot destroy configured black bars or their user-selected depths.
                _edgeCoverService = EnhancedEdgeCoverService.Shared;
                _edgeCoverService.Changed += EdgeCoverService_Changed;
            }
            catch
            {
                _edgeCoverService = null;
            }
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

        try
        {
            _edgeCoverService.ToggleWindow(_targetHwnd);
        }
        catch
        {
            if (_edgeCoverButton is not null)
            {
                _edgeCoverButton.ToolTip = "MUX could not create the edge covers for this window.";
            }
            return;
        }

        _lastEdgeCoverVisualTarget = _targetHwnd;
        UpdateEdgeCoverVisual();

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                try
                {
                    if (!IsVisible)
                    {
                        return;
                    }

                    Topmost = false;
                    Topmost = true;
                }
                catch
                {
                }
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
        try { UpdateEdgeCoverVisual(); } catch { }
    }

    private void UpdateEdgeCoverVisual()
    {
        if (_edgeCoverButton is null || _edgeCoverGlyph is null)
        {
            return;
        }

        var active = _edgeCoverService?.IsEnabledForWindow(_targetHwnd) == true;
        var globallyVisible = _edgeCoverService?.AreCoversGloballyVisible != false;
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
            : active && !globallyVisible
                ? "Window edge covers · Configured but hidden globally · Use the Stream Deck / black-bars shortcut to show all"
                : active
                    ? "Window edge covers · On · Grab from either side when you are near a black edge"
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
            // The edge-cover service is process-wide. Only detach this pill's listener; disposing
            // it here would erase every configured cover whenever the display-gated pill is rebuilt.
            try { _edgeCoverService.Changed -= EdgeCoverService_Changed; } catch { }
            _edgeCoverService = null;
        }

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Click -= EdgeCoverButton_Click;
        }
    }
}
