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
/// Per-window black edge covers. Each side is one hardened transparent overlay HWND containing
/// both the black cover and its proximity grab handle. Transparent activation space returns
/// HTTRANSPARENT, so normal clicks continue through to the target application.
/// </summary>
public sealed class EdgeCoverService : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private readonly Dictionary<IntPtr, EdgeCoverSession> _sessions = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public EdgeCoverService()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    public event EventHandler? Changed;

    public bool IsEnabledForWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && _sessions.ContainsKey(hwnd);

    /// <summary>
    /// Toggles the covers for one external top-level window. Overlay failures are contained to
    /// this target; they must never terminate the MUX process.
    /// </summary>
    public bool ToggleWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_sessions.Remove(hwnd, out var existing))
        {
            SafeDispose(existing);
            RaiseChangedSafely();
            return false;
        }

        if (!IsEligibleTarget(hwnd))
        {
            return false;
        }

        EdgeCoverSession? session = null;
        try
        {
            session = new EdgeCoverSession(hwnd);
            if (!session.Refresh(force: true, cursor: TryCursor()))
            {
                SafeDispose(session);
                return false;
            }

            _sessions.Add(hwnd, session);
            RaiseChangedSafely();
            return true;
        }
        catch
        {
            SafeDispose(session);
            return false;
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0)
        {
            return;
        }

        var cursor = TryCursor();
        List<IntPtr>? stale = null;

        // Snapshot the collection: overlay teardown can indirectly pump WPF messages.
        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                if (pair.Value.Refresh(force: false, cursor))
                {
                    continue;
                }
            }
            catch
            {
                // One broken overlay session is removed without taking down the application.
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

        RaiseChangedSafely();
    }

    private static NativePoint? TryCursor()
    {
        return GetCursorPos(out var point) ? point : null;
    }

    private static bool IsEligibleTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    private void RaiseChangedSafely()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try { disposable.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;

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

    private sealed class EdgeCoverSession : IDisposable
    {
        private readonly IntPtr _targetHwnd;
        private readonly EdgeOverlayWindow _top;
        private readonly EdgeOverlayWindow _right;
        private readonly EdgeOverlayWindow _bottom;
        private readonly EdgeOverlayWindow _left;

        private NativeRect _lastTargetRect;
        private uint _lastDpi = 96;
        private int _minimumThicknessPx;
        private int _topThicknessPx;
        private int _rightThicknessPx;
        private int _bottomThicknessPx;
        private int _leftThicknessPx;
        private bool _initialized;
        private bool _disposed;

        public EdgeCoverSession(IntPtr targetHwnd)
        {
            _targetHwnd = targetHwnd;
            _lastDpi = EffectiveDpi(targetHwnd);
            _minimumThicknessPx = ScaleForDpi(4, _lastDpi);
            _topThicknessPx = _minimumThicknessPx;
            _rightThicknessPx = _minimumThicknessPx;
            _bottomThicknessPx = _minimumThicknessPx;
            _leftThicknessPx = _minimumThicknessPx;

            _top = CreateOverlay(EdgeSide.Top);
            _right = CreateOverlay(EdgeSide.Right);
            _bottom = CreateOverlay(EdgeSide.Bottom);
            _left = CreateOverlay(EdgeSide.Left);
        }

        private EdgeOverlayWindow CreateOverlay(EdgeSide side)
        {
            return new EdgeOverlayWindow(
                side,
                () => GetThickness(side),
                requested => SetThickness(side, requested));
        }

        public bool Refresh(bool force, NativePoint? cursor)
        {
            if (_disposed || !IsWindow(_targetHwnd))
            {
                return false;
            }

            if (!IsWindowVisible(_targetHwnd) || IsIconic(_targetHwnd) || IsWindowCloaked(_targetHwnd))
            {
                HideAll();
                return true;
            }

            if (!TryGetTargetRect(_targetHwnd, out var targetRect) || targetRect.Width <= 0 || targetRect.Height <= 0)
            {
                HideAll();
                return true;
            }

            _lastDpi = EffectiveDpi(_targetHwnd);
            _minimumThicknessPx = ScaleForDpi(4, _lastDpi);
            ClampThicknesses(targetRect);
            _lastTargetRect = targetRect;
            _initialized = true;

            // Update every pass, even if the target rectangle did not move, because the proximity
            // handle follows the pointer independently of target geometry.
            _top.Update(targetRect, _topThicknessPx, _lastDpi, cursor);
            _right.Update(targetRect, _rightThicknessPx, _lastDpi, cursor);
            _bottom.Update(targetRect, _bottomThicknessPx, _lastDpi, cursor);
            _left.Update(targetRect, _leftThicknessPx, _lastDpi, cursor);
            return true;
        }

        private int GetThickness(EdgeSide side)
        {
            return side switch
            {
                EdgeSide.Top => _topThicknessPx,
                EdgeSide.Right => _rightThicknessPx,
                EdgeSide.Bottom => _bottomThicknessPx,
                EdgeSide.Left => _leftThicknessPx,
                _ => _minimumThicknessPx
            };
        }

        private void SetThickness(EdgeSide side, int requestedThickness)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            var max = side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Max(_minimumThicknessPx, _lastTargetRect.Height)
                : Math.Max(_minimumThicknessPx, _lastTargetRect.Width);
            var next = Math.Clamp(requestedThickness, _minimumThicknessPx, max);

            switch (side)
            {
                case EdgeSide.Top: _topThicknessPx = next; break;
                case EdgeSide.Right: _rightThicknessPx = next; break;
                case EdgeSide.Bottom: _bottomThicknessPx = next; break;
                case EdgeSide.Left: _leftThicknessPx = next; break;
            }

            // Drag callbacks run on the UI dispatcher; refresh immediately for direct manipulation.
            Refresh(force: true, cursor: TryCursor());
        }

        private void ClampThicknesses(NativeRect rect)
        {
            var verticalMax = Math.Max(_minimumThicknessPx, rect.Height);
            var horizontalMax = Math.Max(_minimumThicknessPx, rect.Width);
            _topThicknessPx = Math.Clamp(_topThicknessPx, _minimumThicknessPx, verticalMax);
            _bottomThicknessPx = Math.Clamp(_bottomThicknessPx, _minimumThicknessPx, verticalMax);
            _leftThicknessPx = Math.Clamp(_leftThicknessPx, _minimumThicknessPx, horizontalMax);
            _rightThicknessPx = Math.Clamp(_rightThicknessPx, _minimumThicknessPx, horizontalMax);
        }

        private void HideAll()
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

    /// <summary>
    /// One HWND per side. The black cover and the animated proximity handle share this window;
    /// transparent pixels report HTTRANSPARENT so they never create a dead click strip.
    /// </summary>
    private sealed class EdgeOverlayWindow : Window, IDisposable
    {
        private const int WmMouseActivate = 0x0021;
        private const int WmNcHitTest = 0x0084;
        private const int MaNoActivate = 3;
        private const int HtClient = 1;
        private const int HtTransparent = -1;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new(-1);
        private static readonly TimeSpan HandleAnimation = TimeSpan.FromMilliseconds(105);

        private readonly EdgeSide _side;
        private readonly Func<int> _getCurrentThickness;
        private readonly Action<int> _setThickness;
        private readonly Canvas _canvas;
        private readonly Border _cover;
        private readonly Border _handle;
        private readonly ScaleTransform _handleScale;

        private HwndSource? _source;
        private NativeRect _targetRect;
        private NativeRect _overlayRect;
        private int _thicknessPx;
        private int _activationPx;
        private uint _dpi = 96;
        private bool _hot;
        private bool _dragging;
        private NativePoint _dragStartPoint;
        private int _dragStartThickness;
        private bool _shown;
        private bool _disposed;

        public EdgeOverlayWindow(EdgeSide side, Func<int> getCurrentThickness, Action<int> setThickness)
        {
            _side = side;
            _getCurrentThickness = getCurrentThickness;
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

            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            _cover = new Border { Background = Brushes.Black, SnapsToDevicePixels = true };
            _handleScale = new ScaleTransform(0.84, 0.84);
            _handle = BuildHandle(side, _handleScale);
            _canvas.Children.Add(_cover);
            _canvas.Children.Add(_handle);
            Content = _canvas;

            Cursor = side is EdgeSide.Top or EdgeSide.Bottom ? Cursors.SizeNS : Cursors.SizeWE;
            MouseLeftButtonDown += MouseLeftButtonDownHandler;
            MouseMove += MouseMoveHandler;
            MouseLeftButtonUp += MouseLeftButtonUpHandler;
            LostMouseCapture += LostMouseCaptureHandler;
        }

        private static Border BuildHandle(EdgeSide side, ScaleTransform scale)
        {
            var grip = new StackPanel
            {
                Orientation = side is EdgeSide.Top or EdgeSide.Bottom ? Orientation.Vertical : Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            for (var i = 0; i < 3; i++)
            {
                grip.Children.Add(new Rectangle
                {
                    Width = side is EdgeSide.Top or EdgeSide.Bottom ? 15 : 1.2,
                    Height = side is EdgeSide.Top or EdgeSide.Bottom ? 1.2 : 15,
                    RadiusX = 0.6,
                    RadiusY = 0.6,
                    Margin = side is EdgeSide.Top or EdgeSide.Bottom
                        ? new Thickness(0, i == 0 ? 0 : 2, 0, 0)
                        : new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(205, 205, 211))
                });
            }

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(248, 24, 24, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(92, 92, 101)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
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

        public void Update(NativeRect targetRect, int thicknessPx, uint dpi, NativePoint? cursor)
        {
            if (_disposed)
            {
                return;
            }

            _targetRect = targetRect;
            _thicknessPx = Math.Max(1, thicknessPx);
            _dpi = Math.Max(96u, dpi);
            _activationPx = ScaleForDpi(30, _dpi);
            _overlayRect = CalculateOverlayRect(targetRect, _thicknessPx, _activationPx, _side);

            EnsureShownAndPositioned();
            UpdateCoverVisual();
            UpdateHandleVisual(cursor);
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
                HwndTopmost,
                _overlayRect.Left,
                _overlayRect.Top,
                Math.Max(1, _overlayRect.Width),
                Math.Max(1, _overlayRect.Height),
                SwpNoActivate | SwpNoOwnerZOrder);
            Opacity = 1;
        }

        private void UpdateCoverVisual()
        {
            var scale = _dpi / 96.0;
            var overlayWidth = Math.Max(1.0, _overlayRect.Width / scale);
            var overlayHeight = Math.Max(1.0, _overlayRect.Height / scale);
            var thickness = Math.Max(1.0, _thicknessPx / scale);
            var activation = Math.Max(0.0, _activationPx / scale);

            _canvas.Width = overlayWidth;
            _canvas.Height = overlayHeight;

            if (_side is EdgeSide.Top or EdgeSide.Bottom)
            {
                _cover.Width = overlayWidth;
                _cover.Height = Math.Min(overlayHeight, thickness);
                Canvas.SetLeft(_cover, 0);
                Canvas.SetTop(_cover, _side == EdgeSide.Top ? 0 : Math.Min(activation, overlayHeight - _cover.Height));
            }
            else
            {
                _cover.Width = Math.Min(overlayWidth, thickness);
                _cover.Height = overlayHeight;
                Canvas.SetTop(_cover, 0);
                Canvas.SetLeft(_cover, _side == EdgeSide.Left ? 0 : Math.Min(activation, overlayWidth - _cover.Width));
            }
        }

        private void UpdateHandleVisual(NativePoint? cursor)
        {
            if (cursor is null || _dragging)
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

            var hot = along && distance <= _activationPx;
            SetHot(hot);
            if (!hot)
            {
                return;
            }

            var scale = _dpi / 96.0;
            var shortPx = ScaleForDpi(18, _dpi);
            var longPx = ScaleForDpi(46, _dpi);
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
                new DoubleAnimation(_handleScale.ScaleX, hot ? 1.0 : 0.84, HandleAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
            _handleScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(_handleScale.ScaleY, hot ? 1.0 : 0.84, HandleAnimation) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
        }

        private bool IsInteractivePoint(NativePoint point)
        {
            if (_disposed || !_shown)
            {
                return false;
            }

            var insideSpan = _side is EdgeSide.Top or EdgeSide.Bottom
                ? point.X >= _targetRect.Left && point.X < _targetRect.Right
                : point.Y >= _targetRect.Top && point.Y < _targetRect.Bottom;
            if (!insideSpan)
            {
                return false;
            }

            var onBlackCover = _side switch
            {
                EdgeSide.Top => point.Y >= _targetRect.Top && point.Y < _targetRect.Top + _thicknessPx,
                EdgeSide.Bottom => point.Y >= _targetRect.Bottom - _thicknessPx && point.Y < _targetRect.Bottom,
                EdgeSide.Left => point.X >= _targetRect.Left && point.X < _targetRect.Left + _thicknessPx,
                EdgeSide.Right => point.X >= _targetRect.Right - _thicknessPx && point.X < _targetRect.Right,
                _ => false
            };

            if (onBlackCover)
            {
                return true;
            }

            var boundary = InnerBoundary();
            var nearBoundary = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.Y - boundary) <= _activationPx
                : Math.Abs(point.X - boundary) <= _activationPx;
            return nearBoundary;
        }

        private int InnerBoundary()
        {
            return _side switch
            {
                EdgeSide.Top => _targetRect.Top + _thicknessPx,
                EdgeSide.Bottom => _targetRect.Bottom - _thicknessPx,
                EdgeSide.Left => _targetRect.Left + _thicknessPx,
                EdgeSide.Right => _targetRect.Right - _thicknessPx,
                _ => 0
            };
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
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, SwpNoActivate | SwpNoOwnerZOrder);
            }
            Opacity = 0;
        }

        private void MouseLeftButtonDownHandler(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || e.ChangedButton != MouseButton.Left || !GetCursorPos(out _dragStartPoint))
            {
                return;
            }

            _dragging = true;
            _dragStartThickness = _getCurrentThickness();
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

            _setThickness(_dragStartThickness + CalculateDragDelta(_side, _dragStartPoint, current));
            e.Handled = true;
        }

        private void MouseLeftButtonUpHandler(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            EndDrag();
            e.Handled = true;
        }

        private void LostMouseCaptureHandler(object sender, MouseEventArgs e)
        {
            _dragging = false;
        }

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

        private static NativeRect CalculateOverlayRect(NativeRect target, int thickness, int activation, EdgeSide side)
        {
            return side switch
            {
                EdgeSide.Top => new NativeRect(
                    target.Left,
                    target.Top,
                    target.Right,
                    Math.Min(target.Bottom, target.Top + thickness + activation)),
                EdgeSide.Bottom => new NativeRect(
                    target.Left,
                    Math.Max(target.Top, target.Bottom - thickness - activation),
                    target.Right,
                    target.Bottom),
                EdgeSide.Left => new NativeRect(
                    target.Left,
                    target.Top,
                    Math.Min(target.Right, target.Left + thickness + activation),
                    target.Bottom),
                EdgeSide.Right => new NativeRect(
                    Math.Max(target.Left, target.Right - thickness - activation),
                    target.Top,
                    target.Right,
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

    private static int CalculateDragDelta(EdgeSide side, NativePoint start, NativePoint current)
    {
        return side switch
        {
            EdgeSide.Top => current.Y - start.Y,
            EdgeSide.Bottom => start.Y - current.Y,
            EdgeSide.Left => current.X - start.X,
            EdgeSide.Right => start.X - current.X,
            _ => 0
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect : IEquatable<NativeRect>
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
        public readonly bool Equals(NativeRect other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public override readonly bool Equals(object? obj) => obj is NativeRect other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
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
            rect.Width > 0 &&
            rect.Height > 0)
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
    {
        return Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));
    }

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
