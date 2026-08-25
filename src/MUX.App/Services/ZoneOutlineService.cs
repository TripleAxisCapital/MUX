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

    public void Configure(DisplayProfile? display, LayoutProfile? layout, bool enabled, double thickness)
    {
        _display = display;
        _layout = layout;
        _enabled = enabled;
        _defaultThickness = Math.Clamp(thickness, 1.0, 6.0);

        if (!enabled || display is null || layout is null || layout.Zones.Count == 0)
        {
            ClearOutlines();
            HideToolbar();
            _hoverTimer.Stop();
            return;
        }

        if (!_hoverTimer.IsEnabled)
        {
            _hoverTimer.Start();
        }

        var activeIds = layout.Zones.Select(zone => zone.Id).ToHashSet();
        foreach (var staleId in _outlines.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            Remove(staleId);
        }

        foreach (var zone in layout.Zones)
        {
            if (!_outlines.TryGetValue(zone.Id, out var visual))
            {
                visual = CreateOutline();
                _outlines[zone.Id] = visual;
            }

            UpdateOutline(zone, visual);
        }

        if (_toolbarZone is not null)
        {
            var current = layout.Zones.FirstOrDefault(zone => zone.Id == _toolbarZone.Id);
            if (current is null)
            {
                HideToolbar();
            }
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
        if (!_enabled || _display is null || _layout is null || _layout.Zones.Count == 0 || !GetCursorPos(out var cursor))
        {
            HideToolbar();
            return;
        }

        if (_draggingZone || IsToolbarInteracting() || CursorInsideToolbar(cursor))
        {
            _lastHoverUtc = DateTime.UtcNow;
            return;
        }

        var hovered = FindTopEdgeZone(cursor);
        if (hovered is not null)
        {
            _lastHoverUtc = DateTime.UtcNow;
            ShowToolbar(hovered);
            return;
        }

        if (_toolbarWindow?.IsVisible == true && DateTime.UtcNow - _lastHoverUtc > TimeSpan.FromMilliseconds(480))
        {
            HideToolbar();
        }
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

    private void HideToolbar()
    {
        if (_draggingZone || IsToolbarInteracting())
        {
            return;
        }

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

        _moveButton = CreateToolbarButton("Move", "Drag to reposition this virtual monitor", 50);
        _moveButton.Cursor = Cursors.SizeAll;
        _moveButton.PreviewMouseLeftButtonDown += MoveButton_MouseLeftButtonDown;
        _moveButton.PreviewMouseMove += MoveButton_MouseMove;
        _moveButton.PreviewMouseLeftButtonUp += MoveButton_MouseLeftButtonUp;
        row.Children.Add(_moveButton);
        row.Children.Add(CreateDivider());

        _sizeBox = new TextBox
        {
            Width = 58,
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Diagonal size in inches"
        };
        SetStyle(_sizeBox, "MuxTextBox");
        _sizeBox.KeyDown += SizeBox_KeyDown;
        _sizeBox.LostKeyboardFocus += (_, _) => ApplySizeFromToolbar();
        row.Children.Add(_sizeBox);

        _aspectBox = new ComboBox
        {
            Width = 82,
            Height = 32,
            MinHeight = 32,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 5, 6, 5),
            ToolTip = "Aspect ratio"
        };
        SetStyle(_aspectBox, "MuxComboBox");
        foreach (var preset in AspectPresets)
        {
            _aspectBox.Items.Add(new ComboBoxItem { Content = preset });
        }
        _aspectBox.SelectionChanged += AspectBox_SelectionChanged;
        row.Children.Add(_aspectBox);
        row.Children.Add(CreateDivider());

        var clone = CreateToolbarButton("⧉", "Clone this virtual monitor", 34);
        clone.FontSize = 17;
        clone.Click += (_, _) => CloneCurrentZone();
        row.Children.Add(clone);

        _colorButton = CreateColorButton();
        _colorButton.ToolTip = "Outline color";
        _colorButton.Click += ColorButton_Click;
        row.Children.Add(_colorButton);

        _thicknessBox = new ComboBox
        {
            Width = 64,
            Height = 32,
            MinHeight = 32,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 5, 6, 5),
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
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(7, 6, 7, 6),
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
            Height = 32,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0),
            ToolTip = tooltip,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 240)),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand
        };
        SetStyle(button, "MuxButton");
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.Padding = new Thickness(8, 4, 8, 4);
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

        var button = CreateToolbarButton(string.Empty, "Outline color", 34);
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
        Configure(_display, _layout, true, _defaultThickness);
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
        visual.Border.BorderThickness = new Thickness(EffectiveThickness(zone));
        visual.Border.BorderBrush = BrushFromHex(zone.OutlineColor, Colors.Black);
        if (_display is not null)
        {
            Position(visual.Window, DisplayGeometry.ZoneToPixels(_display, zone));
        }
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

    private static OutlineVisual CreateOutline()
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
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

        window.SourceInitialized += (_, _) => MakeClickThrough(window);
        window.Show();
        return new OutlineVisual(window, border);
    }

    private static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var desired = current | WsExTransparent | WsExToolWindow | WsExNoActivate;
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

    private sealed record OutlineVisual(Window Window, Border Border);

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
