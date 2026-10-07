using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MUX.App.Services;

internal enum EdgeBarSide
{
    Top,
    Right,
    Bottom,
    Left
}

internal readonly record struct PixelPoint(int X, int Y);

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Shared, non-activating black edge overlay used by both per-window covers and per-monitor bars.
/// Only the small resize grip owns input; the remainder of each overlay is click-through.
/// For the top edge, the grip sits in a protected far-left zone. Approaching the caption anywhere
/// else fades the black surface away immediately so the native window can still be grabbed.
/// </summary>
internal sealed partial class EdgeBarOverlay : Window, IDisposable
{
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int MaNoActivate = 3;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int VkLeftButton = 0x01;

    private static readonly TimeSpan GripAnimation = TimeSpan.FromMilliseconds(90);

    private readonly EdgeBarSide _side;
    private readonly Func<int> _getThickness;
    private readonly Action<int> _setThickness;
    private readonly Canvas _canvas;
    private readonly Border _cover;
    private readonly Border _handle;
    private readonly ScaleTransform _handleScale;

    private HwndSource? _source;
    private PixelRect _frame;
    private PixelRect _overlayRect;
    private PixelRect _lastPositioned;
    private int _thickness;
    private int _grabRadius;
    private int _hoverRadius;
    private uint _dpi = 96;
    private bool _inputTransparent = true;
    private bool _dragging;
    private bool _shown;
    private bool _positioned;
    private bool _disposed;
    private bool _hot;
    private bool _topBarRevealed = true;
    private bool _leftButtonWasDown;
    private bool _pressStartedOnGrip;
    private PixelPoint _dragStart;
    private int _dragStartThickness;
    private DateTime _lastTopHideUtc = DateTime.MinValue;
    private double _lastGripOpacity = -1;

    public EdgeBarOverlay(EdgeBarSide side, Func<int> getThickness, Action<int> setThickness)
    {
        _side = side;
        _getThickness = getThickness;
        _setThickness = setThickness;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Cursor = side is EdgeBarSide.Top or EdgeBarSide.Bottom ? Cursors.SizeNS : Cursors.SizeWE;

        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _cover = new Border { Background = Brushes.Black, SnapsToDevicePixels = true };
        _handleScale = new ScaleTransform(side == EdgeBarSide.Top ? 0.94 : 0.86, side == EdgeBarSide.Top ? 0.94 : 0.86);
        _handle = BuildHandle(side, _handleScale);
        _canvas.Children.Add(_cover);
        _canvas.Children.Add(_handle);
        Content = _canvas;

        MouseLeftButtonDown += MouseDown;
        MouseMove += MouseMoveHandler;
        MouseLeftButtonUp += MouseUp;
        LostMouseCapture += LostCapture;
    }

    private static Border BuildHandle(EdgeBarSide side, ScaleTransform scale)
    {
        if (side == EdgeBarSide.Top)
        {
            return new Border
            {
                Width = 40,
                Height = 5,
                Background = new SolidColorBrush(Color.FromRgb(226, 226, 233)),
                CornerRadius = new CornerRadius(3),
                Opacity = 0.32,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
        }

        var grip = new StackPanel
        {
            Orientation = side is EdgeBarSide.Top or EdgeBarSide.Bottom
                ? Orientation.Vertical
                : Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (var i = 0; i < 3; i++)
        {
            grip.Children.Add(new Rectangle
            {
                Width = side is EdgeBarSide.Top or EdgeBarSide.Bottom ? 20 : 1.5,
                Height = side is EdgeBarSide.Top or EdgeBarSide.Bottom ? 1.5 : 20,
                RadiusX = 0.75,
                RadiusY = 0.75,
                Margin = side is EdgeBarSide.Top or EdgeBarSide.Bottom
                    ? new Thickness(0, i == 0 ? 0 : 2.5, 0, 0)
                    : new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                Fill = new SolidColorBrush(Color.FromRgb(218, 218, 223))
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(250, 24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(102, 102, 111)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = grip,
            Opacity = 0,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        SetInputTransparent(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source is not null)
        {
            try { _source.RemoveHook(WndProc); } catch { }
            _source = null;
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (msg == WmNcHitTest)
        {
            var point = PointFromLParam(lParam);
            handled = true;
            return new IntPtr(IsInteractivePoint(point) ? HtClient : HtTransparent);
        }

        return IntPtr.Zero;
    }

    public void Update(PixelRect frame, int thickness, uint dpi, PixelPoint? cursor, bool visible)
    {
        if (_disposed)
        {
            return;
        }

        _frame = frame;
        _thickness = Math.Max(1, thickness);
        _dpi = Math.Max(96u, dpi);
        _grabRadius = ScaleForDpi(12, _dpi);
        _hoverRadius = ScaleForDpi(_side == EdgeBarSide.Top ? 32 : 22, _dpi);
        _overlayRect = CalculateOverlayRect(frame, _thickness, _hoverRadius, _side);

        if (!visible || frame.Width <= 0 || frame.Height <= 0)
        {
            Park();
            return;
        }

        EnsureShownAndPositioned();
        DrawCover();
        if (cursor is PixelPoint point)
        {
            UpdateTopBarReveal(point);
        }
        DrawHandle(cursor);
    }

    public void UpdateInput(PixelPoint cursor, bool visible)
    {
        if (_disposed || !_shown)
        {
            return;
        }

        if (!visible)
        {
            SetInputTransparent(true);
            return;
        }

        if (_side == EdgeBarSide.Top)
        {
            var leftDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
            if (!leftDown)
            {
                _pressStartedOnGrip = false;
            }
            else if (!_leftButtonWasDown)
            {
                _pressStartedOnGrip = !_inputTransparent && IsTopGripHit(cursor);
            }
            _leftButtonWasDown = leftDown;

            UpdateTopBarReveal(cursor);
            DrawHandle(cursor);

            var gripAvailable = _dragging ||
                (IsInteractivePoint(cursor) && (!leftDown || _pressStartedOnGrip));
            SetInputTransparent(!gripAvailable);
            return;
        }

        DrawHandle(cursor);
        SetInputTransparent(!_dragging && !IsInteractivePoint(cursor));
    }

}
