using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Links two touching top-level windows into a rigid positional pair. Moving either member
/// translates the other by exactly the same X/Y delta while preserving each window's size.
/// </summary>
public sealed class WindowLinkService : IDisposable
{
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private readonly Dictionary<IntPtr, LinkedPair> _byWindow = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly WinEventDelegate _eventDelegate;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public WindowLinkService()
    {
        _eventDelegate = OnWinEvent;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AddHook(EventObjectDestroy, EventObjectDestroy);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
    }

    public event EventHandler? Changed;

    public bool IsLinked(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && _byWindow.ContainsKey(hwnd);
    }

    public IntPtr GetPartner(IntPtr hwnd)
    {
        return _byWindow.TryGetValue(hwnd, out var pair) ? pair.Other(hwnd) : IntPtr.Zero;
    }

    /// <summary>
    /// Returns the currently linked partner, or the best flush-touching candidate that can be linked.
    /// </summary>
    public IntPtr FindTouchingWindow(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (_byWindow.TryGetValue(hwnd, out var linked))
        {
            return linked.Other(hwnd);
        }

        return FindTouchingWindowCore(hwnd);
    }

    /// <summary>
    /// Toggles a rigid pair for the supplied window. Returns true when the window is linked after
    /// the operation and false when it is unlinked or no eligible touching partner exists.
    /// </summary>
    public bool ToggleLink(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (_byWindow.TryGetValue(hwnd, out var existing))
        {
            RemovePair(existing);
            RaiseChanged();
            return false;
        }

        var partner = FindTouchingWindowCore(hwnd);
        if (partner == IntPtr.Zero || _byWindow.ContainsKey(partner))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var first) ||
            !GetWindowRect(partner, out var second) ||
            first.Width <= 0 ||
            first.Height <= 0 ||
            second.Width <= 0 ||
            second.Height <= 0)
        {
            return false;
        }

        var pair = new LinkedPair(hwnd, partner, first, second);
        _byWindow[hwnd] = pair;
        _byWindow[partner] = pair;
        RaiseChanged();
        return true;
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
        if (_disposed || hwnd == IntPtr.Zero || idObject != ObjidWindow)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => HandleWinEvent(eventType, hwnd)));
        }
        catch
        {
            // The application may be shutting down.
        }
    }

    private void HandleWinEvent(uint eventType, IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }

        if (eventType == EventObjectDestroy)
        {
            if (_byWindow.TryGetValue(hwnd, out var destroyedPair))
            {
                RemovePair(destroyedPair);
                RaiseChanged();
            }
            return;
        }

        if (eventType != EventObjectLocationChange || !_byWindow.TryGetValue(hwnd, out var pair))
        {
            return;
        }

        PropagateMovement(pair, hwnd);
    }

    private void PropagateMovement(LinkedPair pair, IntPtr sourceHwnd)
    {
        if (pair.Applying || !_byWindow.ContainsKey(sourceHwnd))
        {
            return;
        }

        var partnerHwnd = pair.Other(sourceHwnd);
        if (partnerHwnd == IntPtr.Zero || !IsWindow(sourceHwnd) || !IsWindow(partnerHwnd))
        {
            RemovePair(pair);
            RaiseChanged();
            return;
        }

        if (!GetWindowRect(sourceHwnd, out var sourceCurrent) ||
            !GetWindowRect(partnerHwnd, out var partnerCurrent))
        {
            return;
        }

        var sourcePrevious = pair.GetLast(sourceHwnd);

        // Linking is intentionally positional. A resize updates the baseline but does not resize
        // the partner or break the pair.
        if (sourceCurrent.Width != sourcePrevious.Width || sourceCurrent.Height != sourcePrevious.Height)
        {
            pair.SetLast(sourceHwnd, sourceCurrent);
            pair.SetLast(partnerHwnd, partnerCurrent);
            return;
        }

        var deltaX = sourceCurrent.Left - sourcePrevious.Left;
        var deltaY = sourceCurrent.Top - sourcePrevious.Top;
        if (deltaX == 0 && deltaY == 0)
        {
            pair.SetLast(sourceHwnd, sourceCurrent);
            pair.SetLast(partnerHwnd, partnerCurrent);
            return;
        }

        pair.Applying = true;
        try
        {
            SetWindowPos(
                partnerHwnd,
                IntPtr.Zero,
                partnerCurrent.Left + deltaX,
                partnerCurrent.Top + deltaY,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

            pair.SetLast(sourceHwnd, sourceCurrent);

            if (GetWindowRect(partnerHwnd, out var partnerApplied))
            {
                pair.SetLast(partnerHwnd, partnerApplied);
            }
            else
            {
                pair.SetLast(
                    partnerHwnd,
                    new NativeRect(
                        partnerCurrent.Left + deltaX,
                        partnerCurrent.Top + deltaY,
                        partnerCurrent.Right + deltaX,
                        partnerCurrent.Bottom + deltaY));
            }
        }
        finally
        {
            pair.Applying = false;
        }
    }

    private IntPtr FindTouchingWindowCore(IntPtr hwnd)
    {
        if (!IsEligibleWindow(hwnd) || !TryGetVisualRect(hwnd, out var source))
        {
            return IntPtr.Zero;
        }

        var dpi = EffectiveDpi(hwnd);
        var edgeTolerance = ScaleForDpi(4, dpi);
        var minimumOverlap = ScaleForDpi(36, dpi);

        IntPtr bestHwnd = IntPtr.Zero;
        long bestScore = long.MinValue;

        EnumWindows((candidateHwnd, _) =>
        {
            if (candidateHwnd == hwnd ||
                _byWindow.ContainsKey(candidateHwnd) ||
                !IsEligibleWindow(candidateHwnd) ||
                !TryGetVisualRect(candidateHwnd, out var candidate))
            {
                return true;
            }

            var score = TouchScore(source, candidate, edgeTolerance, minimumOverlap);
            if (score > bestScore)
            {
                bestScore = score;
                bestHwnd = candidateHwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestScore > long.MinValue ? bestHwnd : IntPtr.Zero;
    }

    private static long TouchScore(NativeRect a, NativeRect b, int tolerance, int minimumOverlap)
    {
        long best = long.MinValue;

        var verticalOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (verticalOverlap >= minimumOverlap)
        {
            ScoreEdge(Math.Abs(a.Right - b.Left), verticalOverlap, tolerance, ref best);
            ScoreEdge(Math.Abs(a.Left - b.Right), verticalOverlap, tolerance, ref best);
        }

        var horizontalOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (horizontalOverlap >= minimumOverlap)
        {
            ScoreEdge(Math.Abs(a.Bottom - b.Top), horizontalOverlap, tolerance, ref best);
            ScoreEdge(Math.Abs(a.Top - b.Bottom), horizontalOverlap, tolerance, ref best);
        }

        return best;
    }

    private static void ScoreEdge(int gap, int overlap, int tolerance, ref long best)
    {
        if (gap > tolerance)
        {
            return;
        }

        // Prefer the longest shared edge, then the smallest residual gap.
        var score = (long)overlap * 1000L - gap;
        if (score > best)
        {
            best = score;
        }
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        if ((GetWindowExStyle(hwnd) & WsExToolWindow) != 0)
        {
            return false;
        }

        if (IsWindowCloaked(hwnd))
        {
            return false;
        }

        var className = GetWindowClassName(hwnd);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
        {
            return false;
        }

        return TryGetVisualRect(hwnd, out var rect) && rect.Width >= 80 && rect.Height >= 60;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[128];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static bool TryGetVisualRect(IntPtr hwnd, out NativeRect rect)
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

    private static long GetWindowExStyle(IntPtr hwnd)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr(hwnd, GwlExStyle).ToInt64()
            : GetWindowLong(hwnd, GwlExStyle);
    }

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    private static int ScaleForDpi(int value, uint dpi)
    {
        return Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));
    }

    private void RemovePair(LinkedPair pair)
    {
        _byWindow.Remove(pair.A);
        _byWindow.Remove(pair.B);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // UI listeners are auxiliary.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var hook in _hooks)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        _hooks.Clear();
        _byWindow.Clear();
    }

    private sealed class LinkedPair
    {
        public LinkedPair(IntPtr a, IntPtr b, NativeRect lastA, NativeRect lastB)
        {
            A = a;
            B = b;
            LastA = lastA;
            LastB = lastB;
        }

        public IntPtr A { get; }
        public IntPtr B { get; }
        public NativeRect LastA { get; private set; }
        public NativeRect LastB { get; private set; }
        public bool Applying { get; set; }

        public IntPtr Other(IntPtr hwnd)
        {
            if (hwnd == A) return B;
            if (hwnd == B) return A;
            return IntPtr.Zero;
        }

        public NativeRect GetLast(IntPtr hwnd)
        {
            return hwnd == A ? LastA : LastB;
        }

        public void SetLast(IntPtr hwnd, NativeRect rect)
        {
            if (hwnd == A)
            {
                LastA = rect;
            }
            else if (hwnd == B)
            {
                LastB = rect;
            }
        }
    }

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

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
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
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
