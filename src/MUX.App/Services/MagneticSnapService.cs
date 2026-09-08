using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Pixel-accurate magnetic snapping for normal top-level windows.
/// Snapping is calculated from DWM visible frame bounds, while SetWindowPos is applied to the
/// native window rectangle. Candidate geometry is refreshed continuously and every snap is
/// verified/corrected after Windows applies it, eliminating the small residual gaps that can be
/// introduced by invisible resize borders, DPI transitions, or framework rounding.
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
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int ResizeTolerancePx = 2;
    private const int VerificationPasses = 3;

    private readonly WinEventDelegate _eventDelegate;
    private readonly Dispatcher _dispatcher;
    private readonly List<IntPtr> _hooks = new();
    private readonly List<CandidateWindow> _candidates = new();

    private IntPtr _movingHwnd;
    private NativeRect _startRawRect;
    private bool _resizeDetected;
    private SnapAnchor? _horizontalAnchor;
    private SnapAnchor? _verticalAnchor;
    private NativeRect? _lastAppliedRawRect;
    private int _snapThresholdPx = 14;
    private int _releaseThresholdPx = 34;
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
            else
            {
                RemoveCandidate(hwnd);
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

        var dpi = EffectiveDpi(hwnd);
        _snapThresholdPx = ScaleForDpi(14, dpi);
        _releaseThresholdPx = ScaleForDpi(34, dpi);
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
            _horizontalAnchor = null;
            _verticalAnchor = null;
            return;
        }

        if (_resizeDetected)
        {
            return;
        }

        if (_lastAppliedRawRect is NativeRect lastApplied && PositionsEqual(raw, lastApplied))
        {
            // Ignore the event generated by our own SetWindowPos. The snap was already verified
            // synchronously immediately after that call.
            _lastAppliedRawRect = null;
            return;
        }

        RefreshCandidateGeometry();

        var deltaX = ResolveHorizontalDelta(visual, finalPass);
        var deltaY = ResolveVerticalDelta(visual, finalPass);
        if (deltaX == 0 && deltaY == 0)
        {
            _lastAppliedRawRect = null;
            return;
        }

        ApplyTranslation(hwnd, raw, deltaX, deltaY);
        VerifyAndCorrectFlush(hwnd);
    }

    private void ApplyTranslation(IntPtr hwnd, NativeRect raw, int deltaX, int deltaY)
    {
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        var adjusted = new NativeRect(
            raw.Left + deltaX,
            raw.Top + deltaY,
            raw.Right + deltaX,
            raw.Bottom + deltaY);

        _lastAppliedRawRect = adjusted;
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            adjusted.Left,
            adjusted.Top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    /// <summary>
    /// Windows can occasionally land one or two physical pixels away from the requested visible
    /// position because the raw HWND rectangle and DWM visible rectangle have different insets.
    /// Re-read the actual visible bounds after SetWindowPos and close any residual error. This is
    /// intentionally bounded to a few passes so snapping remains deterministic and flicker-free.
    /// </summary>
    private void VerifyAndCorrectFlush(IntPtr hwnd)
    {
        for (var pass = 0; pass < VerificationPasses; pass++)
        {
            if (!TryGetWindowGeometry(hwnd, out var raw, out var visual))
            {
                return;
            }

            RefreshAnchoredCandidates();

            var correctionX = _horizontalAnchor is SnapAnchor horizontal
                ? DesiredVisualLeft(horizontal, visual.Width) - visual.Left
                : 0;
            var correctionY = _verticalAnchor is SnapAnchor vertical
                ? DesiredVisualTop(vertical, visual.Height) - visual.Top
                : 0;

            if (correctionX == 0 && correctionY == 0)
            {
                _lastAppliedRawRect = raw;
                return;
            }

            ApplyTranslation(hwnd, raw, correctionX, correctionY);
        }
    }

    private int ResolveHorizontalDelta(NativeRect current, bool finalPass)
    {
        if (_horizontalAnchor is SnapAnchor locked && TryRefreshAnchorCandidate(ref locked))
        {
            _horizontalAnchor = locked;
            var desired = DesiredVisualLeft(locked, current.Width);
            var lockedDelta = desired - current.Left;
            if (Math.Abs(lockedDelta) <= _releaseThresholdPx)
            {
                return lockedDelta;
            }

            _horizontalAnchor = null;
        }
        else
        {
            _horizontalAnchor = null;
        }

        var threshold = finalPass
            ? Math.Max(_snapThresholdPx, ScaleForDpi(18, EffectiveDpi(_movingHwnd)))
            : _snapThresholdPx;

        var bestDistance = threshold + 1;
        SnapAnchor? best = null;

        foreach (var candidate in _candidates)
        {
            if (!candidate.Valid || !SpansNear(current.Top, current.Bottom, candidate.Visual.Top, candidate.Visual.Bottom, threshold))
            {
                continue;
            }

            EvaluateHorizontal(candidate, MovingEdge.Near, CandidateEdge.Near, current, threshold, ref bestDistance, ref best);
            EvaluateHorizontal(candidate, MovingEdge.Near, CandidateEdge.Far, current, threshold, ref bestDistance, ref best);
            EvaluateHorizontal(candidate, MovingEdge.Far, CandidateEdge.Near, current, threshold, ref bestDistance, ref best);
            EvaluateHorizontal(candidate, MovingEdge.Far, CandidateEdge.Far, current, threshold, ref bestDistance, ref best);
        }

        if (best is not SnapAnchor snap)
        {
            return 0;
        }

        _horizontalAnchor = snap;
        return DesiredVisualLeft(snap, current.Width) - current.Left;
    }

    private static void EvaluateHorizontal(
        CandidateWindow candidate,
        MovingEdge movingEdge,
        CandidateEdge candidateEdge,
        NativeRect current,
        int threshold,
        ref int bestDistance,
        ref SnapAnchor? best)
    {
        var anchor = new SnapAnchor(candidate.Hwnd, SnapAxis.Horizontal, movingEdge, candidateEdge, candidate.Visual);
        var desiredLeft = DesiredVisualLeft(anchor, current.Width);
        var distance = Math.Abs(desiredLeft - current.Left);
        if (distance > threshold)
        {
            return;
        }

        // Deterministic tie-break: when equally close, prefer edge-to-edge abutment over
        // same-side alignment. It makes side-by-side tiling feel intentional rather than sticky.
        var beatsTie = distance == bestDistance && best is SnapAnchor existing &&
                       IsAbutment(anchor) && !IsAbutment(existing);
        if (distance < bestDistance || beatsTie)
        {
            bestDistance = distance;
            best = anchor;
        }
    }

    private int ResolveVerticalDelta(NativeRect current, bool finalPass)
    {
        if (_verticalAnchor is SnapAnchor locked && TryRefreshAnchorCandidate(ref locked))
        {
            _verticalAnchor = locked;
            var desired = DesiredVisualTop(locked, current.Height);
            var lockedDelta = desired - current.Top;
            if (Math.Abs(lockedDelta) <= _releaseThresholdPx)
            {
                return lockedDelta;
            }

            _verticalAnchor = null;
        }
        else
        {
            _verticalAnchor = null;
        }

        var threshold = finalPass
            ? Math.Max(_snapThresholdPx, ScaleForDpi(18, EffectiveDpi(_movingHwnd)))
            : _snapThresholdPx;

        var bestDistance = threshold + 1;
        SnapAnchor? best = null;

        foreach (var candidate in _candidates)
        {
            if (!candidate.Valid || !SpansNear(current.Left, current.Right, candidate.Visual.Left, candidate.Visual.Right, threshold))
            {
                continue;
            }

            EvaluateVertical(candidate, MovingEdge.Near, CandidateEdge.Near, current, threshold, ref bestDistance, ref best);
            EvaluateVertical(candidate, MovingEdge.Near, CandidateEdge.Far, current, threshold, ref bestDistance, ref best);
            EvaluateVertical(candidate, MovingEdge.Far, CandidateEdge.Near, current, threshold, ref bestDistance, ref best);
            EvaluateVertical(candidate, MovingEdge.Far, CandidateEdge.Far, current, threshold, ref bestDistance, ref best);
        }

        if (best is not SnapAnchor snap)
        {
            return 0;
        }

        _verticalAnchor = snap;
        return DesiredVisualTop(snap, current.Height) - current.Top;
    }

    private static void EvaluateVertical(
        CandidateWindow candidate,
        MovingEdge movingEdge,
        CandidateEdge candidateEdge,
        NativeRect current,
        int threshold,
        ref int bestDistance,
        ref SnapAnchor? best)
    {
        var anchor = new SnapAnchor(candidate.Hwnd, SnapAxis.Vertical, movingEdge, candidateEdge, candidate.Visual);
        var desiredTop = DesiredVisualTop(anchor, current.Height);
        var distance = Math.Abs(desiredTop - current.Top);
        if (distance > threshold)
        {
            return;
        }

        var beatsTie = distance == bestDistance && best is SnapAnchor existing &&
                       IsAbutment(anchor) && !IsAbutment(existing);
        if (distance < bestDistance || beatsTie)
        {
            bestDistance = distance;
            best = anchor;
        }
    }

    private static int DesiredVisualLeft(SnapAnchor anchor, int movingWidth)
    {
        var candidateEdge = anchor.CandidateEdge == CandidateEdge.Near
            ? anchor.CandidateVisual.Left
            : anchor.CandidateVisual.Right;
        return anchor.MovingEdge == MovingEdge.Near
            ? candidateEdge
            : candidateEdge - movingWidth;
    }

    private static int DesiredVisualTop(SnapAnchor anchor, int movingHeight)
    {
        var candidateEdge = anchor.CandidateEdge == CandidateEdge.Near
            ? anchor.CandidateVisual.Top
            : anchor.CandidateVisual.Bottom;
        return anchor.MovingEdge == MovingEdge.Near
            ? candidateEdge
            : candidateEdge - movingHeight;
    }

    private static bool IsAbutment(SnapAnchor anchor)
    {
        return anchor.MovingEdge != (MovingEdge)anchor.CandidateEdge;
    }

    private static bool SpansNear(int aStart, int aEnd, int bStart, int bEnd, int tolerance)
    {
        return aEnd >= bStart - tolerance && bEnd >= aStart - tolerance;
    }

    private void CaptureCandidateWindows(IntPtr movingHwnd)
    {
        _candidates.Clear();

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == movingHwnd || !IsEligibleWindow(hwnd))
            {
                return true;
            }

            if (TryGetWindowGeometry(hwnd, out var raw, out var visual) &&
                raw.Width > 0 && visual.Width >= 80 && visual.Height >= 60)
            {
                _candidates.Add(new CandidateWindow(hwnd, visual, true));
            }

            return true;
        }, IntPtr.Zero);
    }

    private void RefreshCandidateGeometry()
    {
        for (var i = 0; i < _candidates.Count; i++)
        {
            var candidate = _candidates[i];
            if (!IsEligibleWindow(candidate.Hwnd) ||
                !TryGetWindowGeometry(candidate.Hwnd, out _, out var visual))
            {
                _candidates[i] = candidate with { Valid = false };
                continue;
            }

            _candidates[i] = candidate with { Visual = visual, Valid = true };
        }
    }

    private void RefreshAnchoredCandidates()
    {
        if (_horizontalAnchor is SnapAnchor horizontal && TryRefreshAnchorCandidate(ref horizontal))
        {
            _horizontalAnchor = horizontal;
        }
        else if (_horizontalAnchor is not null)
        {
            _horizontalAnchor = null;
        }

        if (_verticalAnchor is SnapAnchor vertical && TryRefreshAnchorCandidate(ref vertical))
        {
            _verticalAnchor = vertical;
        }
        else if (_verticalAnchor is not null)
        {
            _verticalAnchor = null;
        }
    }

    private bool TryRefreshAnchorCandidate(ref SnapAnchor anchor)
    {
        if (!IsEligibleWindow(anchor.CandidateHwnd) ||
            !TryGetWindowGeometry(anchor.CandidateHwnd, out _, out var visual))
        {
            return false;
        }

        anchor = anchor with { CandidateVisual = visual };
        return true;
    }

    private void RemoveCandidate(IntPtr hwnd)
    {
        _candidates.RemoveAll(candidate => candidate.Hwnd == hwnd);
        if (_horizontalAnchor?.CandidateHwnd == hwnd)
        {
            _horizontalAnchor = null;
        }
        if (_verticalAnchor?.CandidateHwnd == hwnd)
        {
            _verticalAnchor = null;
        }
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
            visual.Width <= 0 || visual.Height <= 0)
        {
            visual = raw;
        }

        return true;
    }

    private static bool PositionsEqual(NativeRect a, NativeRect b)
    {
        return a.Left == b.Left && a.Top == b.Top;
    }

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    private static int ScaleForDpi(int value, uint dpi)
    {
        var effectiveDpi = Math.Max(96u, dpi);
        return Math.Max(1, (int)Math.Round(value * effectiveDpi / 96.0));
    }

    private void ResetDrag()
    {
        _movingHwnd = IntPtr.Zero;
        _startRawRect = default;
        _resizeDetected = false;
        _horizontalAnchor = null;
        _verticalAnchor = null;
        _lastAppliedRawRect = null;
        _candidates.Clear();
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

    private enum SnapAxis
    {
        Horizontal,
        Vertical
    }

    private enum MovingEdge
    {
        Near = 0,
        Far = 1
    }

    private enum CandidateEdge
    {
        Near = 0,
        Far = 1
    }

    private readonly record struct CandidateWindow(IntPtr Hwnd, NativeRect Visual, bool Valid);

    private readonly record struct SnapAnchor(
        IntPtr CandidateHwnd,
        SnapAxis Axis,
        MovingEdge MovingEdge,
        CandidateEdge CandidateEdge,
        NativeRect CandidateVisual);

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
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
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
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
