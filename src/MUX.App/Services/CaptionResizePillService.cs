using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MUX.App.Windows;

namespace MUX.App.Services;

public sealed class CaptionResizePillService : IDisposable
{
    private const int GaRoot = 2;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const int SmCxSize = 30;
    private const int SmCySize = 31;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int SwRestore = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(430);

    private readonly DispatcherTimer _timer;
    private readonly CaptionResizePillWindow _pill;
    private IntPtr _targetHwnd;
    private NativeRect _targetVisualBounds;
    private uint _targetDpi = 96;
    private int _targetClusterWidth;
    private int _targetCaptionHeight;
    private DateTime _lastHotUtc = DateTime.MinValue;
    private bool _started;
    private bool _disposed;

    public CaptionResizePillService()
    {
        _pill = new CaptionResizePillWindow();
        _pill.ResizeRequested += Pill_ResizeRequested;
        _pill.LayoutModeChanged += (_, _) => RepositionCurrentPill();

        _timer = new DispatcherTimer(DispatcherPriority.Input, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(65)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _timer.Start();
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
        _pill.ResizeRequested -= Pill_ResizeRequested;

        try
        {
            _pill.Close();
        }
        catch
        {
            // Shutdown should never be blocked by the helper UI.
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !GetCursorPos(out var cursor))
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (_pill.IsVisible && (_pill.IsMouseOver || _pill.IsInteractionLocked))
        {
            _lastHotUtc = now;
            RefreshCurrentTarget();
            return;
        }

        if (TryResolveCaptionTarget(
                cursor,
                out var hwnd,
                out var visualBounds,
                out var dpi,
                out var clusterWidth,
                out var captionHeight))
        {
            _targetHwnd = hwnd;
            _targetVisualBounds = visualBounds;
            _targetDpi = dpi;
            _targetClusterWidth = clusterWidth;
            _targetCaptionHeight = captionHeight;
            _lastHotUtc = now;

            if (GetWindowRect(hwnd, out var rawBounds))
            {
                _pill.SetTarget(hwnd, rawBounds.Width, rawBounds.Height);
            }
            else
            {
                _pill.SetTarget(hwnd, visualBounds.Width, visualBounds.Height);
            }

            _pill.Reveal();
            PositionPill(visualBounds, dpi, clusterWidth, captionHeight);
            return;
        }

        if (_pill.IsVisible && now - _lastHotUtc >= HideDelay)
        {
            _pill.Collapse(animate: false);
            _pill.Dismiss();
            _targetHwnd = IntPtr.Zero;
        }
    }

    private bool TryResolveCaptionTarget(
        NativePoint cursor,
        out IntPtr hwnd,
        out NativeRect bounds,
        out uint dpi,
        out int clusterWidth,
        out int captionHeight)
    {
        hwnd = IntPtr.Zero;
        bounds = default;
        dpi = 96;
        clusterWidth = 0;
        captionHeight = 0;

        var hit = WindowFromPoint(cursor);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        hwnd = GetAncestor(hit, GaRoot);
        if (hwnd == IntPtr.Zero || hwnd == _pill.NativeHandle || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        var style = GetWindowStyle(hwnd);
        if ((style & WsCaption) != WsCaption || (style & WsSysMenu) == 0)
        {
            return false;
        }

        if (!TryGetVisualBounds(hwnd, out bounds) || bounds.Width < 180 || bounds.Height < 100)
        {
            return false;
        }

        dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var buttonWidth = Math.Max(ScaleForDpi(44, dpi), GetSystemMetricsForDpi(SmCxSize, dpi));
        captionHeight = Math.Max(ScaleForDpi(30, dpi), GetSystemMetricsForDpi(SmCySize, dpi));
        var buttonCount = 1;
        if ((style & WsMaximizeBox) != 0) buttonCount++;
        if ((style & WsMinimizeBox) != 0) buttonCount++;
        clusterWidth = Math.Max(buttonWidth, buttonWidth * buttonCount);

        var proximity = ScaleForDpi(18, dpi);
        var hotLeft = bounds.Right - clusterWidth - proximity;
        var hotRight = bounds.Right + proximity;
        var hotTop = bounds.Top - proximity;
        var hotBottom = bounds.Top + captionHeight + proximity;

        return cursor.X >= hotLeft && cursor.X <= hotRight && cursor.Y >= hotTop && cursor.Y <= hotBottom;
    }

    private void RefreshCurrentTarget()
    {
        if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd))
        {
            return;
        }

        if (TryGetVisualBounds(_targetHwnd, out var visualBounds))
        {
            _targetVisualBounds = visualBounds;
        }

        _targetDpi = GetDpiForWindow(_targetHwnd);
        if (_targetDpi == 0)
        {
            _targetDpi = 96;
        }

        var style = GetWindowStyle(_targetHwnd);
        var buttonWidth = Math.Max(ScaleForDpi(44, _targetDpi), GetSystemMetricsForDpi(SmCxSize, _targetDpi));
        _targetCaptionHeight = Math.Max(ScaleForDpi(30, _targetDpi), GetSystemMetricsForDpi(SmCySize, _targetDpi));
        var buttonCount = 1;
        if ((style & WsMaximizeBox) != 0) buttonCount++;
        if ((style & WsMinimizeBox) != 0) buttonCount++;
        _targetClusterWidth = Math.Max(buttonWidth, buttonWidth * buttonCount);

        if (GetWindowRect(_targetHwnd, out var rawBounds))
        {
            _pill.UpdateTargetDimensions(rawBounds.Width, rawBounds.Height, updateEditors: !_pill.IsInteractionLocked);
        }

        RepositionCurrentPill();
    }

    private void RepositionCurrentPill()
    {
        if (!_pill.IsVisible || _targetHwnd == IntPtr.Zero)
        {
            return;
        }

        PositionPill(_targetVisualBounds, _targetDpi, _targetClusterWidth, _targetCaptionHeight);
    }

    private void PositionPill(NativeRect targetBounds, uint dpi, int clusterWidth, int captionHeight)
    {
        var pillHwnd = _pill.NativeHandle;
        if (pillHwnd == IntPtr.Zero || !GetWindowRect(pillHwnd, out var pillBounds))
        {
            return;
        }

        var monitor = MonitorFromWindow(_targetHwnd, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var gap = ScaleForDpi(8, dpi);
        var edgePadding = ScaleForDpi(6, dpi);
        var clusterCenterX = targetBounds.Right - Math.Max(clusterWidth, 1) / 2;
        var left = clusterCenterX - pillBounds.Width / 2;
        var top = targetBounds.Top - pillBounds.Height - gap;

        if (top < monitorInfo.Work.Top + edgePadding)
        {
            top = targetBounds.Top + captionHeight + gap;
        }

        var maxLeft = monitorInfo.Work.Right - pillBounds.Width - edgePadding;
        var maxTop = monitorInfo.Work.Bottom - pillBounds.Height - edgePadding;
        left = Math.Clamp(left, monitorInfo.Work.Left + edgePadding, Math.Max(monitorInfo.Work.Left + edgePadding, maxLeft));
        top = Math.Clamp(top, monitorInfo.Work.Top + edgePadding, Math.Max(monitorInfo.Work.Top + edgePadding, maxTop));

        SetWindowPos(
            pillHwnd,
            HwndTopmost,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void Pill_ResizeRequested(object? sender, CaptionResizeRequestEventArgs e)
    {
        if (e.TargetHwnd == IntPtr.Zero || e.TargetHwnd != _targetHwnd || !IsWindow(e.TargetHwnd))
        {
            return;
        }

        e.Succeeded = ResizeTargetWindow(e.TargetHwnd, e.Width, e.Height);
        if (!e.Succeeded)
        {
            return;
        }

        if (GetWindowRect(e.TargetHwnd, out var rawBounds))
        {
            _pill.UpdateTargetDimensions(rawBounds.Width, rawBounds.Height);
        }

        TryGetVisualBounds(e.TargetHwnd, out _targetVisualBounds);
        SetForegroundWindow(e.TargetHwnd);
        RepositionCurrentPill();
    }

    private static bool ResizeTargetWindow(IntPtr hwnd, int width, int height)
    {
        if (IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        if (!GetWindowRect(hwnd, out var current))
        {
            return false;
        }

        return SetWindowPos(
            hwnd,
            IntPtr.Zero,
            current.Left,
            current.Top,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }

    private static bool TryGetVisualBounds(IntPtr hwnd, out NativeRect bounds)
    {
        if (DwmGetWindowAttribute(
                hwnd,
                DwmwaExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<NativeRect>()) == 0 &&
            bounds.Width > 0 &&
            bounds.Height > 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    private static long GetWindowStyle(IntPtr hwnd)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlStyle).ToInt64()
            : GetWindowLong32(hwnd, GwlStyle);
    }

    private static int ScaleForDpi(int value, uint dpi)
    {
        return (int)Math.Round(value * Math.Max(dpi, 96) / 96.0);
    }

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

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

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
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
