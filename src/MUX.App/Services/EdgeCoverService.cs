using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Adds per-window black edge covers that begin as a thin frame and can be dragged inward
/// independently from any side. Multiple target windows can be framed at the same time.
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

    private readonly Dictionary<IntPtr, EdgeCoverSession> _sessions = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public EdgeCoverService()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
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

        List<IntPtr>? stale = null;
        foreach (var pair in _sessions)
        {
            if (pair.Value.Refresh(force: false))
            {
                continue;
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

            var dpi = GetDpiForWindow(targetHwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

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
                HideAll();
                return true;
            }

            if (!TryGetTargetRect(_targetHwnd, out var targetRect) || targetRect.Width <= 0 || targetRect.Height <= 0)
            {
                HideAll();
                return true;
            }

            var dpi = GetDpiForWindow(_targetHwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }
            _minimumThicknessPx = ScaleForDpi(4, dpi);

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

        public void HideBar()
        {
            if (!_closing && IsVisible)
            {
                Hide();
            }
        }

        public void CloseBar()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            EndDrag();
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

            var delta = _side switch
            {
                EdgeSide.Top => currentPoint.Y - _dragStartPoint.Y,
                EdgeSide.Bottom => _dragStartPoint.Y - currentPoint.Y,
                EdgeSide.Left => currentPoint.X - _dragStartPoint.X,
                EdgeSide.Right => _dragStartPoint.X - currentPoint.X,
                _ => 0
            };

            _setThickness(_dragStartThickness + delta);
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
