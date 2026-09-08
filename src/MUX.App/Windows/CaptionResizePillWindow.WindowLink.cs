using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MUX.App.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private WindowLinkService? _windowLinkService;
    private Button? _windowLinkButton;
    private ShapePath? _windowLinkGlyph;
    private DispatcherTimer? _windowLinkVisualTimer;
    private IntPtr _lastWindowLinkTarget;
    private bool _lastWindowLinkAvailable;
    private bool _lastWindowLinked;

    private static readonly Geometry WindowLinkGeometry = Geometry.Parse(
        "M6.6,5.2 L4.8,7 C3.7,8.1 3.7,9.9 4.8,11 C5.9,12.1 7.7,12.1 8.8,11 L10.6,9.2 " +
        "M9.4,8.8 L11.2,7 C12.3,5.9 14.1,5.9 15.2,7 C16.3,8.1 16.3,9.9 15.2,11 L13.4,12.8 " +
        "M7.4,10.6 L12.6,7.4");

    private void InitializeWindowLinkControls()
    {
        if (_windowLinkService is null)
        {
            try
            {
                _windowLinkService = new WindowLinkService();
                _windowLinkService.Changed += WindowLinkService_Changed;
            }
            catch
            {
                _windowLinkService = null;
            }
        }

        EnsureWindowLinkButton();

        if (_windowLinkVisualTimer is null)
        {
            _windowLinkVisualTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(110)
            };
            _windowLinkVisualTimer.Tick += WindowLinkVisualTimer_Tick;
            _windowLinkVisualTimer.Start();
        }

        _lastWindowLinkTarget = IntPtr.Zero;
        RefreshWindowLinkAvailability(force: true);
    }

    private void EnsureWindowLinkButton()
    {
        if (_windowLinkButton is not null || MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        // Four utility actions share the existing 78-DIP cluster. This keeps the pill right-anchored
        // exactly as before and prevents the new control from pushing beyond the target window.
        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });

        if (CollapsedPanel.ColumnDefinitions.Count > 6)
        {
            CollapsedPanel.ColumnDefinitions[6].Width = new GridLength(78);
        }

        var currentSizeBorder = controlGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 0);
        if (currentSizeBorder is not null)
        {
            currentSizeBorder.Padding = new Thickness(0.5, 0, 0.5, 0);
        }
        CurrentSizeText.FontSize = 8.0;

        MagnetButton.Width = 12;
        MagnetButton.Padding = new Thickness(0);
        Grid.SetColumn(MagnetButton, 2);
        if (MagnetButton.Content is Viewbox magnetViewbox)
        {
            magnetViewbox.Width = 10;
            magnetViewbox.Height = 10;
        }

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Width = 12;
            _edgeCoverButton.Padding = new Thickness(0);
            Grid.SetColumn(_edgeCoverButton, 4);
            if (_edgeCoverButton.Content is Viewbox edgeViewbox)
            {
                edgeViewbox.Width = 10;
                edgeViewbox.Height = 10;
            }
        }

        if (_sizeLockButton is not null)
        {
            _sizeLockButton.Width = 12;
            _sizeLockButton.Padding = new Thickness(0);
            Grid.SetColumn(_sizeLockButton, 6);
            if (_sizeLockButton.Content is Viewbox lockViewbox)
            {
                lockViewbox.Width = 10;
                lockViewbox.Height = 10;
            }
        }

        _windowLinkGlyph = new ShapePath
        {
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            Data = WindowLinkGeometry,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromRgb(156, 156, 164)),
            StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true
        };

        var glyphViewbox = new Viewbox
        {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            Child = _windowLinkGlyph
        };

        _windowLinkButton = new Button
        {
            Width = 12,
            Height = 34,
            Padding = new Thickness(0),
            ToolTip = "Link Windows",
            Content = glyphViewbox,
            Visibility = Visibility.Collapsed
        };
        _windowLinkButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        _windowLinkButton.Click += WindowLinkButton_Click;
        Grid.SetColumn(_windowLinkButton, 8);
        controlGrid.Children.Add(_windowLinkButton);
    }

    private void WindowLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_windowLinkService is null)
        {
            InitializeWindowLinkControls();
        }

        if (_windowLinkService is null || _targetHwnd == IntPtr.Zero)
        {
            return;
        }

        var alreadyLinked = _windowLinkService.IsLinked(_targetHwnd);
        if (!alreadyLinked && !_magneticSnappingEnabled)
        {
            if (_windowLinkButton is not null)
            {
                _windowLinkButton.ToolTip = "Turn on magnetic snapping and attach two windows first.";
            }
            return;
        }

        try
        {
            _windowLinkService.ToggleLink(_targetHwnd);
        }
        catch
        {
            if (_windowLinkButton is not null)
            {
                _windowLinkButton.ToolTip = "MUX could not link these windows.";
            }
        }

        RefreshWindowLinkAvailability(force: true);
    }

    private void WindowLinkVisualTimer_Tick(object? sender, EventArgs e)
    {
        RefreshWindowLinkAvailability(force: false);
    }

    private void WindowLinkService_Changed(object? sender, EventArgs e)
    {
        try
        {
            RefreshWindowLinkAvailability(force: true);
        }
        catch
        {
            // Link UI must never destabilize the pill.
        }
    }

    private void RefreshWindowLinkAvailability(bool force = false)
    {
        if (_windowLinkButton is null || _windowLinkGlyph is null)
        {
            return;
        }

        var target = _targetHwnd;
        var linked = target != IntPtr.Zero && _windowLinkService?.IsLinked(target) == true;
        var touchingPartner = IntPtr.Zero;

        if (!linked && target != IntPtr.Zero && _magneticSnappingEnabled && _windowLinkService is not null)
        {
            try
            {
                touchingPartner = _windowLinkService.FindTouchingWindow(target);
            }
            catch
            {
                touchingPartner = IntPtr.Zero;
            }
        }

        var available = linked || touchingPartner != IntPtr.Zero;
        if (!force &&
            target == _lastWindowLinkTarget &&
            available == _lastWindowLinkAvailable &&
            linked == _lastWindowLinked)
        {
            return;
        }

        _lastWindowLinkTarget = target;
        _lastWindowLinkAvailable = available;
        _lastWindowLinked = linked;

        _windowLinkButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _windowLinkButton.Background = new SolidColorBrush(
            linked
                ? Color.FromRgb(62, 62, 69)
                : Color.FromRgb(31, 31, 35));
        _windowLinkButton.Opacity = linked ? 1.0 : 0.78;
        _windowLinkGlyph.Stroke = new SolidColorBrush(
            linked
                ? Color.FromRgb(245, 245, 247)
                : Color.FromRgb(176, 176, 184));

        _windowLinkButton.ToolTip = linked
            ? "Unlink Windows · stop moving this pair together"
            : "Link Windows · keep these magnetically attached windows moving together";
    }

    private void DisposeWindowLinkControls()
    {
        if (_windowLinkVisualTimer is not null)
        {
            _windowLinkVisualTimer.Stop();
            _windowLinkVisualTimer.Tick -= WindowLinkVisualTimer_Tick;
            _windowLinkVisualTimer = null;
        }

        if (_windowLinkService is not null)
        {
            try
            {
                _windowLinkService.Changed -= WindowLinkService_Changed;
                _windowLinkService.Dispose();
            }
            catch
            {
            }
            _windowLinkService = null;
        }

        if (_windowLinkButton is not null)
        {
            _windowLinkButton.Click -= WindowLinkButton_Click;
        }
    }
}
