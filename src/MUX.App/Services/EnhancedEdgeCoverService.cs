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
public readonly record struct EdgeCoverGeometry(
    int Width, int Height, int MinimumThickness,
    int TopThickness, int RightThickness, int BottomThickness, int LeftThickness);

public sealed class EnhancedEdgeCoverService : IDisposable
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private static readonly Lazy<EnhancedEdgeCoverService> SharedInstance = new(() => new EnhancedEdgeCoverService());

    private readonly Dictionary<IntPtr, CoverSession> _sessions = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _inputTimer;
    private bool _globallyVisible = true;
    private bool _disposed;

    public EnhancedEdgeCoverService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _inputTimer.Tick += InputTimer_Tick;
        _inputTimer.Start();
    }

    public static EnhancedEdgeCoverService Shared => SharedInstance.Value;

    public event EventHandler? Changed;

    public bool AreCoversGloballyVisible => _globallyVisible;
    public int ConfiguredWindowCount => _sessions.Count;

    public bool IsEnabledForWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _sessions.ContainsKey(hwnd);

    // Typed template API: never access cover session fields with reflection.
    public bool TryGetCoverGeometry(IntPtr hwnd, out EdgeCoverGeometry geometry)
    {
        geometry = default;
        return !_disposed && _sessions.TryGetValue(hwnd, out var session) &&
               session.TryGetGeometry(out geometry);
    }

    public bool TrySetCoverThicknesses(IntPtr hwnd, int top, int right, int bottom, int left)
    {
        if (_disposed || !_sessions.TryGetValue(hwnd, out var session) ||
            !session.SetThicknesses(top, right, bottom, left))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Globally hides or shows every configured edge-cover session without destroying the session
    /// or losing any of the four user-selected cover depths. This is the operation used by the
    /// Stream Deck/global hotkey toggle.
    /// </summary>
    public bool ToggleAllVisibility()
    {
        if (_disposed)
        {
            return false;
        }

        SetAllVisibility(!_globallyVisible);
        return _globallyVisible;
    }

    public void SetAllVisibility(bool visible)
    {
        if (_disposed || _globallyVisible == visible)
        {
            return;
        }

        _globallyVisible = visible;
        List<IntPtr>? stale = null;

        foreach (var pair in _sessions.ToArray())
        {
            try
            {
                if (pair.Value.SetGlobalVisibility(visible))
                {
                    continue;
                }
            }
            catch
            {
                // A failed target must not affect the remaining configured windows.
            }

            stale ??= new List<IntPtr>();
            stale.Add(pair.Key);
        }

        if (stale is not null)
        {
            foreach (var hwnd in stale)
            {
                if (_sessions.Remove(hwnd, out var session))
                {
                    SafeDispose(session);
                }
            }
        }

        RaiseChanged();
    }

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
            if (!session.Refresh(TryCursor(), _globallyVisible))
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

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.Count == 0 || TryCursor() is not NativePoint point)
        {
            return;
        }

        // Handle transparency in the owning cover service, without reflection or
        // repeated native frame changes anywhere outside a narrow resize handle.
        foreach (var session in _sessions.Values.ToArray())
        {
            session.UpdateInput(point);
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
                if (pair.Value.Refresh(cursor, _globallyVisible))
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
        _inputTimer.Stop();
        _inputTimer.Tick -= InputTimer_Tick;

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
        private bool _globallyVisible = true;
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

        public bool SetGlobalVisibility(bool visible)
        {
            _globallyVisible = visible;
            return Refresh(TryCursor(), visible);
        }

        public bool Refresh(NativePoint? cursor, bool globallyVisible)
        {
            _globallyVisible = globallyVisible;

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

            if (!_globallyVisible)
            {
                ParkAll();
                return true;
            }

            _top.Update(rect, _topThickness, _dpi, cursor);
            _right.Update(rect, _rightThickness, _dpi, cursor);
            _bottom.Update(rect, _bottomThickness, _dpi, cursor);
            _left.Update(rect, _leftThickness, _dpi, cursor);
            return true;
        }

        public bool TryGetGeometry(out EdgeCoverGeometry geometry)
        {
            geometry = default;
            if (_disposed || !_initialized || _targetRect.Width <= 0 || _targetRect.Height <= 0)
            {
                return false;
            }

            geometry = new EdgeCoverGeometry(_targetRect.Width, _targetRect.Height, _minimumThickness,
                _topThickness, _rightThickness, _bottomThickness, _leftThickness);
            return true;
        }

        public bool SetThicknesses(int top, int right, int bottom, int left)
        {
            if (_disposed || !_initialized)
            {
                return false;
            }

            _topThickness = top;
            _rightThickness = right;
            _bottomThickness = bottom;
            _leftThickness = left;
            ClampThicknesses();
            return Refresh(TryCursor(), _globallyVisible);
        }

        public void UpdateInput(NativePoint cursor)
        {
            _top.SetInputForCursor(cursor, _globallyVisible);
            _right.SetInputForCursor(cursor, _globallyVisible);
            _bottom.SetInputForCursor(cursor, _globallyVisible);
            _left.SetInputForCursor(cursor, _globallyVisible);
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

            try { Refresh(TryCursor(), _globallyVisible); } catch { }
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
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpFrameChanged = 0x0020;
        private const int MaNoActivate = 3;
        private const int HtClient = 1;
        private const int HtTransparent = -1;
        private const int VkLeftButton = 0x01;
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
        private bool _inputTransparent;
        private bool _dragging;
        private NativePoint _dragStart;
        private int _dragStartThickness;
        private bool _shown;
        private bool _positioned;
        private bool _topBarRevealed = true;
        private DateTime _lastTopHoverUtc = DateTime.MinValue;
        private NativeRect _lastPositioned;
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
            // Newly shown covers must be click-through even before the first
            // input-timer tick; only the explicit handle can ever be armed.
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

        public void SetInputForCursor(NativePoint cursor, bool globallyVisible)
        {
            if (_disposed || !_shown)
            {
                return;
            }

            // Evaluate caption proximity on the responsive input cadence rather
            // than only on the rendering timer. Reaching the top cover must
            // reveal the underlying title bar even under heavy window movement.
            if (_side == EdgeSide.Top && globallyVisible)
            {
                UpdateTopBarReveal(cursor);
            }

            // When dragging a normal window through the top edge, never arm the
            // bar grip under the pressed pointer. Only an already captured
            // cover-resize gesture retains its own mouse input.
            var windowDragInProgress = _side == EdgeSide.Top && !_dragging &&
                (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
            SetInputTransparent(!globallyVisible || windowDragInProgress ||
                (!_dragging && !IsInteractivePoint(cursor)));
        }

        private void SetInputTransparent(bool transparent)
        {
            if (_inputTransparent == transparent)
            {
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var next = transparent ? style | WsExTransparent : style & ~WsExTransparent;
            if (style != next)
            {
                _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next));
                _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);
            }

            _inputTransparent = transparent;
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
            // The top-edge grip needs a larger hit area than the side grips,
            // particularly at the minimum 4-DIP black-bar thickness.
            _grabRadius = ScaleForDpi(_side == EdgeSide.Top ? 19 : 12, _dpi);
            _hoverRadius = ScaleForDpi(_side == EdgeSide.Top ? 30 : 22, _dpi);
            _overlayRect = CalculateOverlayRect(targetRect, _thickness, _hoverRadius, _side);

            EnsureShownAndPositioned();
            DrawCover();
            if (_side == EdgeSide.Top && cursor is NativePoint current)
            {
                UpdateTopBarReveal(current);
            }
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

            if (!_positioned || !_lastPositioned.Equals(_overlayRect))
            {
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    _overlayRect.Left,
                    _overlayRect.Top,
                    Math.Max(1, _overlayRect.Width),
                    Math.Max(1, _overlayRect.Height),
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                _lastPositioned = _overlayRect;
                _positioned = true;
            }
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

        private void UpdateTopBarReveal(NativePoint? cursor)
        {
            var now = DateTime.UtcNow;
            if (_dragging)
            {
                // Never animate the cover away while its thickness is being adjusted.
                _lastTopHoverUtc = DateTime.MinValue;
                SetTopBarRevealed(true);
                return;
            }

            if (cursor is NativePoint point)
            {
                // A separate grip lives at the inner edge, away from the middle
                // of the title bar. Hovering it takes precedence over unveiling
                // the caption so the top bar can still be resized.
                // Only the actual visible grip is reserved for resizing; the
                // old 19-pixel proximity radius masked a large part of the
                // title bar and prevented the auto-hide trigger.
                var nearGrip = Math.Abs(point.X - HandleCenter()) <= ScaleForDpi(32, _dpi) &&
                    Math.Abs(point.Y - InnerBoundary()) <= ScaleForDpi(9, _dpi);
                if (nearGrip)
                {
                    _lastTopHoverUtc = DateTime.MinValue;
                    SetTopBarRevealed(true);
                    return;
                }

                var approach = ScaleForDpi(36, _dpi);
                var titleBand = Math.Max(ScaleForDpi(86, _dpi), _thickness + ScaleForDpi(52, _dpi));
                if (point.X >= _targetRect.Left && point.X < _targetRect.Right &&
                    point.Y >= _targetRect.Top - approach &&
                    point.Y <= Math.Min(_targetRect.Bottom, _targetRect.Top + titleBand))
                {
                    _lastTopHoverUtc = now;
                    SetTopBarRevealed(false);
                    return;
                }
            }

            // A short exit delay prevents flicker when crossing the cover edge
            // or moving from the caption into a native/custom caption button.
            if (now - _lastTopHoverUtc >= TimeSpan.FromMilliseconds(360))
            {
                SetTopBarRevealed(true);
            }
        }

        private void SetTopBarRevealed(bool revealed)
        {
            if (_topBarRevealed == revealed)
            {
                return;
            }

            _topBarRevealed = revealed;
            _cover.BeginAnimation(OpacityProperty,
                new DoubleAnimation(revealed ? 1.0 : 0.0,
                    TimeSpan.FromMilliseconds(revealed ? 175 : 135))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                HandoffBehavior.SnapshotAndReplace);
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

            var handleCenter = HandleCenter();
            var nearHandle = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.X - handleCenter) <= ScaleForDpi(50, _dpi)
                : Math.Abs(point.Y - handleCenter) <= ScaleForDpi(50, _dpi);
            var hot = _dragging || (along && nearHandle && distance <= _hoverRadius);
            if (_side == EdgeSide.Top && !hot && !_topBarRevealed)
            {
                SetHot(false);
                return;
            }
            SetHot(hot);
            if (!hot)
            {
                return;
            }

            var scale = _dpi / 96.0;
            var longPx = ScaleForDpi(64, _dpi);
            var shortPx = ScaleForDpi(24, _dpi);
            if (_side is EdgeSide.Top or EdgeSide.Bottom)
            {
                _handle.Width = longPx / scale;
                _handle.Height = shortPx / scale;
                var center = HandleCenter();
                Canvas.SetLeft(_handle, (center - longPx / 2 - _overlayRect.Left) / scale);
                Canvas.SetTop(_handle, (boundary - shortPx / 2 - _overlayRect.Top) / scale);
            }
            else
            {
                _handle.Width = shortPx / scale;
                _handle.Height = longPx / scale;
                var center = HandleCenter();
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

        private int HandleCenter()
        {
            var horizontal = _side is EdgeSide.Top or EdgeSide.Bottom;
            var start = horizontal ? _targetRect.Left : _targetRect.Top;
            var end = horizontal ? _targetRect.Right : _targetRect.Bottom;
            // Reserve the centre of the native title bar for ordinary window dragging.
            var desired = _side == EdgeSide.Top
                ? start + Math.Max(0, end - start) / 4
                : start + Math.Max(0, end - start) / 2;
            return ClampCenter(desired, start, end, ScaleForDpi(64, _dpi), ScaleForDpi(8, _dpi));
        }

        private bool IsInteractivePoint(NativePoint point)
        {
            if (_disposed || !_shown)
            {
                return false;
            }

            // Hit-test only the dedicated resize handle, never the black surface or
            // the large transparent proximity overlay covering the target caption.
            var nearBoundary = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.Y - InnerBoundary()) <= _grabRadius
                : Math.Abs(point.X - InnerBoundary()) <= _grabRadius;
            var nearHandle = _side is EdgeSide.Top or EdgeSide.Bottom
                ? Math.Abs(point.X - HandleCenter()) <= ScaleForDpi(36, _dpi)
                : Math.Abs(point.Y - HandleCenter()) <= ScaleForDpi(36, _dpi);
            if (_side == EdgeSide.Top && !_dragging && !_topBarRevealed)
            {
                return false;
            }

            return _dragging || (nearBoundary && nearHandle);
        }

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
            SetInputTransparent(false);
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

            EndDrag();
            SetInputTransparent(true);
            _cover.BeginAnimation(OpacityProperty, null);
            _cover.Opacity = 1;
            _topBarRevealed = true;
            _lastTopHoverUtc = DateTime.MinValue;
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
            _positioned = false;
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
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

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
