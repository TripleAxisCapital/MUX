using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MUX.App.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private EnhancedEdgeCoverService? _edgeCoverService;
    private EdgeCoverTemplateCoordinator? _edgeCoverTemplateCoordinator;
    private Button? _edgeCoverButton;
    private Button? _edgeCoverTemplateButton;
    private Rectangle? _edgeCoverGlyph;
    private ShapePath? _edgeCoverTemplateGlyph;
    private DispatcherTimer? _edgeCoverVisualTimer;
    private IntPtr _lastEdgeCoverVisualTarget;

    private static readonly Geometry EdgeCoverTemplateGeometry = Geometry.Parse(
        "M3,2 H13 V14 H3 Z M5,2 V6 H11 V2 M5,10 H11 V14 H5 Z");

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
                _edgeCoverTemplateCoordinator = EdgeCoverTemplateCoordinator.Shared;
                _edgeCoverTemplateCoordinator.Attach(_edgeCoverService);
            }
            catch
            {
                _edgeCoverService = null;
                _edgeCoverTemplateCoordinator = null;
            }
        }

        EnsureEdgeCoverButton();
        EnsureEdgeCoverTemplateButton();

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

    private void EnsureEdgeCoverTemplateButton()
    {
        if (_edgeCoverTemplateButton is not null || MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        _edgeCoverTemplateGlyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Data = EdgeCoverTemplateGeometry,
            Fill = new SolidColorBrush(Color.FromRgb(156, 156, 164)),
            SnapsToDevicePixels = true
        };

        _edgeCoverTemplateButton = new Button
        {
            Width = 17,
            Height = 34,
            Padding = new Thickness(1),
            ToolTip = "Save current black bars as the default template",
            Content = new Viewbox
            {
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Child = _edgeCoverTemplateGlyph
            }
        };
        _edgeCoverTemplateButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        _edgeCoverTemplateButton.Click += EdgeCoverTemplateButton_Click;

        // The final utility layout is normalized after all optional controls are initialized.
        // This temporary column merely keeps the control in the same utility grid until then.
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        Grid.SetColumn(_edgeCoverTemplateButton, controlGrid.ColumnDefinitions.Count - 1);
        controlGrid.Children.Add(_edgeCoverTemplateButton);
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
            var enabled = _edgeCoverService.ToggleWindow(_targetHwnd);
            if (enabled && _edgeCoverTemplateCoordinator is not null)
            {
                // A saved template is automatically recalled whenever black bars are deployed.
                _ = _edgeCoverTemplateCoordinator.ApplyTemplate(_edgeCoverService, _targetHwnd);
            }
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

                    // Edge overlays are also top-level windows. Reassert the pill after deployment
                    // so it remains visually above them while the coordinator makes non-handle
                    // cover surface click-through.
                    Topmost = false;
                    Topmost = true;
                }
                catch
                {
                }
            }));
    }

    private void EdgeCoverTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_edgeCoverService is null)
        {
            InitializeEdgeCoverControls();
        }

        if (_edgeCoverService is null || _edgeCoverTemplateCoordinator is null || _targetHwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_edgeCoverService.IsEnabledForWindow(_targetHwnd))
        {
            if (_edgeCoverTemplateButton is not null)
            {
                _edgeCoverTemplateButton.ToolTip = "Turn on the black bars, drag them into place, then save the template.";
            }
            return;
        }

        var saved = false;
        try
        {
            saved = _edgeCoverTemplateCoordinator.SaveTemplate(_edgeCoverService, _targetHwnd);
        }
        catch
        {
        }

        if (_edgeCoverTemplateButton is not null)
        {
            _edgeCoverTemplateButton.ToolTip = saved
                ? "Default black-bar template saved · future black-bar clicks reuse this layout"
                : "MUX could not save the current black-bar template.";
        }
        UpdateEdgeCoverVisual();
    }

    private void EdgeCoverVisualTimer_Tick(object? sender, EventArgs e)
    {
        if (_lastEdgeCoverVisualTarget != _targetHwnd)
        {
            _lastEdgeCoverVisualTarget = _targetHwnd;
            UpdateEdgeCoverVisual();
        }

        // WPF/Win32 can reorder topmost helpers when a target changes activation. Keeping this
        // inexpensive assertion here prevents the cover windows from ever sitting above the pill.
        if (IsVisible && _edgeCoverService?.ConfiguredWindowCount > 0)
        {
            try
            {
                Topmost = true;
            }
            catch
            {
            }
        }
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
                    ? "Window edge covers · On · drag any inner black edge to tune it"
                    : _edgeCoverTemplateCoordinator?.HasTemplate == true
                        ? "Window edge covers · Off · click to deploy the saved template"
                        : "Window edge covers · Off for this window";

        if (_edgeCoverTemplateButton is null || _edgeCoverTemplateGlyph is null)
        {
            return;
        }

        var hasTemplate = _edgeCoverTemplateCoordinator?.HasTemplate == true;
        _edgeCoverTemplateButton.Background = new SolidColorBrush(
            hasTemplate ? Color.FromRgb(49, 49, 55) : Color.FromRgb(31, 31, 35));
        _edgeCoverTemplateButton.Opacity = _targetHwnd == IntPtr.Zero ? 0.45 : active ? 0.94 : 0.7;
        _edgeCoverTemplateGlyph.Fill = new SolidColorBrush(
            hasTemplate ? Color.FromRgb(224, 224, 230) : Color.FromRgb(156, 156, 164));
        _edgeCoverTemplateButton.ToolTip = _targetHwnd == IntPtr.Zero
            ? "Black-bar template · point at a window first"
            : active
                ? hasTemplate
                    ? "Save current black bars as the new default template"
                    : "Save current black bars as the default template"
                : hasTemplate
                    ? "Default black-bar template saved · turn on black bars to use it"
                    : "Turn on black bars, position them, then save a default template";
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
            // The edge-cover service and template coordinator are process-wide. Only detach this
            // pill's listener; configured covers must survive display filtering and pill recreation.
            try { _edgeCoverService.Changed -= EdgeCoverService_Changed; } catch { }
            _edgeCoverService = null;
        }
        _edgeCoverTemplateCoordinator = null;

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Click -= EdgeCoverButton_Click;
        }
        if (_edgeCoverTemplateButton is not null)
        {
            _edgeCoverTemplateButton.Click -= EdgeCoverTemplateButton_Click;
        }
    }
}
