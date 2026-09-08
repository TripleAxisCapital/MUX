using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Provides lightweight, process-external magnetic edge snapping while the user moves a normal top-level window.
/// The service never resizes windows: it only adjusts X/Y so visible window edges align or abut precisely.
/// </summary>
public sealed class MagneticSnapService : IDisposable
{
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int ResizeTolerancePx = 2;

    private readonly WinEventDelegate _eventDelegate;
    private readonly Dispatcher _dispatcher;
    private readonly List<IntPtr> _hooks = new();
    private readonly List<NativeRect> _candidateVisualRects = new();

    private IntPtr _movingHwnd;
    private NativeRect _startRawRect;
    private bool _resizeDetected;
    private int? _lockedVisualLeft;
    private int? _lockedVisualTop;
    private NativeRect? _lastAppliedRawRect;
    private int _snapThresholdPx = 14;
    private int _releaseThresholdPx = 30;
    private bool _enabled = true;
    private bool _disposed;

    public MagneticSnapService(bool enabled = true)
    {
        _enabled = enabled;
        _eventDelegate = OnWinEvent;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        InstallHooks();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (!value)
            {
                ResetDrag();
            }
        }
    }

    private void InstallHooks()
    {
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventObjectDestroy, EventObjectDestroy);
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

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || !_enabled || hwnd == IntPtr.Zero)
        {
            return;
        }

        if ((eventType == EventObjectLocationChange || eventType == EventObjectDestroy) && idObject != ObjidWindow)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() => HandleEvent(eventType, hwnd)));
    }

    private void HandleEvent(uint eventType, IntPtr hwnd)
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        if (eventType == EventSystemMoveSizeStart)
        {
            BeginDrag(hwnd);
            return;
        }

        if (eventType == EventObjectDestroy)
        {
            if (hwnd == _movingHwnd)
            {
                ResetDrag();
            }
            return;
        }

        if (hwnd == IntPtr.Zero || hwnd != _movingHwnd)
        {
            return;
        }

        if (eventType == EventObjectLocationChange)
        {
            ApplyMagnet(hwnd, finalPass: false);
            return;
        }

        if (eventType == EventSystemMoveSizeEnd)
        {
            ApplyMagnet(hwnd, finalPass: true);
            ResetDrag();
        }
    }

    private void BeginDrag(IntPtr hwnd)
    {
        ResetDrag();

        if (!IsEligibleWindow(hwnd) || !TryGetWindowGeometry(hwnd, out var raw, out _))
        {
            return;
        }

        _movingHwnd = hwnd;
        _startRawRect = raw;
        _resizeDetected = false;
        _lastAppliedRawRect = null;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        _snapThresholdPx = ScaleForDpi(14, dpi);
        _releaseThresholdPx = ScaleForDpi(30, dpi);
        CaptureCandidateWindows(hwnd);
    }

    private void ApplyMagnet(IntPtr hwnd, bool finalPass)
    {
        if (!TryGetWindowGeometry(hwnd, out var raw, out var visual))
        {
            return;
        }

        if (Math.Abs(raw.Width - _startRawRect.Width) > ResizeTolerancePx ||
            Math.Abs(raw.Height - _startRawRect.Height) > ResizeTolerancePx)
        {
            _resizeDetected = true;
            _lockedVisualLeft = null;
            _lockedVisualTop = null;
            return;
        }

        if (_resizeDetected)
        {
            return;
        }

        if (_lastAppliedRawRect is NativeRect lastApplied && PositionsEqual(raw, lastApplied))
        {
            // This is the location notification generated by our own SetWindowPos call.
            return;
        }

        var deltaX = ResolveHorizontalDelta(visual, finalPass);
        var deltaY = ResolveVerticalDelta(visual, finalPass);
        if (deltaX == 0 && deltaY == 0)
        {
            _lastAppliedRawRect = null;
            return;
        }

        var adjusted = new NativeRect
        {
            Left = raw.Left + deltaX,
            Top = raw.Top + deltaY,
            Right = raw.Right + deltaX,
            Bottom = raw.Bottom + deltaY
        };

        _lastAppliedRawRect = adjusted;
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            adjusted.Left,
            adjusted.Top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private int ResolveHorizontalDelta(NativeRect current, bool finalPass)
    {
        if (_lockedVisualLeft is int lockedLeft)
        {
            var lockedDelta = lockedLeft - current.Left;
            if (Math.Abs(lockedDelta) <= _releaseThresholdPx)
            {
                return lockedDelta;
            }

            _lockedVisualLeft = null;
        }

        var threshold = finalPass ? Math.Max(_snapThresholdPx, ScaleForDpi(18, GetDpiForWindow(_movingHwnd))) : _snapThresholdPx;
        var bestDistance = threshold + 1;
        int? bestLeft = null;

        foreach (var candidate in _candidateVisualRects)
        {
            if (!SpansNear(current.Top, current.Bottom, candidate.Top, candidate.Bottom, threshold))
            {
                continue;
            }

            EvaluateHorizontal(candidate.Left, current, threshold, ref bestDistance, ref bestLeft);
            EvaluateHorizontal(candidate.Right, current, threshold, ref bestDistance, ref bestLeft);
            EvaluateHorizontal(candidate.Right - current.Width, current, threshold, ref bestDistance, ref bestLeft);
            EvaluateHorizontal(candidate.Left - current.Width, current, threshold, ref bestDistance, ref bestLeft);
        }

        if (bestLeft is not int snapLeft)
        {
            return 0;
        }

        _lockedVisualLeft = snapLeft;
        return snapLeft - current.Left;
    }

    private static void EvaluateHorizontal(
        int desiredLeft,
        NativeRect current,
        int threshold,
        ref int bestDistance,
        ref int? bestLeft)
    {
        var distance = Math.Abs(desiredLeft - current.Left);
        if (distance <= threshold && distance < bestDistance)
        {
            bestDistance = distance;
            bestLeft = desiredLeft;
        }
    }

    private int ResolveVerticalDelta(NativeRect current, bool finalPass)
    {
        if (_lockedVisualTop is int lockedTop)
        {
            var lockedDelta = lockedTop - current.Top;
            if (Math.Abs(lockedDelta) <= _releaseThresholdPx)
            {
                return lockedDelta;
            }

            _lockedVisualTop = null;
        }

        var threshold = finalPass ? Math.Max(_snapThresholdPx, ScaleForDpi(18, GetDpiForWindow(_movingHwnd))) : _snapThresholdPx;
        var bestDistance = threshold + 1;
        int? bestTop = null;

        foreach (var candidate in _candidateVisualRects)
        {
            if (!SpansNear(current.Left, current.Right, candidate.Left, candidate.Right, threshold))
            {
                continue;
            }

            EvaluateVertical(candidate.Top, current, threshold, ref bestDistance, ref bestTop);
            EvaluateVertical(candidate.Bottom, current, threshold, ref bestDistance, ref bestTop);
            EvaluateVertical(candidate.Bottom - current.Height, current, threshold, ref bestDistance, ref bestTop);
            EvaluateVertical(candidate.Top - current.Height, current, threshold, ref bestDistance, ref bestTop);
        }

        if (bestTop is not int snapTop)
        {
            return 0;
        }

        _lockedVisualTop = snapTop;
        return snapTop - current.Top;
    }

    private static void EvaluateVertical(
        int desiredTop,
        NativeRect current,
        int threshold,
        ref int bestDistance,
        ref int? bestTop)
    {
        var distance = Math.Abs(desiredTop - current.Top);
        if (distance <= threshold && distance < bestDistance)
        {
            bestDistance = distance;
            bestTop = desiredTop;
        }
    }

    private static bool SpansNear(int aStart, int aEnd, int bStart, int bEnd, int tolerance)
    {
        return aEnd >= bStart - tolerance && bEnd >= aStart - tolerance;
    }

    private void CaptureCandidateWindows(IntPtr movingHwnd)
    {
        _candidateVisualRects.Clear();

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == movingHwnd || !IsEligibleWindow(hwnd))
            {
                return true;
            }

            if (TryGetWindowGeometry(hwnd, out _, out var visual) && visual.Width >= 80 && visual.Height >= 60)
            {
                _candidateVisualRects.Add(visual);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        var cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetWindowGeometry(IntPtr hwnd, out NativeRect raw, out NativeRect visual)
    {
        if (!GetWindowRect(hwnd, out raw) || raw.Width <= 0 || raw.Height <= 0)
        {
            visual = default;
            return false;
        }

        if (DwmGetWindowAttribute(
                hwnd,
                DwmwaExtendedFrameBounds,
                out visual,
                Marshal.SizeOf<NativeRect>()) != 0 ||
            visual.Width <= 0 ||
            visual.Height <= 0)
        {
            visual = raw;
        }

        return true;
    }

    private static bool PositionsEqual(NativeRect a, NativeRect b)
    {
        return a.Left == b.Left && a.Top == b.Top;
    }

    private static int ScaleForDpi(int value, uint dpi)
    {
        var effectiveDpi = Math.Max(96u, dpi);
        return (int)Math.Round(value * effectiveDpi / 96.0);
    }

    private void ResetDrag()
    {
        _movingHwnd = IntPtr.Zero;
        _startRawRect = default;
        _resizeDetected = false;
        _lockedVisualLeft = null;
        _lockedVisualTop = null;
        _lastAppliedRawRect = null;
        _candidateVisualRects.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetDrag();

        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);

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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHook,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr lParam);

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
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

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
