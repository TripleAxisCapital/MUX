using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Per-window black edge covers with a generous two-sided proximity grab zone. Users can approach
/// the adjustable edge from either inside the target window or from the surrounding desktop,
/// click-drag anywhere in that proximity band, and resize the black cover directly.
/// </summary>
public sealed class EnhancedEdgeCoverService : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private readonly Dictionary<IntPtr, CoverSession> _sessions = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public EnhancedEdgeCoverService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public event EventHandler? Changed;

    public bool IsEnabledForWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _sessions.ContainsKey(hwnd);

    public bool ToggleWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_sessions.Remove(hwnd, out var existing))
        {
            SafeDispose(existing);
            RaiseChanged();
            return false;
        }

        if (!IsEligibleTarget(hwnd))
        {
            return false;
        }

        CoverSession? session = null;
        try
        {
            session = new CoverSession(hwnd);
            if (!session.Refresh(TryCursor()))
            {
                SafeDispose(session);
                return false;
            }

            _sessions[hwnd] = session;
            RaiseChanged();
            return true;
        }
        catch
        {
            SafeDispose(session);
            return false;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0)
        {
            return;
        }

        var cursor = TryCursor();
        List<IntPtr>? stale = null;

        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                if (pair.Value.Refresh(cursor))
                {
                    continue;
                }
            }
            catch
            {
                // Isolate failures to one target window.
            }

            stale ??= new List<IntPtr>();
            stale.Add(pair.Key);
        }

        if (stale is null)
        {
            return;
        }

        foreach (var hwnd in stale)
        {
            if (_sessions.Remove(hwnd, out var session))
            {
                SafeDispose(session);
            }
        }

        RaiseChanged();
    }

    private static NativePoint? TryCursor()
        => GetCursorPos(out var point) ? point : null;

    private static bool IsEligibleTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;

        foreach (var session in _sessions.Values.ToArray())
        {
            SafeDispose(session);
        }
        _sessions.Clear();
    }

    private enum EdgeSide
    {
        Top,
        Right,
        Bottom,
        Left
    }

    private sealed class CoverSession : IDisposable
    {
        private readonly IntPtr _target;
        private readonly EdgeOverlayWindow _top;
        private readonly EdgeOverlayWindow _right;
        private readonly EdgeOverlayWindow _bottom;
        private readonly EdgeOverlayWindow _left;

        private NativeRect _targetRect;
        private int _minimumThickness;
        private int _topThickness;
        private int _rightThickness;
        private int _bottomThickness;
        private int _leftThickness;
        private uint _dpi;
        private bool _initialized;
        private bool _disposed;

        public CoverSession(IntPtr target)
        {
            _target = target;
            _dpi = EffectiveDpi(target);
            _minimumThickness = ScaleForDpi(4, _dpi);
            _topThickness = _minimumThickness;
            _rightThickness = _minimumThickness;
            _bottomThickness = _minimumThickness;
            _leftThickness = _minimumThickness;

            _top = CreateOverlay(EdgeSide.Top);
            _right = CreateOverlay(EdgeSide.Right);
            _bottom = CreateOverlay(EdgeSide.Bottom);
            _left = CreateOverlay(EdgeSide.Left);
        }

        private EdgeOverlayWindow CreateOverlay(EdgeSide side)
            => new(
                side,
                () => GetThickness(side),
                value => SetThickness(side, value));

        public bool Refresh(NativePoint? cursor)
        {
            if (_disposed || !IsWindow(_target))
            {
                return false;
            }

            if (!IsWindowVisible(_target) || IsIconic(_target) || IsWindowCloaked(_target))
            {
                ParkAll();
                return true;
            }

            if (!TryGetTargetRect(_target, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                ParkAll();
                return true;
            }

            _dpi = EffectiveDpi(_target);
            _minimumThickness = ScaleForDpi(4, _dpi);
            _targetRect = rect;
            _initialized = true;
            ClampThicknesses();

            _top.Update(rect, _topThickness, _dpi, cursor);
            _right.Update(rect, _rightThickness, _dpi, cursor);
            _bottom.Update(rect, _bottomThickness, _dpi, cursor);
            _left.Update(rect, _leftThickness, _dpi, cursor);
            return true;
        }

        private int GetThickness(EdgeSide side)
            => side switch
            {
                EdgeSide.Top => _topThickness,
                EdgeSide.Right => _rightThickness,
                EdgeSide.Bottom => _bottomThickness,
                EdgeSide.Left => _leftThickness,
                _ => _minimumThickness
            };

        private void SetThickness(EdgeSide side, int value)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            var maximum = side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Max(_minimumThickness, _targetRect.Height)
                : Math.Max(_minimumThickness, _targetRect.Width);
            var next = Math.Clamp(value, _minimumThickness, maximum);

            switch (side)
            {
                case EdgeSide.Top: _topThickness = next; break;
                case EdgeSide.Right: _rightThickness = next; break;
                case EdgeSide.Bottom: _bottomThickness = next; break;
                case EdgeSide.Left: _leftThickness = next; break;
            }

            try { Refresh(TryCursor()); } catch { }
        }

        private void ClampThicknesses()
        {
            var verticalMax = Math.Max(_minimumThickness, _targetRect.Height);
            var horizontalMax = Math.Max(_minimumThickness, _targetRect.Width);
            _topThickness = Math.Clamp(_topThickness, _minimumThickness, verticalMax);
            _bottomThickness = Math.Clamp(_bottomThickness, _minimumThickness, verticalMax);
            _leftThickness = Math.Clamp(_leftThickness, _minimumThickness, horizontalMax);
            _rightThickness = Math.Clamp(_rightThickness, _minimumThickness, horizontalMax);
        }

        private void ParkAll()
        {
            _top.Park();
            _right.Park();
            _bottom.Park();
            _left.Park();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SafeDispose(_top);
            SafeDispose(_right);
            SafeDispose(_bottom);
            SafeDispose(_left);
        }
    }

    private sealed class EdgeOverlayWindow : Window, IDisposable
    {
        private const int WmMouseActivate = 0x0021;
        private const int WmNcHitTest = 0x0084;
        private const int MaNoActivate = 3;
        private const int HtClient = 1;
        private const int HtTransparent = -1;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly TimeSpan HandleAnimation = TimeSpan.FromMilliseconds(95);

        private readonly EdgeSide _side;
        private readonly Func<int> _getThickness;
        private readonly Action<int> _setThickness;
        private readonly Canvas _canvas;
        private readonly Border _cover;
        private readonly Border _handle;
        private readonly ScaleTransform _handleScale;

        private HwndSource? _source;
        private NativeRect _targetRect;
        private NativeRect _overlayRect;
        private int _thickness;
        private int _grabRadius;
        private int _hoverRadius;
        private uint _dpi = 96;
        private bool _hot;
        private bool _dragging;
        private NativePoint _dragStart;
        private int _dragStartThickness;
        private bool _shown;
        private bool _disposed;

        public EdgeOverlayWindow(EdgeSide side, Func<int> getThickness, Action<int> setThickness)
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
            Cursor = side is EdgeSide.Top or EdgeSide.Bottom ? Cursors.SizeNS : Cursors.SizeWE;

            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            _cover = new Border { Background = Brushes.Black, SnapsToDevicePixels = true };
            _handleScale = new ScaleTransform(0.82, 0.82);
            _handle = BuildHandle(side, _handleScale);
            _canvas.Children.Add(_cover);
            _canvas.Children.Add(_handle);
            Content = _canvas;

            MouseLeftButtonDown += MouseDown;
            MouseMove += MouseMoveHandler;
            MouseLeftButtonUp += MouseUp;
            LostMouseCapture += LostCapture;
        }

        private static Border BuildHandle(EdgeSide side, ScaleTransform scale)
        {
            var grip = new StackPanel
            {
                Orientation = side is EdgeSide.Top or EdgeSide.Bottom
                    ? Orientation.Vertical
                    : Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            for (var i = 0; i < 3; i++)
            {
                grip.Children.Add(new Rectangle
                {
                    Width = side is EdgeSide.Top or EdgeSide.Bottom ? 19 : 1.5,
                    Height = side is EdgeSide.Top or EdgeSide.Bottom ? 1.5 : 19,
                    RadiusX = 0.75,
                    RadiusY = 0.75,
                    Margin = side is EdgeSide.Top or EdgeSide.Bottom
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

        public void Update(NativeRect targetRect, int thickness, uint dpi, NativePoint? cursor)
        {
            if (_disposed)
            {
                return;
            }

            _targetRect = targetRect;
            _thickness = Math.Max(1, thickness);
            _dpi = Math.Max(96u, dpi);
            _grabRadius = ScaleForDpi(64, _dpi);
            _hoverRadius = ScaleForDpi(76, _dpi);
            _overlayRect = CalculateOverlayRect(targetRect, _thickness, _hoverRadius, _side);

            EnsureShownAndPositioned();
            DrawCover();
            DrawHandle(cursor);
        }

        private void EnsureShownAndPositioned()
        {
            if (!_shown)
            {
                Opacity = 0;
                Show();
                _shown = true;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                _overlayRect.Left,
                _overlayRect.Top,
                Math.Max(1, _overlayRect.Width),
                Math.Max(1, _overlayRect.Height),
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            Opacity = 1;
        }

        private void DrawCover()
        {
            var scale = _dpi / 96.0;
            var overlayWidth = Math.Max(1.0, _overlayRect.Width / scale);
            var overlayHeight = Math.Max(1.0, _overlayRect.Height / scale);
            _canvas.Width = overlayWidth;
            _canvas.Height = overlayHeight;

            var left = (_targetRect.Left - _overlayRect.Left) / scale;
            var top = (_targetRect.Top - _overlayRect.Top) / scale;
            var rightCoverLeft = (_targetRect.Right - _thickness - _overlayRect.Left) / scale;
            var bottomCoverTop = (_targetRect.Bottom - _thickness - _overlayRect.Top) / scale;
            var thicknessDip = Math.Max(1.0, _thickness / scale);
            var targetWidthDip = Math.Max(1.0, _targetRect.Width / scale);
            var targetHeightDip = Math.Max(1.0, _targetRect.Height / scale);

            if (_side is EdgeSide.Top or EdgeSide.Bottom)
            {
                _cover.Width = targetWidthDip;
                _cover.Height = Math.Min(targetHeightDip, thicknessDip);
                Canvas.SetLeft(_cover, left);
                Canvas.SetTop(_cover, _side == EdgeSide.Top ? top : bottomCoverTop);
            }
            else
            {
                _cover.Width = Math.Min(targetWidthDip, thicknessDip);
                _cover.Height = targetHeightDip;
                Canvas.SetTop(_cover, top);
                Canvas.SetLeft(_cover, _side == EdgeSide.Left ? left : rightCoverLeft);
            }
        }

        private void DrawHandle(NativePoint? cursor)
        {
            if (cursor is null)
            {
                SetHot(_dragging);
                return;
            }

            var point = cursor.Value;
            var boundary = InnerBoundary();
            var along = _side is EdgeSide.Top or EdgeSide.Bottom
                ? point.X >= _targetRect.Left && point.X <= _targetRect.Right
                : point.Y >= _targetRect.Top && point.Y <= _targetRect.Bottom;
            var distance = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.Y - boundary)
                : Math.Abs(point.X - boundary);

            var hot = _dragging || (along && distance <= _hoverRadius);
            SetHot(hot);
            if (!hot)
            {
                return;
            }

            var scale = _dpi / 96.0;
            var longPx = ScaleForDpi(64, _dpi);
            var shortPx = ScaleForDpi(24, _dpi);
            var paddingPx = ScaleForDpi(8, _dpi);

            if (_side is EdgeSide.Top or EdgeSide.Bottom)
            {
                _handle.Width = longPx / scale;
                _handle.Height = shortPx / scale;
                var center = ClampCenter(point.X, _targetRect.Left, _targetRect.Right, longPx, paddingPx);
                Canvas.SetLeft(_handle, (center - longPx / 2 - _overlayRect.Left) / scale);
                Canvas.SetTop(_handle, (boundary - shortPx / 2 - _overlayRect.Top) / scale);
            }
            else
            {
                _handle.Width = shortPx / scale;
                _handle.Height = longPx / scale;
                var center = ClampCenter(point.Y, _targetRect.Top, _targetRect.Bottom, longPx, paddingPx);
                Canvas.SetLeft(_handle, (boundary - shortPx / 2 - _overlayRect.Left) / scale);
                Canvas.SetTop(_handle, (center - longPx / 2 - _overlayRect.Top) / scale);
            }
        }

        private void SetHot(bool hot)
        {
            if (_hot == hot)
            {
                return;
            }

            _hot = hot;
            var easing = new CubicEase { EasingMode = hot ? EasingMode.EaseOut : EasingMode.EaseIn };
            _handle.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(_handle.Opacity, hot ? 1.0 : 0.0, HandleAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
            _handleScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(_handleScale.ScaleX, hot ? 1.0 : 0.82, HandleAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
            _handleScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(_handleScale.ScaleY, hot ? 1.0 : 0.82, HandleAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
        }

        private bool IsInteractivePoint(NativePoint point)
        {
            if (_disposed || !_shown)
            {
                return false;
            }

            var along = _side is EdgeSide.Top or EdgeSide.Bottom
                ? point.X >= _targetRect.Left && point.X < _targetRect.Right
                : point.Y >= _targetRect.Top && point.Y < _targetRect.Bottom;
            if (!along)
            {
                return false;
            }

            var boundary = InnerBoundary();
            var nearBoundary = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.Y - boundary) <= _grabRadius
                : Math.Abs(point.X - boundary) <= _grabRadius;

            return nearBoundary || IsOnBlackCover(point);
        }

        private bool IsOnBlackCover(NativePoint point)
            => _side switch
            {
                EdgeSide.Top =>
                    point.Y >= _targetRect.Top && point.Y < _targetRect.Top + _thickness,
                EdgeSide.Bottom =>
                    point.Y >= _targetRect.Bottom - _thickness && point.Y < _targetRect.Bottom,
                EdgeSide.Left =>
                    point.X >= _targetRect.Left && point.X < _targetRect.Left + _thickness,
                EdgeSide.Right =>
                    point.X >= _targetRect.Right - _thickness && point.X < _targetRect.Right,
                _ => false
            };

        private int InnerBoundary()
            => _side switch
            {
                EdgeSide.Top => _targetRect.Top + _thickness,
                EdgeSide.Bottom => _targetRect.Bottom - _thickness,
                EdgeSide.Left => _targetRect.Left + _thickness,
                EdgeSide.Right => _targetRect.Right - _thickness,
                _ => 0
            };

        private void MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || e.ChangedButton != MouseButton.Left || !GetCursorPos(out _dragStart))
            {
                return;
            }

            _dragging = true;
            _dragStartThickness = _getThickness();
            CaptureMouse();
            SetHot(true);
            e.Handled = true;
        }

        private void MouseMoveHandler(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var current))
            {
                return;
            }

            _setThickness(_dragStartThickness + DragDelta(_side, _dragStart, current));
            e.Handled = true;
        }

        private void MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            EndDrag();
            e.Handled = true;
        }

        private void LostCapture(object sender, MouseEventArgs e)
            => _dragging = false;

        private void EndDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (Mouse.Captured == this)
            {
                ReleaseMouseCapture();
            }
        }

        public void Park()
        {
            if (_disposed || !_shown)
            {
                return;
            }

            _hot = false;
            _handle.BeginAnimation(OpacityProperty, null);
            _handle.Opacity = 0;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    -32000,
                    -32000,
                    1,
                    1,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
            }
            Opacity = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { EndDrag(); } catch { }
            try
            {
                if (IsVisible)
                {
                    Close();
                }
            }
            catch { }
        }

        private static NativeRect CalculateOverlayRect(
            NativeRect target,
            int thickness,
            int proximity,
            EdgeSide side)
        {
            return side switch
            {
                EdgeSide.Top => new NativeRect(
                    target.Left,
                    target.Top - proximity,
                    target.Right,
                    Math.Min(target.Bottom + proximity, target.Top + thickness + proximity)),
                EdgeSide.Bottom => new NativeRect(
                    target.Left,
                    Math.Max(target.Top - proximity, target.Bottom - thickness - proximity),
                    target.Right,
                    target.Bottom + proximity),
                EdgeSide.Left => new NativeRect(
                    target.Left - proximity,
                    target.Top,
                    Math.Min(target.Right + proximity, target.Left + thickness + proximity),
                    target.Bottom),
                EdgeSide.Right => new NativeRect(
                    Math.Max(target.Left - proximity, target.Right - thickness - proximity),
                    target.Top,
                    target.Right + proximity,
                    target.Bottom),
                _ => target
            };
        }

        private static int ClampCenter(int desired, int start, int end, int length, int padding)
        {
            var half = length / 2;
            var min = start + half + padding;
            var max = end - half - padding;
            return min <= max ? Math.Clamp(desired, min, max) : start + Math.Max(0, end - start) / 2;
        }

        private static NativePoint PointFromLParam(IntPtr lParam)
        {
            var value = unchecked((long)lParam.ToInt64());
            return new NativePoint
            {
                X = unchecked((short)(value & 0xFFFF)),
                Y = unchecked((short)((value >> 16) & 0xFFFF))
            };
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }

    private static int DragDelta(EdgeSide side, NativePoint start, NativePoint current)
        => side switch
        {
            EdgeSide.Top => current.Y - start.Y,
            EdgeSide.Bottom => start.Y - current.Y,
            EdgeSide.Left => current.X - start.X,
            EdgeSide.Right => start.X - current.X,
            _ => 0
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static bool TryGetTargetRect(IntPtr hwnd, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(
                hwnd,
                DwmwaExtendedFrameBounds,
                out rect,
                Marshal.SizeOf<NativeRect>()) == 0 &&
            rect.Width > 0 && rect.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        var cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    private static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
