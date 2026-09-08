using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MUX.App.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private ReliableWindowLinkService? _windowLinkService;
    private Button? _windowLinkButton;
    private ShapePath? _windowLinkGlyph;
    private DispatcherTimer? _windowLinkVisualTimer;
    private IntPtr _lastWindowLinkTarget;
    private bool _lastWindowLinkAvailable;
    private bool _lastWindowLinked;
    private int _lastWindowLinkGroupSize;

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
                _windowLinkService = ReliableWindowLinkService.Shared;
                _windowLinkService.GroupSnappingEnabled = _magneticSnappingEnabled;
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
                Interval = TimeSpan.FromMilliseconds(90)
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

        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });

        if (CollapsedPanel.ColumnDefinitions.Count > 6)
        {
            CollapsedPanel.ColumnDefinitions[6].Width = new GridLength(78);
        }

        var currentSizeBorder = controlGrid.Children.OfType<Border>().FirstOrDefault(child => Grid.GetColumn(child) == 0);
        if (currentSizeBorder is not null)
        {
            currentSizeBorder.Padding = new Thickness(0.5, 0, 0.5, 0);
        }
        CurrentSizeText.FontSize = 7.8;

        MagnetButton.Width = 13;
        MagnetButton.Padding = new Thickness(0);
        Grid.SetColumn(MagnetButton, 2);
        if (MagnetButton.Content is Viewbox magnetViewbox)
        {
            magnetViewbox.Width = 11;
            magnetViewbox.Height = 11;
        }

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Width = 13;
            _edgeCoverButton.Padding = new Thickness(0);
            Grid.SetColumn(_edgeCoverButton, 4);
            if (_edgeCoverButton.Content is Viewbox edgeViewbox)
            {
                edgeViewbox.Width = 11;
                edgeViewbox.Height = 11;
            }
        }

        if (_sizeLockButton is not null)
        {
            _sizeLockButton.Width = 13;
            _sizeLockButton.Padding = new Thickness(0);
            Grid.SetColumn(_sizeLockButton, 6);
            if (_sizeLockButton.Content is Viewbox lockViewbox)
            {
                lockViewbox.Width = 11;
                lockViewbox.Height = 11;
            }
        }

        _windowLinkGlyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Data = WindowLinkGeometry,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromRgb(142, 142, 150)),
            StrokeThickness = 1.75,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true
        };

        _windowLinkButton = new Button
        {
            Width = 13,
            Height = 34,
            Padding = new Thickness(0),
            ToolTip = "Link touching windows",
            Content = new Viewbox
            {
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                Child = _windowLinkGlyph
            },
            Visibility = Visibility.Visible,
            IsEnabled = false
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

        try
        {
            _windowLinkService.GroupSnappingEnabled = _magneticSnappingEnabled;
            var linkedBefore = _windowLinkService.IsLinked(_targetHwnd);
            var groupBefore = _windowLinkService.GetGroupSize(_targetHwnd);
            var touchingExternal = _windowLinkService.FindAttachablePartner(_targetHwnd) != IntPtr.Zero;
            var linkedAfter = _windowLinkService.ToggleLink(_targetHwnd);

            if (_windowLinkButton is not null && linkedBefore && !touchingExternal && !linkedAfter)
            {
                _windowLinkButton.ToolTip = groupBefore > 1
                    ? $"Unlinked {groupBefore} windows"
                    : "Windows unlinked";
            }
        }
        catch
        {
            if (_windowLinkButton is not null)
            {
                _windowLinkButton.ToolTip = "MUX could not update this linked window group.";
            }
        }

        RefreshWindowLinkAvailability(force: true);
    }

    private void WindowLinkVisualTimer_Tick(object? sender, EventArgs e)
        => RefreshWindowLinkAvailability(force: false);

    private void WindowLinkService_Changed(object? sender, EventArgs e)
    {
        try { RefreshWindowLinkAvailability(force: true); } catch { }
    }

    private void RefreshWindowLinkAvailability(bool force = false)
    {
        if (_windowLinkButton is null || _windowLinkGlyph is null)
        {
            return;
        }

        var target = _targetHwnd;
        var linked = target != IntPtr.Zero && _windowLinkService?.IsLinked(target) == true;
        var groupSize = linked ? _windowLinkService?.GetGroupSize(target) ?? 0 : 0;
        var touchingExternal = false;

        if (target != IntPtr.Zero && _windowLinkService is not null)
        {
            try
            {
                touchingExternal = _windowLinkService.FindAttachablePartner(target) != IntPtr.Zero;
            }
            catch
            {
                touchingExternal = false;
            }
        }

        // An existing group can always be clicked: touching outsiders are absorbed; with no
        // outsiders the same control unlinks the group. An unlinked window enables as soon as any
        // other eligible window is physically touching it.
        var available = linked || touchingExternal;
        if (!force &&
            target == _lastWindowLinkTarget &&
            available == _lastWindowLinkAvailable &&
            linked == _lastWindowLinked &&
            groupSize == _lastWindowLinkGroupSize)
        {
            return;
        }

        _lastWindowLinkTarget = target;
        _lastWindowLinkAvailable = available;
        _lastWindowLinked = linked;
        _lastWindowLinkGroupSize = groupSize;

        _windowLinkButton.Visibility = Visibility.Visible;
        _windowLinkButton.IsEnabled = available;
        _windowLinkButton.Background = new SolidColorBrush(
            linked ? Color.FromRgb(62, 62, 69) : available ? Color.FromRgb(42, 42, 47) : Color.FromRgb(31, 31, 35));
        _windowLinkButton.Opacity = linked ? 1.0 : available ? 0.9 : 0.62;
        _windowLinkGlyph.Stroke = new SolidColorBrush(
            linked ? Color.FromRgb(245, 245, 247) : available ? Color.FromRgb(214, 214, 220) : Color.FromRgb(142, 142, 150));

        _windowLinkButton.ToolTip = target == IntPtr.Zero
            ? "Link Windows · point at a window first"
            : linked && touchingExternal
                ? $"Link Windows · {groupSize} linked · click to absorb every touching window"
                : linked
                    ? $"Linked group · {groupSize} windows · click to unlink"
                    : available
                        ? "Link Windows · link the entire touching window cluster"
                        : "Link Windows · move another window flush against this one first";
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
            try { _windowLinkService.Changed -= WindowLinkService_Changed; } catch { }
            // The shared coordinator intentionally outlives the pill so linked groups survive
            // display filtering and pill recreation.
            _windowLinkService = null;
        }

        if (_windowLinkButton is not null)
        {
            _windowLinkButton.Click -= WindowLinkButton_Click;
        }
    }
}
