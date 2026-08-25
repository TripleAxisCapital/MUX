using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MUX.Core.Geometry;
using MUX.Core.Models;

namespace MUX.App.Services;

public sealed class WindowManagerService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectStateChange = 0x800A;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectStart = 0x8000;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int SwRestore = 9;
    private const uint SwpNoActivate = 0x0010;
    private const int VkShift = 0x10;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const int NativeMaximizeCorrectionDelayMs = 140;
    private const int RectTolerancePx = 2;

    private readonly WinEventDelegate _eventDelegate;
    private readonly List<IntPtr> _hooks = new();
    private readonly ConcurrentDictionary<IntPtr, PixelRect> _pseudoMaximized = new();
    private readonly ConcurrentDictionary<IntPtr, PixelRect> _lastNormalRects = new();
    private readonly ConcurrentDictionary<IntPtr, Guid> _windowZones = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _suppressed = new();
    private readonly Dispatcher _dispatcher;
    private readonly ZoneOutlineService _outlineService = new();
    private DisplayProfile? _display;
    private LayoutProfile? _layout;
    private bool _enabled;
    private bool _snapOnDrag;
    private bool _showOutlines = true;
    private double _outlineThickness = 2.0;

    public WindowManagerService()
    {
        _eventDelegate = OnWinEvent;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    public void Configure(DisplayProfile? display, LayoutProfile? layout, bool enabled, bool snapOnDrag)
    {
        _display = display;
        _layout = layout;
        _enabled = enabled;
        _snapOnDrag = snapOnDrag;

        if (_hooks.Count == 0)
        {
            InstallHooks();
        }

        RefreshOutlines();
    }

    public void SetOutlineOptions(bool visible, double thickness)
    {
        _showOutlines = visible;
        _outlineThickness = Math.Clamp(thickness, 1.0, 6.0);
        RefreshOutlines();
    }

    public void RefreshOutlines()
    {
        _outlineService.Configure(_display, _layout, _enabled && _showOutlines, _outlineThickness);
    }

    public void ToggleForegroundZoneMaximize()
    {
        if (!_enabled || _display is null || _layout is null)
        {
            return;
        }

        var hwnd = GetForegroundWindow();
        if (!IsEligibleWindow(hwnd))
        {
            return;
        }

        if (_pseudoMaximized.TryRemove(hwnd, out var restore))
        {
            Suppress(hwnd, () => SetWindowRect(hwnd, restore));
            TrackNormalWindow(hwnd, restore);
            return;
        }

        if (!TryGetWindowRect(hwnd, out var current))
        {
            return;
        }

        var zone = DisplayGeometry.FindZoneForMaximize(_display, _layout, current);
        if (zone is null)
        {
            return;
        }

        TrackNormalWindow(hwnd, current);
        _pseudoMaximized[hwnd] = current;
        _windowZones[hwnd] = zone.Id;
        Suppress(hwnd, () => FitWindowToZone(hwnd, zone));
    }

    public void MoveForegroundToAdjacentZone(int direction)
    {
        if (!_enabled || _display is null || _layout is null || _layout.Zones.Count == 0)
        {
            return;
        }

        var hwnd = GetForegroundWindow();
        if (!IsEligibleWindow(hwnd) || !TryGetWindowRect(hwnd, out var current))
        {
            return;
        }

        var ordered = _layout.Zones
            .OrderBy(z => z.YInches)
            .ThenBy(z => z.XInches)
            .ToList();

        var active = ResolveAssignedZone(hwnd, current);
        var index = active is null ? 0 : ordered.FindIndex(z => z.Id == active.Id);
        if (index < 0) index = 0;

        index = (index + direction) % ordered.Count;
        if (index < 0) index += ordered.Count;

        var target = ordered[index];
        var wasPseudoMaximized = _pseudoMaximized.ContainsKey(hwnd);
        _windowZones[hwnd] = target.Id;

        if (wasPseudoMaximized)
        {
            Suppress(hwnd, () => FitWindowToZone(hwnd, target));
        }
        else
        {
            MoveWindowPreservingSize(hwnd, current, target);
        }
    }

    private void InstallHooks()
    {
        AddHook(EventSystemForeground, EventSystemForeground);
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventObjectDestroy, EventObjectDestroy);
        AddHook(EventObjectStateChange, EventObjectLocationChange);
    }

    private void AddHook(uint min, uint max)
    {
        var hook = SetWinEventHook(min, max, IntPtr.Zero, _eventDelegate, 0, 0, WineventOutOfContext);
        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_enabled || hwnd == IntPtr.Zero || _display is null || _layout is null)
        {
            return;
        }

        if (eventType >= EventObjectStart && idObject != ObjidWindow)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => HandleEvent(eventType, hwnd)));
    }

    private void HandleEvent(uint eventType, IntPtr hwnd)
    {
        if (!_enabled || _display is null || _layout is null)
        {
            return;
        }

        if (eventType == EventObjectDestroy)
        {
            ForgetWindow(hwnd);
            return;
        }

        if (_suppressed.ContainsKey(hwnd) || !IsEligibleWindow(hwnd))
        {
            return;
        }

        if (eventType == EventSystemMoveSizeStart)
        {
            _pseudoMaximized.TryRemove(hwnd, out _);
            return;
        }

        if (eventType == EventSystemMoveSizeEnd)
        {
            if (TryGetWindowRect(hwnd, out var movedRect))
            {
                TrackNormalWindow(hwnd, movedRect);

                if (_snapOnDrag && IsShiftDown())
                {
                    var zone = DisplayGeometry.FindBestZone(_display, _layout, movedRect);
                    if (zone is not null)
                    {
                        _pseudoMaximized[hwnd] = movedRect;
                        _windowZones[hwnd] = zone.Id;
                        Suppress(hwnd, () => FitWindowToZone(hwnd, zone));
                    }
                }
            }

            return;
        }

        if ((eventType == EventObjectStateChange || eventType == EventObjectLocationChange || eventType == EventSystemForeground) && IsZoomed(hwnd))
        {
            HandleNativeMaximize(hwnd);
            return;
        }

        if (eventType == EventObjectLocationChange || eventType == EventSystemForeground)
        {
            TrackNormalWindow(hwnd);
        }
    }

    private void HandleNativeMaximize(IntPtr hwnd)
    {
        if (_display is null || _layout is null)
        {
            return;
        }

        if (_pseudoMaximized.TryRemove(hwnd, out var previousRestoreRect))
        {
            Suppress(hwnd, () =>
            {
                ShowWindow(hwnd, SwRestore);
                SetWindowRect(hwnd, previousRestoreRect);
            });
            TrackNormalWindow(hwnd, previousRestoreRect);
            return;
        }

        var normal = GetBestRestoreRect(hwnd);
        if (normal is null)
        {
            return;
        }

        var zone = ResolveAssignedZone(hwnd, normal.Value);
        if (zone is null)
        {
            return;
        }

        _lastNormalRects[hwnd] = normal.Value;
        _windowZones[hwnd] = zone.Id;
        _pseudoMaximized[hwnd] = normal.Value;
        ApplyNativeZoneMaximize(hwnd, zone);
    }

    private PixelRect? GetBestRestoreRect(IntPtr hwnd)
    {
        if (_lastNormalRects.TryGetValue(hwnd, out var cached) && cached.Width > 0 && cached.Height > 0)
        {
            return cached;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var normal = new PixelRect(
            placement.NormalPosition.Left,
            placement.NormalPosition.Top,
            placement.NormalPosition.Right - placement.NormalPosition.Left,
            placement.NormalPosition.Bottom - placement.NormalPosition.Top);

        return normal.Width > 0 && normal.Height > 0 ? normal : null;
    }

    private VirtualMonitorZone? ResolveAssignedZone(IntPtr hwnd, PixelRect fallbackRect)
    {
        if (_layout is null || _display is null)
        {
            return null;
        }

        if (_windowZones.TryGetValue(hwnd, out var zoneId))
        {
            var assigned = _layout.Zones.FirstOrDefault(zone => zone.Id == zoneId);
            if (assigned is not null)
            {
                return assigned;
            }

            _windowZones.TryRemove(hwnd, out _);
        }

        return DisplayGeometry.FindZoneForMaximize(_display, _layout, fallbackRect);
    }

    private void TrackNormalWindow(IntPtr hwnd)
    {
        if (TryGetWindowRect(hwnd, out var rect))
        {
            TrackNormalWindow(hwnd, rect);
        }
    }

    private void TrackNormalWindow(IntPtr hwnd, PixelRect rect)
    {
        if (_display is null || _layout is null || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _lastNormalRects[hwnd] = rect;
        var zone = DisplayGeometry.FindZoneForMaximize(_display, _layout, rect);
        if (zone is null)
        {
            _windowZones.TryRemove(hwnd, out _);
        }
        else
        {
            _windowZones[hwnd] = zone.Id;
        }
    }

    private void ApplyNativeZoneMaximize(IntPtr hwnd, VirtualMonitorZone zone)
    {
        Suppress(hwnd, () =>
        {
            ShowWindow(hwnd, SwRestore);
            FitWindowToZone(hwnd, zone);
        });

        ScheduleNativeMaximizeCorrection(hwnd, zone.Id);
    }

    private void ScheduleNativeMaximizeCorrection(IntPtr hwnd, Guid zoneId)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(NativeMaximizeCorrectionDelayMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (!_enabled || _display is null || _layout is null || !_pseudoMaximized.ContainsKey(hwnd) || !IsEligibleWindow(hwnd))
            {
                return;
            }

            var zone = _layout.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
            if (zone is null)
            {
                return;
            }

            var target = DisplayGeometry.ZoneToPixels(_display, zone);
            var needsCorrection = IsZoomed(hwnd) || !TryGetWindowRect(hwnd, out var current) || !RectsApproximatelyEqual(current, target);
            if (!needsCorrection)
            {
                return;
            }

            Suppress(hwnd, () =>
            {
                if (IsZoomed(hwnd))
                {
                    ShowWindow(hwnd, SwRestore);
                }

                SetWindowRect(hwnd, target);
            });
        };

        timer.Start();
    }

    private void FitWindowToZone(IntPtr hwnd, VirtualMonitorZone zone)
    {
        if (_display is null)
        {
            return;
        }

        SetWindowRect(hwnd, DisplayGeometry.ZoneToPixels(_display, zone));
    }

    private void MoveWindowPreservingSize(IntPtr hwnd, PixelRect current, VirtualMonitorZone zone)
    {
        if (_display is null)
        {
            return;
        }

        var target = DisplayGeometry.ZoneToPixels(_display, zone);
        var width = Math.Min(current.Width, target.Width);
        var height = Math.Min(current.Height, target.Height);
        var left = target.Left + Math.Max(0, (target.Width - width) / 2);
        var top = target.Top + Math.Max(0, (target.Height - height) / 2);
        var moved = new PixelRect(left, top, width, height);

        Suppress(hwnd, () => SetWindowRect(hwnd, moved));
        _lastNormalRects[hwnd] = moved;
        _windowZones[hwnd] = zone.Id;
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

    private static void SetWindowRect(IntPtr hwnd, PixelRect rect)
    {
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate);
    }

    private static bool IsShiftDown() => (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        return (exStyle & WsExToolWindow) == 0;
    }

    private void ForgetWindow(IntPtr hwnd)
    {
        _pseudoMaximized.TryRemove(hwnd, out _);
        _lastNormalRects.TryRemove(hwnd, out _);
        _windowZones.TryRemove(hwnd, out _);
        _suppressed.TryRemove(hwnd, out _);
    }

    private void Suppress(IntPtr hwnd, Action action)
    {
        _suppressed[hwnd] = 1;
        try
        {
            action();
        }
        finally
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _suppressed.TryRemove(hwnd, out _);
            };
            timer.Start();
        }
    }

    public void Dispose()
    {
        _outlineService.Dispose();
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
