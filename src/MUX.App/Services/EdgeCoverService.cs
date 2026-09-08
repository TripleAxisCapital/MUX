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
/// Adds per-window black edge covers that begin as a thin frame and can be dragged inward
/// independently from any side. Multiple target windows can be framed at the same time.
/// Approaching an adjustable edge reveals a larger animated grab handle so thin covers remain
/// easy to manipulate without permanently stealing clicks from the target application.
/// </summary>
public sealed class EdgeCoverService : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private static readonly IntPtr HwndTopmost = new(-1);

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

    public bool IsEnabledForWindow(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && _sessions.ContainsKey(hwnd);
    }

    /// <summary>
    /// Toggles edge covers for one native top-level window. Returns the new enabled state.
    /// </summary>
    public bool ToggleWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_sessions.Remove(hwnd, out var existing))
        {
            existing.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (!IsEligibleTarget(hwnd))
        {
            return false;
        }

        var session = new EdgeCoverSession(hwnd);
        if (!session.Refresh(force: true))
        {
            session.Dispose();
            return false;
        }

        _sessions.Add(hwnd, session);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0)
        {
            return;
        }

        var hasCursor = GetCursorPos(out var cursor);
        List<IntPtr>? stale = null;
        foreach (var pair in _sessions)
        {
            if (!pair.Value.Refresh(force: false))
            {
                stale ??= new List<IntPtr>();
                stale.Add(pair.Key);
                continue;
            }

            pair.Value.UpdatePointer(hasCursor ? cursor : null);
        }

        if (stale is null)
        {
            return;
        }

        foreach (var hwnd in stale)
        {
            if (_sessions.Remove(hwnd, out var session))
            {
                session.Dispose();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsEligibleTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        return true;
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

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
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
        private readonly EdgeBarWindow _topWindow;
        private readonly EdgeBarWindow _rightWindow;
        private readonly EdgeBarWindow _bottomWindow;
        private readonly EdgeBarWindow _leftWindow;

        private NativeRect _lastTargetRect;
        private uint _lastDpi = 96;
        private int _minimumThicknessPx;
        private int _topThicknessPx;
        private int _rightThicknessPx;
        private int _bottomThicknessPx;
        private int _leftThicknessPx;
        private bool _initialized;
        private bool _targetVisible;
        private bool _disposed;

        public EdgeCoverSession(IntPtr targetHwnd)
        {
            _targetHwnd = targetHwnd;

            var dpi = GetDpiForWindow(targetHwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

            _lastDpi = dpi;
            _minimumThicknessPx = ScaleForDpi(4, dpi);
            _topThicknessPx = _minimumThicknessPx;
            _rightThicknessPx = _minimumThicknessPx;
            _bottomThicknessPx = _minimumThicknessPx;
            _leftThicknessPx = _minimumThicknessPx;

            _topWindow = CreateBar(EdgeSide.Top);
            _rightWindow = CreateBar(EdgeSide.Right);
            _bottomWindow = CreateBar(EdgeSide.Bottom);
            _leftWindow = CreateBar(EdgeSide.Left);
        }

        private EdgeBarWindow CreateBar(EdgeSide side)
        {
            return new EdgeBarWindow(
                side,
                () => GetThickness(side),
                requested => SetThickness(side, requested));
        }

        public bool Refresh(bool force)
        {
            if (_disposed || !IsWindow(_targetHwnd))
            {
                return false;
            }

            if (!IsWindowVisible(_targetHwnd) || IsIconic(_targetHwnd) || IsWindowCloaked(_targetHwnd))
            {
                _targetVisible = false;
                HideAll();
                return true;
            }

            if (!TryGetTargetRect(_targetHwnd, out var targetRect) || targetRect.Width <= 0 || targetRect.Height <= 0)
            {
                _targetVisible = false;
                HideAll();
                return true;
            }

            var dpi = GetDpiForWindow(_targetHwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }
            _lastDpi = dpi;
            _minimumThicknessPx = ScaleForDpi(4, dpi);
            _targetVisible = true;

            ClampThicknesses(targetRect);

            if (!force && _initialized && targetRect.Equals(_lastTargetRect) && AllBarsVisible())
            {
                return true;
            }

            _lastTargetRect = targetRect;
            _initialized = true;

            PositionBar(
                _topWindow,
                targetRect.Left,
                targetRect.Top,
                targetRect.Width,
                _topThicknessPx);

            PositionBar(
                _bottomWindow,
                targetRect.Left,
                targetRect.Bottom - _bottomThicknessPx,
                targetRect.Width,
                _bottomThicknessPx);

            PositionBar(
                _leftWindow,
                targetRect.Left,
                targetRect.Top,
                _leftThicknessPx,
                targetRect.Height);

            PositionBar(
                _rightWindow,
                targetRect.Right - _rightThicknessPx,
                targetRect.Top,
                _rightThicknessPx,
                targetRect.Height);

            return true;
        }

        public void UpdatePointer(NativePoint? cursor)
        {
            if (_disposed || !_initialized || !_targetVisible || cursor is null)
            {
                HideGrabHandles();
                return;
            }

            var point = cursor.Value;
            _topWindow.UpdateGrabHandle(point, _lastTargetRect, _topThicknessPx, _lastDpi);
            _rightWindow.UpdateGrabHandle(point, _lastTargetRect, _rightThicknessPx, _lastDpi);
            _bottomWindow.UpdateGrabHandle(point, _lastTargetRect, _bottomThicknessPx, _lastDpi);
            _leftWindow.UpdateGrabHandle(point, _lastTargetRect, _leftThicknessPx, _lastDpi);
        }

        private void PositionBar(EdgeBarWindow window, int x, int y, int width, int height)
        {
            window.SetPixelBounds(x, y, Math.Max(1, width), Math.Max(1, height));
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
                case EdgeSide.Top:
                    _topThicknessPx = next;
                    break;
                case EdgeSide.Right:
                    _rightThicknessPx = next;
                    break;
                case EdgeSide.Bottom:
                    _bottomThicknessPx = next;
                    break;
                case EdgeSide.Left:
                    _leftThicknessPx = next;
                    break;
            }

            Refresh(force: true);
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

        private bool AllBarsVisible()
        {
            return _topWindow.IsVisible &&
                   _rightWindow.IsVisible &&
                   _bottomWindow.IsVisible &&
                   _leftWindow.IsVisible;
        }

        private void HideAll()
        {
            _topWindow.HideBar();
            _rightWindow.HideBar();
            _bottomWindow.HideBar();
            _leftWindow.HideBar();
            HideGrabHandles();
        }

        private void HideGrabHandles()
        {
            _topWindow.HideGrabHandle();
            _rightWindow.HideGrabHandle();
            _bottomWindow.HideGrabHandle();
            _leftWindow.HideGrabHandle();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _topWindow.CloseBar();
            _rightWindow.CloseBar();
            _bottomWindow.CloseBar();
            _leftWindow.CloseBar();
        }
    }

    private sealed class EdgeBarWindow : Window
    {
        private readonly EdgeSide _side;
        private readonly Func<int> _getCurrentThickness;
        private readonly Action<int> _setThickness;
        private readonly EdgeGrabHandleWindow _grabHandle;
        private HwndSource? _source;
        private bool _dragging;
        private NativePoint _dragStartPoint;
        private int _dragStartThickness;
        private bool _closing;

        public EdgeBarWindow(
            EdgeSide side,
            Func<int> getCurrentThickness,
            Action<int> setThickness)
        {
            _side = side;
            _getCurrentThickness = getCurrentThickness;
            _setThickness = setThickness;
            _grabHandle = new EdgeGrabHandleWindow(side, getCurrentThickness, setThickness);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Colors.Black);
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Focusable = false;
            Cursor = side is EdgeSide.Top or EdgeSide.Bottom
                ? Cursors.SizeNS
                : Cursors.SizeWE;

            MouseLeftButtonDown += EdgeBar_MouseLeftButtonDown;
            MouseMove += EdgeBar_MouseMove;
            MouseLeftButtonUp += EdgeBar_MouseLeftButtonUp;
            LostMouseCapture += EdgeBar_LostMouseCapture;
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
                _source.RemoveHook(WndProc);
                _source = null;
            }

            base.OnClosed(e);
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            return IntPtr.Zero;
        }

        public void SetPixelBounds(int x, int y, int width, int height)
        {
            if (_closing)
            {
                return;
            }

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                x,
                y,
                Math.Max(1, width),
                Math.Max(1, height),
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

            if (Opacity != 1)
            {
                Opacity = 1;
            }
        }

        public void UpdateGrabHandle(NativePoint cursor, NativeRect targetRect, int thickness, uint dpi)
        {
            if (_closing || _grabHandle.IsDragging)
            {
                return;
            }

            var activationDistance = ScaleForDpi(28, dpi);
            var handleShort = ScaleForDpi(18, dpi);
            var handleLong = ScaleForDpi(46, dpi);
            var edgePadding = ScaleForDpi(8, dpi);

            var horizontal = _side is EdgeSide.Top or EdgeSide.Bottom;
            var boundary = _side switch
            {
                EdgeSide.Top => targetRect.Top + thickness,
                EdgeSide.Bottom => targetRect.Bottom - thickness,
                EdgeSide.Left => targetRect.Left + thickness,
                EdgeSide.Right => targetRect.Right - thickness,
                _ => 0
            };

            var perpendicularDistance = horizontal
                ? Math.Abs(cursor.Y - boundary)
                : Math.Abs(cursor.X - boundary);

            var alongInside = horizontal
                ? cursor.X >= targetRect.Left - activationDistance && cursor.X <= targetRect.Right + activationDistance
                : cursor.Y >= targetRect.Top - activationDistance && cursor.Y <= targetRect.Bottom + activationDistance;

            if (perpendicularDistance > activationDistance || !alongInside)
            {
                _grabHandle.HideAnimated();
                return;
            }

            if (horizontal)
            {
                var width = handleLong;
                var height = handleShort;
                var centerX = ClampHandleCenter(cursor.X, targetRect.Left, targetRect.Right, width, edgePadding);
                _grabHandle.ShowAt(centerX - width / 2, boundary - height / 2, width, height);
            }
            else
            {
                var width = handleShort;
                var height = handleLong;
                var centerY = ClampHandleCenter(cursor.Y, targetRect.Top, targetRect.Bottom, height, edgePadding);
                _grabHandle.ShowAt(boundary - width / 2, centerY - height / 2, width, height);
            }
        }

        private static int ClampHandleCenter(int desired, int start, int end, int handleLength, int padding)
        {
            var half = handleLength / 2;
            var minimum = start + half + padding;
            var maximum = end - half - padding;
            if (minimum > maximum)
            {
                return start + Math.Max(0, end - start) / 2;
            }

            return Math.Clamp(desired, minimum, maximum);
        }

        public void HideBar()
        {
            HideGrabHandle();
            if (!_closing && IsVisible)
            {
                Hide();
            }
        }

        public void HideGrabHandle()
        {
            _grabHandle.HideAnimated();
        }

        public void CloseBar()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            EndDrag();
            _grabHandle.CloseHandle();
            Close();
        }

        private void EdgeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !GetCursorPos(out _dragStartPoint))
            {
                return;
            }

            _dragging = true;
            _dragStartThickness = _getCurrentThickness();
            CaptureMouse();
            e.Handled = true;
        }

        private void EdgeBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var currentPoint))
            {
                return;
            }

            _setThickness(_dragStartThickness + CalculateDragDelta(_side, _dragStartPoint, currentPoint));
            e.Handled = true;
        }

        private void EdgeBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            EndDrag();
            e.Handled = true;
        }

        private void EdgeBar_LostMouseCapture(object sender, MouseEventArgs e)
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
    }

    /// <summary>
    /// A temporary, much larger target that appears only when the pointer approaches a cover edge.
    /// It is deliberately a separate window so the normal browser/app surface remains clickable
    /// everywhere except the visible grab handle itself.
    /// </summary>
    private sealed class EdgeGrabHandleWindow : Window
    {
        private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(110);
        private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(80);

        private readonly EdgeSide _side;
        private readonly Func<int> _getCurrentThickness;
        private readonly Action<int> _setThickness;
        private readonly ScaleTransform _scaleTransform;
        private HwndSource? _source;
        private bool _presented;
        private bool _dragging;
        private bool _closing;
        private long _animationVersion;
        private NativePoint _dragStartPoint;
        private int _dragStartThickness;

        public EdgeGrabHandleWindow(
            EdgeSide side,
            Func<int> getCurrentThickness,
            Action<int> setThickness)
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
            Width = side is EdgeSide.Top or EdgeSide.Bottom ? 46 : 18;
            Height = side is EdgeSide.Top or EdgeSide.Bottom ? 18 : 46;
            Opacity = 0;
            Cursor = side is EdgeSide.Top or EdgeSide.Bottom ? Cursors.SizeNS : Cursors.SizeWE;

            _scaleTransform = new ScaleTransform(0.82, 0.82);
            RenderTransform = _scaleTransform;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Content = BuildHandleVisual(side);

            MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            MouseMove += Handle_MouseMove;
            MouseLeftButtonUp += Handle_MouseLeftButtonUp;
            LostMouseCapture += Handle_LostMouseCapture;
        }

        public bool IsDragging => _dragging;

        private static UIElement BuildHandleVisual(EdgeSide side)
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
                Background = new SolidColorBrush(Color.FromArgb(246, 24, 24, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(92, 92, 101)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Child = grip,
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
                _source.RemoveHook(WndProc);
                _source = null;
            }
            base.OnClosed(e);
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            return IntPtr.Zero;
        }

        public void ShowAt(int x, int y, int width, int height)
        {
            if (_closing)
            {
                return;
            }

            if (!IsVisible)
            {
                Show();
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    hwnd,
                    HwndTopmost,
                    x,
                    y,
                    Math.Max(1, width),
                    Math.Max(1, height),
                    SwpNoActivate | SwpNoOwnerZOrder);
            }

            if (_presented)
            {
                return;
            }

            _presented = true;
            _animationVersion++;
            BeginAnimation(OpacityProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Opacity = Math.Min(Opacity, 0.01);
            _scaleTransform.ScaleX = 0.82;
            _scaleTransform.ScaleY = 0.82;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(Opacity, 1, RevealDuration) { EasingFunction = easing });
            _scaleTransform.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.82, 1, RevealDuration) { EasingFunction = easing });
            _scaleTransform.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.82, 1, RevealDuration) { EasingFunction = easing });
        }

        public void HideAnimated()
        {
            if (_closing || !_presented || _dragging)
            {
                return;
            }

            _presented = false;
            var version = ++_animationVersion;
            var animation = new DoubleAnimation(Opacity, 0, HideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (_, _) =>
            {
                if (_closing || _presented || _dragging || version != _animationVersion)
                {
                    return;
                }

                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Hide();
            };
            BeginAnimation(OpacityProperty, animation);
        }

        public void CloseHandle()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            EndDrag();
            Close();
        }

        private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !GetCursorPos(out _dragStartPoint))
            {
                return;
            }

            _dragging = true;
            _presented = true;
            _dragStartThickness = _getCurrentThickness();
            CaptureMouse();
            e.Handled = true;
        }

        private void Handle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var currentPoint))
            {
                return;
            }

            _setThickness(_dragStartThickness + CalculateDragDelta(_side, _dragStartPoint, currentPoint));
            e.Handled = true;
        }

        private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            EndDrag();
            e.Handled = true;
        }

        private void Handle_LostMouseCapture(object sender, MouseEventArgs e)
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

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public readonly bool Equals(NativeRect other)
        {
            return Left == other.Left &&
                   Top == other.Top &&
                   Right == other.Right &&
                   Bottom == other.Bottom;
        }

        public override readonly bool Equals(object? obj)
        {
            return obj is NativeRect other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(Left, Top, Right, Bottom);
        }
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

    private static int ScaleForDpi(int value, uint dpi)
    {
        var effectiveDpi = Math.Max(96u, dpi);
        return Math.Max(1, (int)Math.Round(value * effectiveDpi / 96.0));
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out NativeRect value,
        int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out int value,
        int size);
}
