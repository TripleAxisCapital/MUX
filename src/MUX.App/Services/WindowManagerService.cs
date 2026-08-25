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
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int SwRestore = 9;
    private const uint SwpNoActivate = 0x0010;
    private const int VkShift = 0x10;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

    private readonly WinEventDelegate _eventDelegate;
    private readonly List<IntPtr> _hooks = new();
    private readonly ConcurrentDictionary<IntPtr, PixelRect> _pseudoMaximized = new();
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

        _pseudoMaximized[hwnd] = current;
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

        var active = DisplayGeometry.FindZoneForMaximize(_display, _layout, current);
        var index = active is null ? 0 : ordered.FindIndex(z => z.Id == active.Id);
        if (index < 0) index = 0;

        index = (index + direction) % ordered.Count;
        if (index < 0) index += ordered.Count;

        var target = ordered[index];
        var wasPseudoMaximized = _pseudoMaximized.ContainsKey(hwnd);

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
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
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

        if (eventType >= EventObjectLocationChange && idObject != ObjidWindow)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => HandleEvent(eventType, hwnd)));
    }

    private void HandleEvent(uint eventType, IntPtr hwnd)
    {
        if (!_enabled || _display is null || _layout is null || _suppressed.ContainsKey(hwnd) || !IsEligibleWindow(hwnd))
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
            if (_snapOnDrag && IsShiftDown() && TryGetWindowRect(hwnd, out var movedRect))
            {
                var zone = DisplayGeometry.FindBestZone(_display, _layout, movedRect);
                if (zone is not null)
                {
                    _pseudoMaximized[hwnd] = movedRect;
                    Suppress(hwnd, () => FitWindowToZone(hwnd, zone));
                }
            }

            return;
        }

        if ((eventType == EventObjectLocationChange || eventType == EventSystemForeground) && IsZoomed(hwnd))
        {
            HandleNativeMaximize(hwnd);
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
            return;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }

        var normal = new PixelRect(
            placement.NormalPosition.Left,
            placement.NormalPosition.Top,
            placement.NormalPosition.Right - placement.NormalPosition.Left,
            placement.NormalPosition.Bottom - placement.NormalPosition.Top);

        var zone = DisplayGeometry.FindZoneForMaximize(_display, _layout, normal);
        if (zone is null)
        {
            return;
        }

        _pseudoMaximized[hwnd] = normal;
        Suppress(hwnd, () =>
        {
            ShowWindow(hwnd, SwRestore);
            FitWindowToZone(hwnd, zone);
        });
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

        Suppress(hwnd, () => SetWindowRect(hwnd, new PixelRect(left, top, width, height)));
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
