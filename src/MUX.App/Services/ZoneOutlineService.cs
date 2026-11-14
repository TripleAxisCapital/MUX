using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MUX.Core.Geometry;
using MUX.Core.Models;
using Forms = System.Windows.Forms;

namespace MUX.App.Services;

public sealed class ZoneOutlineService : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int HoverBandPx = 10;
    private const int SurfaceResizeBandPx = 56;
    private const int VkShift = 0x10;
    private const int VkLeftMouse = 0x01;
    private const int ToolbarGapPx = 8;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly string[] AspectPresets = ["16:9", "16:10", "21:9", "32:9", "4:3", "3:2"];
    private static readonly string[] ColorPresets = ["#000000", "#34343A", "#FFFFFF", "#007AFF", "#64D2FF", "#30D158", "#FF9F0A", "#FF453A", "#BF5AF2"];

    public static event EventHandler? LayoutEdited;

    private readonly Dictionary<Guid, OutlineVisual> _outlines = new();
    private readonly DispatcherTimer _hoverTimer;
    private DisplayProfile? _display;
    private LayoutProfile? _layout;
    private bool _enabled;
    private bool _showOutlines;
    private bool _shiftEditMode;
    private bool _shiftToolbarPinned;
    private DateTime _suppressLegacyHoverUntilUtc = DateTime.MinValue;
    private VirtualMonitorZone? _resizingZone;
    private PixelRect _resizeStartBounds;
    private NativePoint _resizeStartCursor;
    private int _resizeHorizontalEdge;
    private int _resizeVerticalEdge;
    private VirtualMonitorZone? _surfaceGestureZone;
    private Border? _surfaceGestureBorder;
    private PixelRect _surfaceGestureStartRect;
    private NativePoint _surfaceGestureStartCursor;
    private int _surfaceGestureHorizontalEdge;
    private int _surfaceGestureVerticalEdge;
    private bool _surfaceGestureChanged;
    private double _defaultThickness = 2.0;
    private Window? _toolbarWindow;
    private Border? _toolbarShell;
    private VirtualMonitorZone? _toolbarZone;
    private TextBox? _sizeBox;
    private ComboBox? _aspectBox;
    private ComboBox? _thicknessBox;
    private Button? _colorButton;
    private Popup? _colorPopup;
    private bool _syncingToolbar;
    private bool _choosingColor;
    private DateTime _lastHoverUtc = DateTime.MinValue;
    private bool _draggingZone;
    private NativePoint _dragStartCursor;
    private double _dragStartX;
    private double _dragStartY;
    private Button? _moveButton;

    public ZoneOutlineService()
    {
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _hoverTimer.Tick += HoverTimer_Tick;
    }

    public void Configure(DisplayProfile? display, LayoutProfile? layout, bool enabled, bool showOutlines, double thickness)
    {
        _display = display;
        _layout = layout;
        _enabled = enabled;
        _showOutlines = showOutlines;
        _defaultThickness = Math.Clamp(thickness, 1.0, 6.0);

        if (!enabled || display is null || layout is null || layout.Zones.Count == 0)
        {
            _shiftEditMode = false;
            ClearOutlines();
            HideToolbar(force: true);
            _hoverTimer.Stop();
            return;
        }

        // Keep the lightweight key poll alive even with persistent outlines turned off.
        // No mouse hook, global keyboard hook or focus change is required.
        if (!_hoverTimer.IsEnabled)
            _hoverTimer.Start();

        SyncOutlines();
    }

    private void SyncOutlines()
    {
        if (_display is null || _layout is null || (!_showOutlines && !_shiftEditMode))
        {
            ClearOutlines();
            return;
        }

        var activeIds = _layout.Zones.Select(zone => zone.Id).ToHashSet();
        foreach (var staleId in _outlines.Keys.Where(id => !activeIds.Contains(id)).ToList())
            Remove(staleId);

        foreach (var zone in _layout.Zones)
        {
            if (!_outlines.TryGetValue(zone.Id, out var visual))
            {
                visual = CreateOutline(zone.Id);
                _outlines[zone.Id] = visual;
            }
            UpdateOutline(zone, visual);
        }

        if (_toolbarZone is not null)
        {
            var current = _layout.Zones.FirstOrDefault(zone => zone.Id == _toolbarZone.Id);
            if (current is null)
                HideToolbar();
            else
            {
                _toolbarZone = current;
                SyncToolbar(current);
                PositionToolbar(current);
            }
        }
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        if (!_enabled || _display is null || _layout is null || !GetCursorPos(out var cursor))
            return;

        // GetCursorPos returns virtual-desktop device pixels, matching DisplayProfile
        // LeftPx/TopPx/WidthPx/HeightPx even with negative secondary-monitor origins.
        // Shift must never cover a display other than the one beneath the pointer.
        var cursorOnDisplay = CursorOnDisplay(cursor);
        var shiftHeld = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
        var activeDrag = _resizingZone is not null || _draggingZone || _surfaceGestureZone is not null;
        var shouldEdit = shiftHeld && cursorOnDisplay;

        // Preserve a resize/move gesture begun in edit mode when Shift is released
        // or the pointer briefly crosses the physical monitor boundary.
        if (activeDrag)
            shouldEdit = true;
        else if (!_shiftEditMode && (GetAsyncKeyState(VkLeftMouse) & 0x8000) != 0)
            shouldEdit = false; // Shift must not hijack an existing app drag.

        if (shouldEdit != _shiftEditMode)
        {
            _shiftEditMode = shouldEdit;
            if (!shouldEdit)
            {
                // A pinned toolbar belongs to the Shift session, not the legacy
                // hover timer. Clear it immediately on release / monitor exit.
                HideToolbar(force: true);
                _suppressLegacyHoverUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            }
            SyncOutlines();
        }

        if (_shiftEditMode)
        {
            if (_shiftToolbarPinned && _toolbarZone is not null && _toolbarWindow?.IsVisible != true)
                ShowToolbar(_toolbarZone);

            // Once a quadrant menu has been opened, keep it visible and on the
            // same quadrant for the entire Shift session, regardless of hover.
            // Only release Shift, leave this monitor or finish an active drag
            // with Shift already released to close the menu.
            _lastHoverUtc = DateTime.UtcNow;
            return;
        }

        if (!cursorOnDisplay)
        {
            HideToolbar(force: true);
            return;
        }

        if (IsToolbarInteracting() || CursorInsideToolbar(cursor))
        {
            _lastHoverUtc = DateTime.UtcNow;
            return;
        }

        // Preserve original hover-toolbar behavior when persistent outlines are on.
        var hovered = _showOutlines && DateTime.UtcNow >= _suppressLegacyHoverUntilUtc
            ? FindTopEdgeZone(cursor)
            : null;
        if (hovered is not null)
        {
            _lastHoverUtc = DateTime.UtcNow;
            ShowToolbar(hovered);
            return;
        }

        if (_toolbarWindow?.IsVisible == true &&
            DateTime.UtcNow - _lastHoverUtc > TimeSpan.FromMilliseconds(650))
            HideToolbar();
    }

    private bool CursorOnDisplay(NativePoint cursor)
    {
        var display = _display;
        return display is not null && display.WidthPx > 0 && display.HeightPx > 0 &&
               (long)cursor.X >= display.LeftPx &&
               (long)cursor.X < (long)display.LeftPx + display.WidthPx &&
               (long)cursor.Y >= display.TopPx &&
               (long)cursor.Y < (long)display.TopPx + display.HeightPx;
    }

    private VirtualMonitorZone? FindTopEdgeZone(NativePoint cursor)
    {
        if (_display is null || _layout is null)
        {
            return null;
        }

        return _layout.Zones
            .Select(zone => new { Zone = zone, Rect = DisplayGeometry.ZoneToPixels(_display, zone) })
            .Where(item => cursor.X >= item.Rect.Left && cursor.X < item.Rect.Right && Math.Abs(cursor.Y - item.Rect.Top) <= HoverBandPx)
            .OrderBy(item => Math.Abs(cursor.Y - item.Rect.Top))
            .Select(item => item.Zone)
            .FirstOrDefault();
    }

    private bool CursorInsideToolbar(NativePoint cursor)
    {
        if (_toolbarWindow?.IsVisible != true)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(_toolbarWindow).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) &&
               cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    private bool IsToolbarInteracting()
    {
        return _choosingColor ||
               _sizeBox?.IsKeyboardFocusWithin == true ||
               _aspectBox?.IsDropDownOpen == true ||
               _thicknessBox?.IsDropDownOpen == true ||
               _colorPopup?.IsOpen == true;
    }

    private void ShowToolbar(VirtualMonitorZone zone)
    {
        EnsureToolbar();
        if (_toolbarWindow is null)
        {
            return;
        }

        var changedZone = _toolbarZone?.Id != zone.Id;
        _toolbarZone = zone;
        if (changedZone)
        {
            SyncToolbar(zone);
        }

        if (!_toolbarWindow.IsVisible)
        {
            _toolbarWindow.Opacity = 0;
            _toolbarWindow.Show();
            _toolbarWindow.UpdateLayout();
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _toolbarWindow.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        PositionToolbar(zone);
    }

    private void HideToolbar(bool force = false)
    {
        if (!force && (_draggingZone || _resizingZone is not null || IsToolbarInteracting()))
            return;

        _shiftToolbarPinned = false;
        _colorPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        _toolbarWindow?.Hide();
        _toolbarZone = null;
    }

    private void EnsureToolbar()
    {
        if (_toolbarWindow is not null)
        {
            return;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _moveButton = CreateToolbarButton("Move", "Drag to reposition this virtual monitor", 62);
        _moveButton.Cursor = Cursors.SizeAll;
        _moveButton.PreviewMouseLeftButtonDown += MoveButton_MouseLeftButtonDown;
        _moveButton.PreviewMouseMove += MoveButton_MouseMove;
        _moveButton.PreviewMouseLeftButtonUp += MoveButton_MouseLeftButtonUp;
        row.Children.Add(_moveButton);
        row.Children.Add(CreateDivider());

        _sizeBox = new TextBox
        {
            Width = 88,
            Height = 36,
            Padding = new Thickness(9, 6, 9, 6),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Physical diagonal (inches)"
        };
        SetStyle(_sizeBox, "MuxTextBox");
        _sizeBox.KeyDown += SizeBox_KeyDown;
        _sizeBox.LostKeyboardFocus += (_, _) => ApplySizeFromToolbar();
        row.Children.Add(_sizeBox);

        _aspectBox = new ComboBox
        {
            Width = 132,
            Height = 36,
            MinHeight = 36,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(7, 6, 6, 6),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = "Aspect ratio · custom sizes are retained"
        };
        SetStyle(_aspectBox, "MuxComboBox");
        foreach (var preset in AspectPresets)
        {
            _aspectBox.Items.Add(new ComboBoxItem { Content = preset });
        }
        _aspectBox.SelectionChanged += AspectBox_SelectionChanged;
        row.Children.Add(_aspectBox);
        row.Children.Add(CreateDivider());

        var clone = CreateToolbarButton("⧉", "Clone this virtual monitor", 38);
        clone.FontSize = 17;
        clone.Click += (_, _) => CloneCurrentZone();
        row.Children.Add(clone);

        _colorButton = CreateColorButton();
        _colorButton.ToolTip = "Outline color";
        _colorButton.Click += ColorButton_Click;
        row.Children.Add(_colorButton);

        _thicknessBox = new ComboBox
        {
            Width = 92,
            Height = 36,
            MinHeight = 36,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(7, 6, 6, 6),
            ToolTip = "Outline thickness"
        };
        SetStyle(_thicknessBox, "MuxComboBox");
        for (var i = 1; i <= 6; i++)
        {
            _thicknessBox.Items.Add(new ComboBoxItem { Content = $"{i} px", Tag = (double)i });
        }
        _thicknessBox.SelectionChanged += ThicknessBox_SelectionChanged;
        row.Children.Add(_thicknessBox);

        _toolbarShell = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(246, 20, 20, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(92, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(23),
            Padding = new Thickness(10, 7, 10, 7),
            Child = row,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.42,
                Color = Colors.Black
            }
        };

        _toolbarWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Content = _toolbarShell
        };
        _toolbarWindow.SourceInitialized += (_, _) => MakeToolbarWindow(_toolbarWindow);
        _toolbarWindow.MouseEnter += (_, _) => _lastHoverUtc = DateTime.UtcNow;
    }

    private static Button CreateToolbarButton(string content, string tooltip, double width)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 36,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0),
            ToolTip = tooltip,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 240)),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        SetStyle(button, "MuxButton");
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.Padding = new Thickness(8, 5, 8, 5);
        return button;
    }

    private static Border CreateDivider()
    {
        return new Border
        {
            Width = 1,
            Height = 20,
            Margin = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button CreateColorButton()
    {
        var ellipse = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            StrokeThickness = 1
        };

        var button = CreateToolbarButton(string.Empty, "Outline color", 38);
        button.Content = ellipse;
        return button;
    }

    private void SyncToolbar(VirtualMonitorZone zone)
    {
        if (_sizeBox is null || _aspectBox is null || _thicknessBox is null || _colorButton is null)
        {
            return;
        }

        _syncingToolbar = true;
        try
        {
            _sizeBox.Text = zone.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture) + "″";

            var aspect = zone.AspectLabel;
            var aspectItem = _aspectBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Content?.ToString(), aspect, StringComparison.OrdinalIgnoreCase));
            if (aspectItem is null)
            {
                aspectItem = new ComboBoxItem { Content = aspect };
                _aspectBox.Items.Add(aspectItem);
            }
            _aspectBox.SelectedItem = aspectItem;

            var effectiveThickness = EffectiveThickness(zone);
            _thicknessBox.SelectedItem = _thicknessBox.Items.OfType<ComboBoxItem>()
                .OrderBy(item => Math.Abs((double)(item.Tag ?? 1.0) - effectiveThickness))
                .FirstOrDefault();

            UpdateColorButton(zone);
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    private void UpdateColorButton(VirtualMonitorZone zone)
    {
        if (_colorButton?.Content is Ellipse ellipse)
        {
            ellipse.Fill = BrushFromHex(zone.OutlineColor, Colors.Black);
        }
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        ApplySizeFromToolbar();
        Keyboard.ClearFocus();
    }

    private void ApplySizeFromToolbar()
    {
        if (_syncingToolbar || _toolbarZone is null || _display is null || _sizeBox is null)
        {
            return;
        }

        var raw = _sizeBox.Text.Replace("″", string.Empty).Replace("\"", string.Empty).Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var diagonal) || diagonal < 5 || diagonal > 500 ||
            !FitsDisplay(diagonal, _toolbarZone.AspectWidth, _toolbarZone.AspectHeight))
        {
            _sizeBox.Text = _toolbarZone.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture) + "″";
            return;
        }

        if (Math.Abs(diagonal - _toolbarZone.DiagonalInches) < 0.001)
        {
            return;
        }

        _toolbarZone.DiagonalInches = diagonal;
        DisplayGeometry.ClampZoneToDisplay(_display, _toolbarZone);
        UpdateCurrentZoneVisual();
        SyncToolbar(_toolbarZone);
        RaiseLayoutEdited();
    }

    private void AspectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || _toolbarZone is null || _display is null || _aspectBox?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (!TryParseAspect(item.Content?.ToString(), out var width, out var height) ||
            !FitsDisplay(_toolbarZone.DiagonalInches, width, height))
        {
            SyncToolbar(_toolbarZone);
            return;
        }

        if (Math.Abs(width - _toolbarZone.AspectWidth) < 0.001 && Math.Abs(height - _toolbarZone.AspectHeight) < 0.001)
        {
            return;
        }

        _toolbarZone.AspectWidth = width;
        _toolbarZone.AspectHeight = height;
        DisplayGeometry.ClampZoneToDisplay(_display, _toolbarZone);
        UpdateCurrentZoneVisual();
        RaiseLayoutEdited();
    }

    private void ThicknessBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || _toolbarZone is null || _thicknessBox?.SelectedItem is not ComboBoxItem { Tag: double thickness })
        {
            return;
        }

        _toolbarZone.OutlineThickness = Math.Clamp(thickness, 1.0, 6.0);
        UpdateCurrentZoneVisual();
        RaiseLayoutEdited();
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_colorButton is null || _toolbarZone is null)
        {
            return;
        }

        if (_colorPopup is null)
        {
            _colorPopup = BuildColorPopup(_colorButton);
        }

        _colorPopup.IsOpen = !_colorPopup.IsOpen;
        _lastHoverUtc = DateTime.UtcNow;
    }

    private Popup BuildColorPopup(Button target)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in ColorPresets)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(4),
                Margin = new Thickness(2),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            SetStyle(swatch, "MuxButton");
            swatch.Background = Brushes.Transparent;
            swatch.BorderBrush = Brushes.Transparent;
            swatch.Padding = new Thickness(4);
            swatch.Content = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = BrushFromHex(hex, Colors.Black),
                Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                StrokeThickness = 1
            };
            swatch.Click += (_, _) => ApplyOutlineColor(hex);
            panel.Children.Add(swatch);
        }

        var more = new Button
        {
            Content = "…",
            Width = 28,
            Height = 26,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Choose a custom color"
        };
        SetStyle(more, "MuxButton");
        more.Padding = new Thickness(0);
        more.Click += (_, _) => ChooseCustomColor();
        panel.Children.Add(more);

        return new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 7,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 24, 24, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(5),
                Child = panel,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = 0.4, Color = Colors.Black }
            }
        };
    }

    private void ApplyOutlineColor(string hex)
    {
        if (_toolbarZone is null)
        {
            return;
        }

        _toolbarZone.OutlineColor = hex;
        UpdateColorButton(_toolbarZone);
        UpdateCurrentZoneVisual();
        if (_colorPopup is not null)
        {
            _colorPopup.IsOpen = false;
        }
        RaiseLayoutEdited();
    }

    private void ChooseCustomColor()
    {
        if (_toolbarZone is null)
        {
            return;
        }

        _choosingColor = true;
        try
        {
            using var dialog = new Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                SolidColorOnly = false
            };

            var current = ColorFromHex(_toolbarZone.OutlineColor, Colors.Black);
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                ApplyOutlineColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            }
        }
        finally
        {
            _choosingColor = false;
            _lastHoverUtc = DateTime.UtcNow;
        }
    }

    private void CloneCurrentZone()
    {
        if (_toolbarZone is null || _display is null || _layout is null)
        {
            return;
        }

        var clone = new VirtualMonitorZone
        {
            Name = string.IsNullOrWhiteSpace(_toolbarZone.Name) ? "Monitor copy" : _toolbarZone.Name + " copy",
            DiagonalInches = _toolbarZone.DiagonalInches,
            AspectWidth = _toolbarZone.AspectWidth,
            AspectHeight = _toolbarZone.AspectHeight,
            OutlineColor = _toolbarZone.OutlineColor,
            OutlineThickness = _toolbarZone.OutlineThickness
        };

        FindOpenPosition(clone);
        _layout.Zones.Add(clone);
        SyncOutlines();
        RaiseLayoutEdited();
    }

    private void MoveButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_toolbarZone is null || _display is null || _moveButton is null || !GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        _draggingZone = true;
        _dragStartX = _toolbarZone.XInches;
        _dragStartY = _toolbarZone.YInches;
        _moveButton.CaptureMouse();
        e.Handled = true;
    }

    private void MoveButton_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingZone || _toolbarZone is null || _display is null || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var cursor))
        {
            return;
        }

        var ppi = DisplayGeometry.PixelsPerInch(_display);
        _toolbarZone.XInches = _dragStartX + (cursor.X - _dragStartCursor.X) / ppi;
        _toolbarZone.YInches = _dragStartY + (cursor.Y - _dragStartCursor.Y) / ppi;
        DisplayGeometry.ClampZoneToDisplay(_display, _toolbarZone);
        UpdateCurrentZoneVisual();
        _lastHoverUtc = DateTime.UtcNow;
        e.Handled = true;
    }

    private void MoveButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingZone)
        {
            return;
        }

        _draggingZone = false;
        _moveButton?.ReleaseMouseCapture();
        _lastHoverUtc = DateTime.UtcNow;
        RaiseLayoutEdited();
        e.Handled = true;
    }

    private void UpdateCurrentZoneVisual()
    {
        if (_toolbarZone is null)
        {
            return;
        }

        if (_outlines.TryGetValue(_toolbarZone.Id, out var visual))
        {
            UpdateOutline(_toolbarZone, visual);
        }

        PositionToolbar(_toolbarZone);
    }

    private void UpdateOutline(VirtualMonitorZone zone, OutlineVisual visual)
    {
        visual.Border.BorderThickness = new Thickness(_shiftEditMode ? 1.5 : EffectiveThickness(zone));
        visual.Border.BorderBrush = _shiftEditMode
            ? new SolidColorBrush(Color.FromArgb(225, 245, 245, 249))
            : BrushFromHex(zone.OutlineColor, Colors.Black);
        visual.Border.Background = _shiftEditMode
            ? new SolidColorBrush(Color.FromArgb(88, 240, 240, 245))
            : Brushes.Transparent;
        visual.Label.Text = $"{zone.DiagonalInches:0.#}″  ·  {zone.AspectWidth / zone.AspectHeight:0.##}:1";
        visual.Label.Visibility = _shiftEditMode ? Visibility.Visible : Visibility.Collapsed;
        visual.Menu.Visibility = _shiftEditMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var grip in visual.Grips)
            grip.Visibility = _shiftEditMode ? Visibility.Visible : Visibility.Collapsed;

        visual.Window.IsHitTestVisible = _shiftEditMode;
        visual.Border.IsHitTestVisible = _shiftEditMode;
        SetClickThrough(visual.Window, !_shiftEditMode);
        if (_display is not null)
            Position(visual.Window, DisplayGeometry.ZoneToPixels(_display, zone));
    }

    private double EffectiveThickness(VirtualMonitorZone zone)
    {
        return zone.OutlineThickness is >= 1.0 and <= 6.0 ? zone.OutlineThickness : _defaultThickness;
    }

    private bool FitsDisplay(double diagonal, double aspectWidth, double aspectHeight)
    {
        if (_display is null || diagonal < 5 || diagonal > 500 || aspectWidth <= 0 || aspectHeight <= 0)
        {
            return false;
        }

        var size = DisplayGeometry.PhysicalSizeFromDiagonal(diagonal, aspectWidth, aspectHeight);
        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        return size.Width <= displaySize.Width + 0.01 && size.Height <= displaySize.Height + 0.01;
    }

    private static bool TryParseAspect(string? text, out double width, out double height)
    {
        width = 0;
        height = 0;
        var parts = text?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 2 } &&
               double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out height) &&
               width > 0 && height > 0;
    }

    private void FindOpenPosition(VirtualMonitorZone zone)
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        var displaySize = DisplayGeometry.DisplayPhysicalSize(_display);
        var size = DisplayGeometry.PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);
        const double step = 0.5;

        for (var y = 0.0; y <= Math.Max(0, displaySize.Height - size.Height); y += step)
        {
            for (var x = 0.0; x <= Math.Max(0, displaySize.Width - size.Width); x += step)
            {
                if (!_layout.Zones.Any(existing => IntersectsInches(x, y, size, existing)))
                {
                    zone.XInches = x;
                    zone.YInches = y;
                    return;
                }
            }
        }

        zone.XInches = Math.Clamp((_toolbarZone?.XInches ?? 0) + 0.75, 0, Math.Max(0, displaySize.Width - size.Width));
        zone.YInches = Math.Clamp((_toolbarZone?.YInches ?? 0) + 0.75, 0, Math.Max(0, displaySize.Height - size.Height));
    }

    private static bool IntersectsInches(double x, double y, SizeD size, VirtualMonitorZone existing)
    {
        var other = DisplayGeometry.PhysicalSizeFromDiagonal(existing.DiagonalInches, existing.AspectWidth, existing.AspectHeight);
        return x < existing.XInches + other.Width && x + size.Width > existing.XInches &&
               y < existing.YInches + other.Height && y + size.Height > existing.YInches;
    }

    private void PositionToolbar(VirtualMonitorZone zone)
    {
        if (_toolbarWindow?.IsVisible != true || _display is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_toolbarWindow).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var toolbarRect))
        {
            return;
        }

        var zoneRect = DisplayGeometry.ZoneToPixels(_display, zone);
        var width = toolbarRect.Right - toolbarRect.Left;
        var height = toolbarRect.Bottom - toolbarRect.Top;
        var minX = _display.LeftPx + 6;
        var maxX = Math.Max(minX, _display.LeftPx + _display.WidthPx - width - 6);
        var x = Math.Clamp(zoneRect.Left + (zoneRect.Width - width) / 2, minX, maxX);
        var y = zoneRect.Top - height - ToolbarGapPx;
        if (y < _display.TopPx + 4)
        {
            y = zoneRect.Top + 10;
        }

        SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    private static void SetStyle(FrameworkElement element, string key)
    {
        if (System.Windows.Application.Current.TryFindResource(key) is Style style)
        {
            element.Style = style;
        }
    }

    private OutlineVisual CreateOutline(Guid zoneId)
    {
        var content = new Grid { IsHitTestVisible = true };
        var label = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Text = "",
            Margin = new Thickness(12, 9, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        content.Children.Add(label);

        var menu = CreateToolbarButton("•••", "Open this quadrant's controls", 36);
        menu.Height = 28;
        menu.Margin = new Thickness(0, 7, 8, 0);
        menu.HorizontalAlignment = HorizontalAlignment.Right;
        menu.VerticalAlignment = VerticalAlignment.Top;
        menu.Visibility = Visibility.Collapsed;
        menu.Click += (_, _) =>
        {
            var zone = _layout?.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
            if (zone is null || !_shiftEditMode) return;
            _shiftToolbarPinned = true;
            _lastHoverUtc = DateTime.UtcNow;
            ShowToolbar(zone);
        };
        Panel.SetZIndex(menu, 20);
        content.Children.Add(menu);

        var grips = new List<Thumb>();
        var positions = new (int X, int Y, HorizontalAlignment Horizontal, VerticalAlignment Vertical, Cursor Cursor)[]
        {
            (-1, -1, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE),
            ( 0, -1, HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS),
            ( 1, -1, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW),
            (-1,  0, HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE),
            ( 1,  0, HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE),
            (-1,  1, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW),
            ( 0,  1, HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS),
            ( 1,  1, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE)
        };
        foreach (var grip in positions)
        {
            var thumb = new Thumb
            {
                Width = grip.X == 0 ? 27 : 15,
                Height = grip.Y == 0 ? 27 : 15,
                HorizontalAlignment = grip.Horizontal,
                VerticalAlignment = grip.Vertical,
                Cursor = grip.Cursor,
                Background = new SolidColorBrush(Color.FromArgb(235, 250, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(95, 95, 103)),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
                ToolTip = "Drag edge or corner to resize"
            };
            thumb.DragStarted += (_, _) => BeginZoneResize(zoneId, grip.X, grip.Y);
            thumb.DragDelta += (_, _) => UpdateZoneResize();
            thumb.DragCompleted += (_, _) => FinishZoneResize();
            Panel.SetZIndex(thumb, 10);
            content.Children.Add(thumb);
            grips.Add(thumb);
        }

        var border = new Border
        {
            Tag = zoneId,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Child = content
        };
        // Every pixel of the surface is actionable during Shift editing:
        // the 56-device-pixel perimeter resizes, the remaining interior moves.
        // Thumb handles and the menu button retain priority over the surface.
        border.PreviewMouseLeftButtonDown += ZoneSurface_MouseLeftButtonDown;
        border.PreviewMouseMove += ZoneSurface_MouseMove;
        border.PreviewMouseLeftButtonUp += ZoneSurface_MouseLeftButtonUp;
        border.LostMouseCapture += (_, _) =>
        {
            if (ReferenceEquals(_surfaceGestureBorder, border))
                FinishZoneSurfaceGesture();
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            Content = border
        };
        window.SourceInitialized += (_, _) => SetClickThrough(window, true);
        window.Show();
        return new OutlineVisual(window, border, label, menu, grips);
    }

    private static bool OriginatesFromSurfaceControl(DependencyObject? source, DependencyObject surface)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, surface);
             current = VisualTreeHelper.GetParent(current))
        {
            // Never hijack the eight existing resize grips or the quadrant menu.
            if (current is Thumb or ButtonBase)
                return true;
        }
        return false;
    }

    private static (int Horizontal, int Vertical) HitTestSurfaceEdges(PixelRect rect, NativePoint cursor)
    {
        // Use native Windows device pixels rather than WPF DIPs. On smaller
        // workspaces shrink the hit bands to leave a usable draggable center.
        var bandX = Math.Min(SurfaceResizeBandPx, Math.Max(8, rect.Width / 3));
        var bandY = Math.Min(SurfaceResizeBandPx, Math.Max(8, rect.Height / 3));

        var horizontal = cursor.X - rect.Left < bandX ? -1
            : rect.Right - cursor.X <= bandX ? 1 : 0;
        var vertical = cursor.Y - rect.Top < bandY ? -1
            : rect.Bottom - cursor.Y <= bandY ? 1 : 0;
        return (horizontal, vertical);
    }

    private static Cursor CursorForSurfaceEdges(int horizontal, int vertical)
    {
        if (horizontal != 0 && vertical != 0)
            return horizontal == vertical ? Cursors.SizeNWSE : Cursors.SizeNESW;
        if (horizontal != 0)
            return Cursors.SizeWE;
        if (vertical != 0)
            return Cursors.SizeNS;
        return Cursors.SizeAll;
    }

    private void ZoneSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_shiftEditMode || _display is null || _layout is null ||
            _resizingZone is not null || _draggingZone || _surfaceGestureZone is not null ||
            sender is not Border border || border.Tag is not Guid zoneId ||
            OriginatesFromSurfaceControl(e.OriginalSource as DependencyObject, border) ||
            !GetCursorPos(out var cursor))
            return;

        var zone = _layout.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
        if (zone is null)
            return;

        var bounds = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: true);
        var edges = HitTestSurfaceEdges(bounds, cursor);
        if (!border.CaptureMouse())
            return;

        _surfaceGestureZone = zone;
        _surfaceGestureBorder = border;
        _surfaceGestureStartRect = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
        _surfaceGestureStartCursor = cursor;
        _surfaceGestureHorizontalEdge = edges.Horizontal;
        _surfaceGestureVerticalEdge = edges.Vertical;
        _surfaceGestureChanged = false;
        border.Cursor = CursorForSurfaceEdges(edges.Horizontal, edges.Vertical);
        _lastHoverUtc = DateTime.UtcNow;
        e.Handled = true;
    }

    private void ZoneSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_shiftEditMode || _display is null || sender is not Border border ||
            border.Tag is not Guid zoneId)
            return;

        if (ReferenceEquals(_surfaceGestureBorder, border))
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                FinishZoneSurfaceGesture();
            else
                UpdateZoneSurfaceGesture();
            e.Handled = true;
            return;
        }

        if (_surfaceGestureZone is not null ||
            OriginatesFromSurfaceControl(e.OriginalSource as DependencyObject, border) ||
            !GetCursorPos(out var cursor))
            return;

        var zone = _layout?.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
        if (zone is null)
            return;

        var edges = HitTestSurfaceEdges(DisplayGeometry.ZoneToPixels(_display, zone), cursor);
        border.Cursor = CursorForSurfaceEdges(edges.Horizontal, edges.Vertical);
    }

    private void ZoneSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || !ReferenceEquals(_surfaceGestureBorder, border))
            return;

        UpdateZoneSurfaceGesture(); // Include the exact final cursor position.
        FinishZoneSurfaceGesture();
        e.Handled = true;
    }

    private void UpdateZoneSurfaceGesture()
    {
        if (_surfaceGestureZone is null || _display is null ||
            !GetCursorPos(out var cursor))
            return;

        var dx = cursor.X - _surfaceGestureStartCursor.X;
        var dy = cursor.Y - _surfaceGestureStartCursor.Y;
        var start = _surfaceGestureStartRect;
        var isResize = _surfaceGestureHorizontalEdge != 0 || _surfaceGestureVerticalEdge != 0;

        if (isResize)
        {
            var resized = DisplayGeometry.ResizeZoneRectangle(
                start, dx, dy, _surfaceGestureHorizontalEdge, _surfaceGestureVerticalEdge,
                _display.WidthPx, _display.HeightPx);
            if (resized.Equals(DisplayGeometry.ZoneToPixels(_display, _surfaceGestureZone, includeDisplayOffset: false)))
                return;
            DisplayGeometry.SetZonePixelBounds(_display, _surfaceGestureZone, resized);
        }
        else
        {
            // Moving a workspace must not alter its exact aspect ratio or diagonal.
            var left = Math.Clamp((long)start.Left + dx, 0, Math.Max(0, _display.WidthPx - start.Width));
            var top = Math.Clamp((long)start.Top + dy, 0, Math.Max(0, _display.HeightPx - start.Height));
            var ppi = DisplayGeometry.PixelsPerInch(_display);
            var newX = left / ppi;
            var newY = top / ppi;
            if (Math.Abs(newX - _surfaceGestureZone.XInches) < 0.00001 &&
                Math.Abs(newY - _surfaceGestureZone.YInches) < 0.00001)
                return;
            _surfaceGestureZone.XInches = newX;
            _surfaceGestureZone.YInches = newY;
        }

        _surfaceGestureChanged = true;
        if (_outlines.TryGetValue(_surfaceGestureZone.Id, out var visual))
            UpdateOutline(_surfaceGestureZone, visual);

        if (_toolbarZone?.Id == _surfaceGestureZone.Id)
        {
            if (isResize)
                SyncToolbar(_surfaceGestureZone);
            PositionToolbar(_surfaceGestureZone);
        }
    }

    private void FinishZoneSurfaceGesture()
    {
        if (_surfaceGestureZone is null)
            return;

        var border = _surfaceGestureBorder;
        var changed = _surfaceGestureChanged;
        _surfaceGestureZone = null;
        _surfaceGestureBorder = null;
        _surfaceGestureChanged = false;
        if (border?.IsMouseCaptured == true)
            border.ReleaseMouseCapture();

        _lastHoverUtc = DateTime.UtcNow;
        if (changed)
            RaiseLayoutEdited(); // Persist once, not on every mouse movement.
    }

    private void BeginZoneResize(Guid zoneId, int horizontal, int vertical)
    {
        if (_display is null || _layout is null || !GetCursorPos(out _resizeStartCursor))
            return;
        var zone = _layout.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
        if (zone is null) return;

        _resizingZone = zone;
        _resizeStartBounds = DisplayGeometry.ZoneToPixels(_display, zone, includeDisplayOffset: false);
        _resizeHorizontalEdge = horizontal;
        _resizeVerticalEdge = vertical;
    }

    private void UpdateZoneResize()
    {
        if (_resizingZone is null || _display is null || !GetCursorPos(out var cursor))
            return;

        var rect = DisplayGeometry.ResizeZoneRectangle(
            _resizeStartBounds, cursor.X - _resizeStartCursor.X, cursor.Y - _resizeStartCursor.Y,
            _resizeHorizontalEdge, _resizeVerticalEdge, _display.WidthPx, _display.HeightPx);
        DisplayGeometry.SetZonePixelBounds(_display, _resizingZone, rect);
        if (_outlines.TryGetValue(_resizingZone.Id, out var visual))
            UpdateOutline(_resizingZone, visual);
        if (_toolbarZone?.Id == _resizingZone.Id)
        {
            SyncToolbar(_resizingZone);
            PositionToolbar(_resizingZone);
        }
    }

    private void FinishZoneResize()
    {
        if (_resizingZone is null)
            return;

        _resizingZone = null;
        _lastHoverUtc = DateTime.UtcNow;
        RaiseLayoutEdited();
    }

    private static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var desired = current | WsExToolWindow | WsExNoActivate;
        desired = clickThrough ? desired | WsExTransparent : desired & ~WsExTransparent;
        if (current != desired)
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(desired));
    }

    private static void MakeToolbarWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(current | WsExToolWindow));
    }

    private static void Position(Window window, PixelRect rect)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate | SwpShowWindow);
    }

    private void Remove(Guid id)
    {
        if (!_outlines.Remove(id, out var visual))
        {
            return;
        }

        visual.Window.Close();
    }

    private void ClearOutlines()
    {
        foreach (var visual in _outlines.Values)
        {
            visual.Window.Close();
        }

        _outlines.Clear();
    }

    private static SolidColorBrush BrushFromHex(string? value, Color fallback)
    {
        return new SolidColorBrush(ColorFromHex(value, fallback));
    }

    private static Color ColorFromHex(string? value, Color fallback)
    {
        try
        {
            return value is null ? fallback : (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static void RaiseLayoutEdited()
    {
        LayoutEdited?.Invoke(null, EventArgs.Empty);
    }

    public void Dispose()
    {
        _hoverTimer.Stop();
        _colorPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        _toolbarWindow?.Close();
        _toolbarWindow = null;
        ClearOutlines();
    }

    private sealed record OutlineVisual(
        Window Window, Border Border, TextBlock Label, Button Menu, List<Thumb> Grips);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
