using System.Runtime.InteropServices;
using System.Windows.Threading;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Keeps application-owned fullscreen modes (Edge/Chrome F11, media fullscreen,
/// borderless presentation modes) inside the MUX virtual monitor that owned the
/// window before fullscreen began.
/// </summary>
public sealed class FullscreenWindowService : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsExToolWindow = 0x00000080L;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int RectTolerancePx = 5;
    private const int PhysicalFullscreenTolerancePx = 16;

    private readonly Func<DisplayProfile?> _displayProvider;
    private readonly Func<LayoutProfile?> _layoutProvider;
    private readonly Func<bool> _enabledProvider;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<IntPtr, PixelRect> _lastNormalRects = new();
    private readonly Dictionary<IntPtr, Guid> _windowZones = new();
    private readonly Dictionary<int, Guid> _processZones = new();
    private readonly Dictionary<IntPtr, bool> _lastBorderlessState = new();
    private readonly HashSet<IntPtr> _constrainedFullscreen = new();

    public FullscreenWindowService(
        Func<DisplayProfile?> displayProvider,
        Func<LayoutProfile?> layoutProvider,
        Func<bool> enabledProvider)
    {
        _displayProvider = displayProvider;
        _layoutProvider = layoutProvider;
        _enabledProvider = enabledProvider;

        _timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_enabledProvider())
        {
            _constrainedFullscreen.Clear();
            return;
        }

        var display = _displayProvider();
        var layout = _layoutProvider();
        if (display is null || layout is null || layout.Zones.Count == 0)
        {
            _constrainedFullscreen.Clear();
            return;
        }

        var hwnd = GetForegroundWindow();
        if (!IsEligibleWindow(hwnd) || !TryGetWindowRect(hwnd, out var current))
        {
            return;
        }

        var borderless = IsBorderless(hwnd);
        var wasBorderless = _lastBorderlessState.TryGetValue(hwnd, out var previousBorderless) && previousBorderless;
        _lastBorderlessState[hwnd] = borderless;

        if (_constrainedFullscreen.Contains(hwnd))
        {
            if (!borderless)
            {
                _constrainedFullscreen.Remove(hwnd);
                if (!IsZoomed(hwnd))
                {
                    TrackNormalWindow(hwnd, current, display, layout);
                }
                return;
            }

            var constrainedZone = ResolveZone(hwnd, display, layout);
            if (constrainedZone is null)
            {
                _constrainedFullscreen.Remove(hwnd);
                return;
            }

            ForceFullscreenIntoZone(hwnd, DisplayGeometry.ZoneToPixels(display, constrainedZone));
            return;
        }

        if (borderless)
        {
            var zone = ResolveZone(hwnd, display, layout);
            if (zone is not null)
            {
                var target = DisplayGeometry.ZoneToPixels(display, zone);
                var styleJustChangedToBorderless = !wasBorderless && _lastNormalRects.ContainsKey(hwnd);
                var wantsPhysicalFullscreen = CoversPhysicalDisplay(current, display);
                var borderlessAndZoomed = IsZoomed(hwnd);

                if (styleJustChangedToBorderless || wantsPhysicalFullscreen || borderlessAndZoomed)
                {
                    _constrainedFullscreen.Add(hwnd);
                    _windowZones[hwnd] = zone.Id;
                    ForceFullscreenIntoZone(hwnd, target);
                    return;
                }
            }
        }

        // Ordinary Windows maximize is handled by WindowManagerService. Do not let
        // this fullscreen service fight that behavior unless the app is borderless.
        if (IsZoomed(hwnd))
        {
            return;
        }

        TrackNormalWindow(hwnd, current, display, layout);
    }

    private VirtualMonitorZone? ResolveZone(IntPtr hwnd, DisplayProfile display, LayoutProfile layout)
    {
        if (_windowZones.TryGetValue(hwnd, out var zoneId))
        {
            var assigned = layout.Zones.FirstOrDefault(zone => zone.Id == zoneId);
            if (assigned is not null)
            {
                return assigned;
            }
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId != 0 && _processZones.TryGetValue(processId, out var processZoneId))
        {
            var processZone = layout.Zones.FirstOrDefault(zone => zone.Id == processZoneId);
            if (processZone is not null)
            {
                _windowZones[hwnd] = processZone.Id;
                return processZone;
            }
        }

        if (_lastNormalRects.TryGetValue(hwnd, out var previous))
        {
            var previousZone = DisplayGeometry.FindZoneForMaximize(display, layout, previous);
            if (previousZone is not null)
            {
                RememberZone(hwnd, processId, previousZone.Id);
                return previousZone;
            }
        }

        if (TryGetPlacementNormalRect(hwnd, out var placementRect))
        {
            var placementZone = DisplayGeometry.FindZoneForMaximize(display, layout, placementRect);
            if (placementZone is not null)
            {
                _lastNormalRects[hwnd] = placementRect;
                RememberZone(hwnd, processId, placementZone.Id);
                return placementZone;
            }
        }

        return null;
    }

    private void TrackNormalWindow(IntPtr hwnd, PixelRect rect, DisplayProfile display, LayoutProfile layout)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || CoversPhysicalDisplay(rect, display))
        {
            return;
        }

        _lastNormalRects[hwnd] = rect;
        var zone = DisplayGeometry.FindZoneForMaximize(display, layout, rect);
        if (zone is null)
        {
            _windowZones.Remove(hwnd);
            return;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        RememberZone(hwnd, processId, zone.Id);
    }

    private void RememberZone(IntPtr hwnd, int processId, Guid zoneId)
    {
        _windowZones[hwnd] = zoneId;
        if (processId != 0)
        {
            _processZones[processId] = zoneId;
        }
    }

    private static void ForceFullscreenIntoZone(IntPtr hwnd, PixelRect target)
    {
        // Chromium can keep WS_MAXIMIZE set while F11 is active. Restore only the
        // Windows show state first; the app's own borderless fullscreen UI remains.
        if (IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow | SwpNoOwnerZOrder);

        // Some Chromium builds reassert their fullscreen rectangle immediately after
        // the frame change. MoveWindow is a second Win32 path; the 45 ms enforcement
        // loop then keeps the app pinned if it tries again.
        if (TryGetWindowRect(hwnd, out var after) && !RectsApproximatelyEqual(after, target))
        {
            MoveWindow(hwnd, target.Left, target.Top, target.Width, target.Height, false);
        }
    }

    private static PixelRect DisplayBounds(DisplayProfile display)
        => new(display.LeftPx, display.TopPx, display.WidthPx, display.HeightPx);

    private static bool CoversPhysicalDisplay(PixelRect rect, DisplayProfile display)
    {
        var bounds = DisplayBounds(display);
        return rect.Left <= bounds.Left + PhysicalFullscreenTolerancePx &&
               rect.Top <= bounds.Top + PhysicalFullscreenTolerancePx &&
               rect.Right >= bounds.Right - PhysicalFullscreenTolerancePx &&
               rect.Bottom >= bounds.Bottom - PhysicalFullscreenTolerancePx &&
               rect.Width >= bounds.Width - PhysicalFullscreenTolerancePx * 2 &&
               rect.Height >= bounds.Height - PhysicalFullscreenTolerancePx * 2;
    }

    private static bool RectsApproximatelyEqual(PixelRect a, PixelRect b)
    {
        return Math.Abs(a.Left - b.Left) <= RectTolerancePx &&
               Math.Abs(a.Top - b.Top) <= RectTolerancePx &&
               Math.Abs(a.Width - b.Width) <= RectTolerancePx &&
               Math.Abs(a.Height - b.Height) <= RectTolerancePx;
    }

    private static bool TryGetWindowRect(IntPtr hwnd, out PixelRect rect)
    {
        if (GetWindowRect(hwnd, out var native))
        {
            rect = new PixelRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static bool TryGetPlacementNormalRect(IntPtr hwnd, out PixelRect rect)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (GetWindowPlacement(hwnd, ref placement))
        {
            var native = placement.NormalPosition;
            rect = new PixelRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static bool IsBorderless(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        return (style & WsCaption) == 0;
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
        _timer.Stop();
        _lastNormalRects.Clear();
        _windowZones.Clear();
        _processZones.Clear();
        _lastBorderlessState.Clear();
        _constrainedFullscreen.Clear();
    }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
}
