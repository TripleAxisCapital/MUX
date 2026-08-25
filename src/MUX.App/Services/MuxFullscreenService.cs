using System.Runtime.InteropServices;
using System.Windows.Threading;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Provides a MUX-owned borderless fullscreen mode. This intentionally does not
/// invoke an application's native fullscreen command (such as browser F11), so
/// Windows never gets a chance to expand the app to the physical monitor.
/// </summary>
public sealed class MuxFullscreenService : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long WsCaption = 0x00C00000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;

    private const long WsExDlgModalFrame = 0x00000001L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;
    private const long WsExToolWindow = 0x00000080L;

    private const int SwRestore = 9;
    private const int SwMaximize = 3;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int RectTolerancePx = 2;

    private readonly Func<DisplayProfile?> _displayProvider;
    private readonly Func<LayoutProfile?> _layoutProvider;
    private readonly Func<bool> _enabledProvider;
    private readonly Dictionary<IntPtr, MuxFullscreenState> _states = new();
    private readonly DispatcherTimer _enforcementTimer;

    public MuxFullscreenService(
        Func<DisplayProfile?> displayProvider,
        Func<LayoutProfile?> layoutProvider,
        Func<bool> enabledProvider)
    {
        _displayProvider = displayProvider;
        _layoutProvider = layoutProvider;
        _enabledProvider = enabledProvider;

        _enforcementTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _enforcementTimer.Tick += EnforcementTimer_Tick;
        _enforcementTimer.Start();
    }

    public void ToggleForeground()
    {
        var hwnd = GetForegroundWindow();
        if (!IsEligibleWindow(hwnd))
        {
            return;
        }

        if (_states.TryGetValue(hwnd, out var existing))
        {
            RestoreWindow(hwnd, existing);
            return;
        }

        if (!_enabledProvider())
        {
            return;
        }

        var display = _displayProvider();
        var layout = _layoutProvider();
        if (display is null || layout is null || layout.Zones.Count == 0 || !TryGetWindowRect(hwnd, out var current))
        {
            return;
        }

        var placement = GetPlacement(hwnd);
        var zone = ResolveZone(display, layout, current, placement);
        if (zone is null)
        {
            return;
        }

        var restoreRect = current;
        var wasZoomed = IsZoomed(hwnd);
        if (wasZoomed && placement is { } savedPlacement)
        {
            var normal = ToPixelRect(savedPlacement.NormalPosition);
            if (normal.Width > 0 && normal.Height > 0)
            {
                restoreRect = normal;
            }
        }

        var state = new MuxFullscreenState(
            zone.Id,
            restoreRect,
            GetWindowLongPtr(hwnd, GwlStyle).ToInt64(),
            GetWindowLongPtr(hwnd, GwlExStyle).ToInt64(),
            wasZoomed);

        _states[hwnd] = state;
        ApplyMuxFullscreen(hwnd, state, display, zone);
    }

    private void EnforcementTimer_Tick(object? sender, EventArgs e)
    {
        if (!_enabledProvider())
        {
            RestoreAll();
            return;
        }

        var display = _displayProvider();
        var layout = _layoutProvider();
        if (display is null || layout is null)
        {
            RestoreAll();
            return;
        }

        foreach (var pair in _states.ToArray())
        {
            var hwnd = pair.Key;
            var state = pair.Value;
            if (!IsWindow(hwnd))
            {
                _states.Remove(hwnd);
                continue;
            }

            var zone = layout.Zones.FirstOrDefault(candidate => candidate.Id == state.ZoneId);
            if (zone is null)
            {
                RestoreWindow(hwnd, state);
                continue;
            }

            var target = DisplayGeometry.ZoneToPixels(display, zone);
            var currentStyle = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            var currentExStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var desiredStyle = MakeBorderlessStyle(currentStyle);
            var desiredExStyle = MakeBorderlessExStyle(currentExStyle);
            var frameChanged = false;

            if (currentStyle != desiredStyle)
            {
                SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(desiredStyle));
                frameChanged = true;
            }

            if (currentExStyle != desiredExStyle)
            {
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(desiredExStyle));
                frameChanged = true;
            }

            if (IsZoomed(hwnd))
            {
                ShowWindow(hwnd, SwRestore);
                frameChanged = true;
            }

            if (!TryGetWindowRect(hwnd, out var current) || !RectsApproximatelyEqual(current, target) || frameChanged)
            {
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    target.Left,
                    target.Top,
                    target.Width,
                    target.Height,
                    SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpShowWindow | (frameChanged ? SwpFrameChanged : 0));
            }
        }
    }

    private static VirtualMonitorZone? ResolveZone(
        DisplayProfile display,
        LayoutProfile layout,
        PixelRect current,
        WindowPlacement? placement)
    {
        var zone = DisplayGeometry.FindZoneForMaximize(display, layout, current);
        if (zone is not null)
        {
            return zone;
        }

        if (placement is { } savedPlacement)
        {
            var normal = ToPixelRect(savedPlacement.NormalPosition);
            zone = DisplayGeometry.FindZoneForMaximize(display, layout, normal);
            if (zone is not null)
            {
                return zone;
            }
        }

        return DisplayGeometry.FindBestZone(display, layout, current);
    }

    private static void ApplyMuxFullscreen(IntPtr hwnd, MuxFullscreenState state, DisplayProfile display, VirtualMonitorZone zone)
    {
        if (state.WasZoomed || IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        var borderlessStyle = MakeBorderlessStyle(state.Style);
        var borderlessExStyle = MakeBorderlessExStyle(state.ExStyle);
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(borderlessStyle));
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(borderlessExStyle));

        var target = DisplayGeometry.ZoneToPixels(display, zone);
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private void RestoreWindow(IntPtr hwnd, MuxFullscreenState state)
    {
        _states.Remove(hwnd);
        if (!IsWindow(hwnd))
        {
            return;
        }

        if (IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(state.Style));
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(state.ExStyle));
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            state.RestoreRect.Left,
            state.RestoreRect.Top,
            state.RestoreRect.Width,
            state.RestoreRect.Height,
            SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);

        if (state.WasZoomed)
        {
            ShowWindow(hwnd, SwMaximize);
        }
    }

    private void RestoreAll()
    {
        foreach (var pair in _states.ToArray())
        {
            RestoreWindow(pair.Key, pair.Value);
        }
    }

    private static long MakeBorderlessStyle(long style) =>
        style & ~(WsCaption | WsBorder | WsDlgFrame | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);

    private static long MakeBorderlessExStyle(long style) =>
        style & ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge);

    private static WindowPlacement? GetPlacement(IntPtr hwnd)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        return GetWindowPlacement(hwnd, ref placement) ? placement : null;
    }

    private static PixelRect ToPixelRect(Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static bool RectsApproximatelyEqual(PixelRect a, PixelRect b) =>
        Math.Abs(a.Left - b.Left) <= RectTolerancePx &&
        Math.Abs(a.Top - b.Top) <= RectTolerancePx &&
        Math.Abs(a.Width - b.Width) <= RectTolerancePx &&
        Math.Abs(a.Height - b.Height) <= RectTolerancePx;

    private static bool TryGetWindowRect(IntPtr hwnd, out PixelRect rect)
    {
        if (GetWindowRect(hwnd, out var native))
        {
            rect = ToPixelRect(native);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        return (exStyle & WsExToolWindow) == 0;
    }

    public void Dispose()
    {
        _enforcementTimer.Stop();
        RestoreAll();
    }

    private sealed record MuxFullscreenState(
        Guid ZoneId,
        PixelRect RestoreRect,
        long Style,
        long ExStyle,
        bool WasZoomed);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newValue);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
