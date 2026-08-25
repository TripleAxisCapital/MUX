using System.Runtime.InteropServices;
using System.Windows.Threading;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

/// <summary>
/// Keeps true application fullscreen modes (for example Edge/Chrome F11) inside
/// the MUX virtual monitor the window belonged to before entering fullscreen.
/// Unlike pseudo-maximize, this does not restore the native window style, so the
/// application remains in its own fullscreen UI while its outer bounds are clamped.
/// </summary>
public sealed class FullscreenWindowService : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int RectTolerancePx = 4;
    private const int PhysicalFullscreenTolerancePx = 12;

    private readonly Func<DisplayProfile?> _displayProvider;
    private readonly Func<LayoutProfile?> _layoutProvider;
    private readonly Func<bool> _enabledProvider;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<IntPtr, PixelRect> _lastNormalRects = new();
    private readonly Dictionary<IntPtr, Guid> _windowZones = new();
    private readonly Dictionary<int, Guid> _processZones = new();
    private readonly Dictionary<IntPtr, FullscreenState> _constrained = new();

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
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_enabledProvider())
        {
            _constrained.Clear();
            return;
        }

        var display = _displayProvider();
        var layout = _layoutProvider();
        if (display is null || layout is null || layout.Zones.Count == 0)
        {
            _constrained.Clear();
            return;
        }

        var hwnd = GetForegroundWindow();
        if (!IsEligibleWindow(hwnd) || !TryGetWindowRect(hwnd, out var current))
        {
            return;
        }

        // Native maximize is handled by WindowManagerService. This service is only
        // responsible for true application fullscreen modes such as browser F11.
        if (IsZoomed(hwnd))
        {
            return;
        }

        if (_constrained.TryGetValue(hwnd, out var state))
        {
            HandleConstrainedWindow(hwnd, current, state, display, layout);
            return;
        }

        if (CoversPhysicalDisplay(current, display))
        {
            var zone = ResolveZone(hwnd, display, layout);
            if (zone is null)
            {
                return;
            }

            var target = DisplayGeometry.ZoneToPixels(display, zone);
            if (RectsApproximatelyEqual(target, DisplayBounds(display)))
            {
                return;
            }

            var stateForFullscreen = new FullscreenState(
                zone.Id,
                IsBorderless(hwnd),
                DateTime.UtcNow,
                DateTime.UtcNow);

            _constrained[hwnd] = stateForFullscreen;
            ConstrainToZone(hwnd, target);
            return;
        }

        TrackNormalWindow(hwnd, current, display, layout);
    }

    private void HandleConstrainedWindow(
        IntPtr hwnd,
        PixelRect current,
        FullscreenState state,
        DisplayProfile display,
        LayoutProfile layout)
    {
        var zone = layout.Zones.FirstOrDefault(candidate => candidate.Id == state.ZoneId);
        if (zone is null)
        {
            _constrained.Remove(hwnd);
            return;
        }

        var target = DisplayGeometry.ZoneToPixels(display, zone);
        var now = DateTime.UtcNow;

        if (CoversPhysicalDisplay(current, display))
        {
            state = state with { LastFullscreenSignalUtc = now };
            _constrained[hwnd] = state;
            ConstrainToZone(hwnd, target);
            return;
        }

        if (RectsApproximatelyEqual(current, target))
        {
            // Browser F11 and most media fullscreen modes remove WS_CAPTION. Keep
            // enforcing while that borderless fullscreen style is active.
            if (IsBorderless(hwnd))
            {
                return;
            }

            // A captioned window can briefly regain its normal style before the
            // application finishes the fullscreen transition. Give that transition
            // a short grace period, then release it back to normal MUX tracking.
            if (now - state.LastFullscreenSignalUtc < TimeSpan.FromMilliseconds(550))
            {
                return;
            }

            _constrained.Remove(hwnd);
            TrackNormalWindow(hwnd, current, display, layout);
            return;
        }

        // The application moved/restored itself somewhere other than the physical
        // display or the MUX zone, which is the strongest signal that fullscreen ended.
        _constrained.Remove(hwnd);
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
        if (_processZones.TryGetValue(processId, out var processZoneId))
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

    private static void ConstrainToZone(IntPtr hwnd, PixelRect target)
    {
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            SwpNoZOrder | SwpNoActivate);
    }

    private static PixelRect DisplayBounds(DisplayProfile display)
        => new(display.LeftPx, display.TopPx, display.WidthPx, display.HeightPx);

    private static bool CoversPhysicalDisplay(PixelRect rect, DisplayProfile display)
    {
        var displayRect = DisplayBounds(display);
        return rect.Left <= displayRect.Left + PhysicalFullscreenTolerancePx &&
               rect.Top <= displayRect.Top + PhysicalFullscreenTolerancePx &&
               rect.Right >= displayRect.Right - PhysicalFullscreenTolerancePx &&
               rect.Bottom >= displayRect.Bottom - PhysicalFullscreenTolerancePx &&
               rect.Width >= displayRect.Width - PhysicalFullscreenTolerancePx * 2 &&
               rect.Height >= displayRect.Height - PhysicalFullscreenTolerancePx * 2;
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
        _constrained.Clear();
    }

    private sealed record FullscreenState(
        Guid ZoneId,
        bool WasBorderlessAtEntry,
        DateTime EnteredUtc,
        DateTime LastFullscreenSignalUtc);

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
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
}
