using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MUX.App.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace MUX.App.Windows;

public partial class CaptionResizePillWindow
{
    private WindowSizeLockService? _sizeLockService;
    private Button? _sizeLockButton;
    private ShapePath? _sizeLockGlyph;
    private DispatcherTimer? _sizeLockVisualTimer;
    private IntPtr _lastSizeLockVisualTarget;

    private static readonly Geometry LockedGeometry = Geometry.Parse(
        "M3,7 H5 V5 C5,2.8 6.8,1 9,1 C11.2,1 13,2.8 13,5 V7 H15 V16 H3 Z M7,7 H11 V5 C11,3.9 10.1,3 9,3 C7.9,3 7,3.9 7,5 Z");

    private static readonly Geometry UnlockedGeometry = Geometry.Parse(
        "M3,7 H11 V5 C11,3.9 10.1,3 9,3 C7.9,3 7,3.9 7,5 H5 C5,2.8 6.8,1 9,1 C11.2,1 13,2.8 13,5 V7 H15 V16 H3 Z");

    private void InitializeSizeLockControls()
    {
        if (_sizeLockService is null)
        {
            _sizeLockService = new WindowSizeLockService();
            _sizeLockService.Changed += SizeLockService_Changed;
        }

        EnsureSizeLockButton();

        if (_sizeLockVisualTimer is null)
        {
            _sizeLockVisualTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _sizeLockVisualTimer.Tick += SizeLockVisualTimer_Tick;
            _sizeLockVisualTimer.Start();
        }

        _lastSizeLockVisualTarget = _targetHwnd;
        UpdateSizeLockVisual();
    }

    private void EnsureSizeLockButton()
    {
        if (_sizeLockButton is not null || MagnetButton.Parent is not Grid controlGrid)
        {
            return;
        }

        // Keep the original 78-DIP utility footprint so the pill never grows farther right or
        // becomes clipped on narrow target windows. Three small utility icons share that area.
        controlGrid.ColumnDefinitions.Clear();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        if (CollapsedPanel.ColumnDefinitions.Count > 6)
        {
            CollapsedPanel.ColumnDefinitions[6].Width = new GridLength(78);
        }

        var currentSizeBorder = controlGrid.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetColumn(child) == 0);
        if (currentSizeBorder is not null)
        {
            currentSizeBorder.Padding = new Thickness(1, 0, 1, 0);
        }
        CurrentSizeText.FontSize = 8.8;

        MagnetButton.Width = 14;
        MagnetButton.Padding = new Thickness(0);
        Grid.SetColumn(MagnetButton, 2);
        if (MagnetButton.Content is Viewbox magnetViewbox)
        {
            magnetViewbox.Width = 12;
            magnetViewbox.Height = 12;
        }

        if (_edgeCoverButton is not null)
        {
            _edgeCoverButton.Width = 14;
            _edgeCoverButton.Padding = new Thickness(0);
            Grid.SetColumn(_edgeCoverButton, 4);
        }

        _sizeLockGlyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(Color.FromRgb(156, 156, 164)),
            Data = UnlockedGeometry,
            SnapsToDevicePixels = true
        };

        var glyphViewbox = new Viewbox
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Child = _sizeLockGlyph
        };

        _sizeLockButton = new Button
        {
            Width = 14,
            Height = 34,
            Padding = new Thickness(0),
            ToolTip = "Lock window position and size",
            Content = glyphViewbox
        };
        _sizeLockButton.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        _sizeLockButton.Click += SizeLockButton_Click;
        Grid.SetColumn(_sizeLockButton, 6);
        controlGrid.Children.Add(_sizeLockButton);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sizeLockService is null)
        {
            InitializeSizeLockControls();
        }

        if (_sizeLockService is null || _targetHwnd == IntPtr.Zero)
        {
            if (_sizeLockButton is not null)
            {
                _sizeLockButton.ToolTip = "Point at the window you want to lock, then click the padlock.";
            }
            return;
        }

        var wasLocked = _sizeLockService.IsLockedForWindow(_targetHwnd);
        var nowLocked = _sizeLockService.ToggleWindow(_targetHwnd);
        _lastSizeLockVisualTarget = _targetHwnd;
        UpdateSizeLockVisual();

        if (!wasLocked && !nowLocked && _sizeLockButton is not null)
        {
            _sizeLockButton.ToolTip = "Restore this window from minimized/maximized state before locking it.";
        }
    }

    private void SizeLockVisualTimer_Tick(object? sender, EventArgs e)
    {
        if (_lastSizeLockVisualTarget == _targetHwnd)
        {
            return;
        }

        _lastSizeLockVisualTarget = _targetHwnd;
        UpdateSizeLockVisual();
    }

    private void SizeLockService_Changed(object? sender, EventArgs e)
    {
        UpdateSizeLockVisual();
    }

    private void UpdateSizeLockVisual()
    {
        if (_sizeLockButton is null || _sizeLockGlyph is null)
        {
            return;
        }

        var locked = _sizeLockService?.IsLockedForWindow(_targetHwnd) == true;
        _sizeLockButton.Background = new SolidColorBrush(
            locked
                ? Color.FromRgb(62, 62, 69)
                : Color.FromRgb(31, 31, 35));
        _sizeLockButton.Opacity = _targetHwnd == IntPtr.Zero ? 0.45 : locked ? 1.0 : 0.72;
        _sizeLockGlyph.Fill = new SolidColorBrush(
            locked
                ? Color.FromRgb(245, 245, 247)
                : Color.FromRgb(156, 156, 164));
        _sizeLockGlyph.Data = locked ? LockedGeometry : UnlockedGeometry;

        _sizeLockButton.ToolTip = _targetHwnd == IntPtr.Zero
            ? "Window lock · Point at a window first"
            : locked
                ? "Window lock · Locked · Position and size fixed"
                : "Window lock · Unlocked";
    }

    private bool IsSizeLockedForCurrentTarget()
    {
        return _targetHwnd != IntPtr.Zero && _sizeLockService?.IsLockedForWindow(_targetHwnd) == true;
    }

    private void DisposeSizeLockControls()
    {
        if (_sizeLockVisualTimer is not null)
        {
            _sizeLockVisualTimer.Stop();
            _sizeLockVisualTimer.Tick -= SizeLockVisualTimer_Tick;
            _sizeLockVisualTimer = null;
        }

        if (_sizeLockService is not null)
        {
            _sizeLockService.Changed -= SizeLockService_Changed;
            _sizeLockService.Dispose();
            _sizeLockService = null;
        }

        if (_sizeLockButton is not null)
        {
            _sizeLockButton.Click -= SizeLockButton_Click;
        }
    }
}
