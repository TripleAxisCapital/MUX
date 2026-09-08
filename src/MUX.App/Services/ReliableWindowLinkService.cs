using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Links two edge-attached top-level windows as a rigid visual assembly.
///
/// The attachment is stored as a real edge constraint (right-to-left, left-to-right,
/// bottom-to-top, or top-to-bottom) plus the exact offset along the shared edge. During an
/// interactive drag/resize, the window the user grabbed is the authoritative leader and the
/// partner is solved to an absolute visual position from that constraint. This deliberately does
/// not accumulate deltas from the follower's current position, so DWM, DPI and magnetic-snap
/// corrections cannot introduce sideways drift over time.
/// </summary>
public sealed class ReliableWindowLinkService : IDisposable
{
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
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
    private const int VerificationPasses = 4;

    private static readonly TimeSpan PostDragStabilization = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan SyntheticMoveSuppression = TimeSpan.FromMilliseconds(220);

    private readonly Dictionary<IntPtr, LinkedPair> _byWindow = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly WinEventDelegate _eventDelegate;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ReliableWindowLinkService()
    {
        _eventDelegate = OnWinEvent;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventObjectDestroy, EventObjectDestroy);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
    }

    public event EventHandler? Changed;

    public bool IsLinked(IntPtr hwnd) => hwnd != IntPtr.Zero && _byWindow.ContainsKey(hwnd);

    public IntPtr GetPartner(IntPtr hwnd)
        => _byWindow.TryGetValue(hwnd, out var pair) ? pair.Other(hwnd) : IntPtr.Zero;

    public IntPtr FindAttachablePartner(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (_byWindow.TryGetValue(hwnd, out var linked))
        {
            return linked.Other(hwnd);
        }

        return FindAttachablePartnerCore(hwnd)?.Hwnd ?? IntPtr.Zero;
    }

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

        var candidate = FindAttachablePartnerCore(hwnd);
        if (candidate is null || _byWindow.ContainsKey(candidate.Value.Hwnd))
        {
            return false;
        }

        var partner = candidate.Value.Hwnd;
        if (!TryGetWindowGeometry(hwnd, out var firstRaw, out var firstVisual) ||
            !TryGetWindowGeometry(partner, out var secondRaw, out var secondVisual) ||
            firstRaw.Width <= 0 || firstRaw.Height <= 0 ||
            secondRaw.Width <= 0 || secondRaw.Height <= 0)
        {
            return false;
        }

        var alongOffset = candidate.Value.Attachment is AttachmentEdge.ARightToBLeft or AttachmentEdge.ALeftToBRight
            ? secondVisual.Top - firstVisual.Top
            : secondVisual.Left - firstVisual.Left;

        var pair = new LinkedPair(
            hwnd,
            partner,
            candidate.Value.Attachment,
            alongOffset,
            firstRaw,
            secondRaw);

        _byWindow[hwnd] = pair;
        _byWindow[partner] = pair;

        EnforceRelationship(pair, hwnd, finalPass: true);
        pair.BeginStabilization(hwnd, PostDragStabilization);
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
        if (_disposed || hwnd == IntPtr.Zero)
        {
            return;
        }

        if ((eventType == EventObjectDestroy || eventType == EventObjectLocationChange) && idObject != ObjidWindow)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => HandleEvent(eventType, hwnd)));
        }
        catch
        {
        }
    }

    private void HandleEvent(uint eventType, IntPtr hwnd)
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

        if (!_byWindow.TryGetValue(hwnd, out var pair))
        {
            return;
        }

        if (eventType == EventSystemMoveSizeStart)
        {
            BeginInteractiveMove(pair, hwnd);
            return;
        }

        if (eventType == EventSystemMoveSizeEnd)
        {
            EndInteractiveMove(pair, hwnd);
            return;
        }

        if (eventType == EventObjectLocationChange)
        {
            HandleLocationChange(pair, hwnd);
        }
    }

    private void BeginInteractiveMove(LinkedPair pair, IntPtr hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return;
        }

        pair.ActiveLeader = hwnd;
        pair.BeginStabilization(hwnd, TimeSpan.FromDays(1));
        EnforceRelationship(pair, hwnd, finalPass: true);
    }

    private void EndInteractiveMove(LinkedPair pair, IntPtr hwnd)
    {
        if (pair.ActiveLeader != IntPtr.Zero && pair.ActiveLeader != hwnd)
        {
            return;
        }

        var leader = pair.ActiveLeader != IntPtr.Zero ? pair.ActiveLeader : hwnd;
        EnforceRelationship(pair, leader, finalPass: true);
        pair.ActiveLeader = IntPtr.Zero;
        pair.BeginStabilization(leader, PostDragStabilization);

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    if (_disposed || !_byWindow.TryGetValue(leader, out var current) || !ReferenceEquals(current, pair))
                    {
                        return;
                    }

                    EnforceRelationship(pair, leader, finalPass: true);
                }));
        }
        catch
        {
        }
    }

    private void HandleLocationChange(LinkedPair pair, IntPtr changedHwnd)
    {
        if (pair.Applying)
        {
            return;
        }

        if (!IsWindow(pair.A) || !IsWindow(pair.B))
        {
            RemovePair(pair);
            RaiseChanged();
            return;
        }

        if (pair.ActiveLeader != IntPtr.Zero)
        {
            if (changedHwnd == pair.ActiveLeader)
            {
                EnforceRelationship(pair, pair.ActiveLeader, finalPass: false);
            }
            return;
        }

        if (pair.TryGetStabilizationLeader(out var stabilizingLeader))
        {
            if (changedHwnd == stabilizingLeader || !pair.IsSyntheticSuppressed(changedHwnd))
            {
                EnforceRelationship(pair, stabilizingLeader, finalPass: false);
            }
            return;
        }

        if (pair.IsSyntheticSuppressed(changedHwnd))
        {
            return;
        }

        EnforceRelationship(pair, changedHwnd, finalPass: true);
        pair.BeginStabilization(changedHwnd, PostDragStabilization);
    }

    private void EnforceRelationship(LinkedPair pair, IntPtr leaderHwnd, bool finalPass)
    {
        if (pair.Applying || leaderHwnd == IntPtr.Zero)
        {
            return;
        }

        var followerHwnd = pair.Other(leaderHwnd);
        if (followerHwnd == IntPtr.Zero || !IsWindow(leaderHwnd) || !IsWindow(followerHwnd))
        {
            return;
        }

        if (!TryGetWindowGeometry(leaderHwnd, out _, out var leaderVisual) ||
            !TryGetWindowGeometry(followerHwnd, out var followerRaw, out var followerVisual))
        {
            return;
        }

        var desired = DesiredFollowerVisualOrigin(pair, leaderHwnd, leaderVisual, followerVisual);

        pair.Applying = true;
        try
        {
            MoveWindowToVisualOrigin(followerHwnd, followerRaw, followerVisual, desired.Left, desired.Top);
            pair.SuppressSynthetic(followerHwnd, SyntheticMoveSuppression);

            if (finalPass)
            {
                VerifyRigidAttachment(pair, leaderHwnd, followerHwnd);
            }

            if (GetWindowRect(pair.A, out var currentA)) pair.LastA = currentA;
            if (GetWindowRect(pair.B, out var currentB)) pair.LastB = currentB;
        }
        finally
        {
            pair.Applying = false;
        }
    }

    private void VerifyRigidAttachment(LinkedPair pair, IntPtr leaderHwnd, IntPtr followerHwnd)
    {
        for (var pass = 0; pass < VerificationPasses; pass++)
        {
            if (!TryGetWindowGeometry(leaderHwnd, out _, out var leaderVisual) ||
                !TryGetWindowGeometry(followerHwnd, out var followerRaw, out var followerVisual))
            {
                return;
            }

            var desired = DesiredFollowerVisualOrigin(pair, leaderHwnd, leaderVisual, followerVisual);
            if (desired.Left == followerVisual.Left && desired.Top == followerVisual.Top)
            {
                return;
            }

            MoveWindowToVisualOrigin(followerHwnd, followerRaw, followerVisual, desired.Left, desired.Top);
            pair.SuppressSynthetic(followerHwnd, SyntheticMoveSuppression);
        }
    }

    private static VisualPoint DesiredFollowerVisualOrigin(
        LinkedPair pair,
        IntPtr leaderHwnd,
        NativeRect leaderVisual,
        NativeRect followerVisual)
    {
        if (leaderHwnd == pair.A)
        {
            return pair.Attachment switch
            {
                AttachmentEdge.ARightToBLeft => new VisualPoint(leaderVisual.Right, leaderVisual.Top + pair.AlongOffset),
                AttachmentEdge.ALeftToBRight => new VisualPoint(leaderVisual.Left - followerVisual.Width, leaderVisual.Top + pair.AlongOffset),
                AttachmentEdge.ABottomToBTop => new VisualPoint(leaderVisual.Left + pair.AlongOffset, leaderVisual.Bottom),
                AttachmentEdge.ATopToBBottom => new VisualPoint(leaderVisual.Left + pair.AlongOffset, leaderVisual.Top - followerVisual.Height),
                _ => new VisualPoint(followerVisual.Left, followerVisual.Top)
            };
        }

        return pair.Attachment switch
        {
            AttachmentEdge.ARightToBLeft => new VisualPoint(leaderVisual.Left - followerVisual.Width, leaderVisual.Top - pair.AlongOffset),
            AttachmentEdge.ALeftToBRight => new VisualPoint(leaderVisual.Right, leaderVisual.Top - pair.AlongOffset),
            AttachmentEdge.ABottomToBTop => new VisualPoint(leaderVisual.Left - pair.AlongOffset, leaderVisual.Top - followerVisual.Height),
            AttachmentEdge.ATopToBBottom => new VisualPoint(leaderVisual.Left - pair.AlongOffset, leaderVisual.Bottom),
            _ => new VisualPoint(followerVisual.Left, followerVisual.Top)
        };
    }

    private static void MoveWindowToVisualOrigin(
        IntPtr hwnd,
        NativeRect raw,
        NativeRect visual,
        int desiredVisualLeft,
        int desiredVisualTop)
    {
        var deltaX = desiredVisualLeft - visual.Left;
        var deltaY = desiredVisualTop - visual.Top;
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            raw.Left + deltaX,
            raw.Top + deltaY,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private AttachmentCandidate? FindAttachablePartnerCore(IntPtr hwnd)
    {
        if (!IsEligibleWindow(hwnd) || !TryGetVisualRect(hwnd, out var source))
        {
            return null;
        }

        var dpi = EffectiveDpi(hwnd);
        var edgeTolerance = ScaleForDpi(22, dpi);
        var minimumOverlap = ScaleForDpi(28, dpi);
        AttachmentCandidate? best = null;

        EnumWindows((candidateHwnd, _) =>
        {
            if (candidateHwnd == hwnd ||
                _byWindow.ContainsKey(candidateHwnd) ||
                !IsEligibleWindow(candidateHwnd) ||
                !TryGetVisualRect(candidateHwnd, out var candidateVisual))
            {
                return true;
            }

            if (!TryBestAttachment(source, candidateVisual, edgeTolerance, minimumOverlap, out var attachment, out var score))
            {
                return true;
            }

            if (best is null || score > best.Value.Score)
            {
                best = new AttachmentCandidate(candidateHwnd, attachment, score);
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static bool TryBestAttachment(
        NativeRect a,
        NativeRect b,
        int tolerance,
        int minimumOverlap,
        out AttachmentEdge attachment,
        out long bestScore)
    {
        attachment = default;
        bestScore = long.MinValue;

        var verticalOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (verticalOverlap >= minimumOverlap)
        {
            ScoreAttachment(Math.Abs(a.Right - b.Left), verticalOverlap, tolerance, AttachmentEdge.ARightToBLeft, ref attachment, ref bestScore);
            ScoreAttachment(Math.Abs(a.Left - b.Right), verticalOverlap, tolerance, AttachmentEdge.ALeftToBRight, ref attachment, ref bestScore);
        }

        var horizontalOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (horizontalOverlap >= minimumOverlap)
        {
            ScoreAttachment(Math.Abs(a.Bottom - b.Top), horizontalOverlap, tolerance, AttachmentEdge.ABottomToBTop, ref attachment, ref bestScore);
            ScoreAttachment(Math.Abs(a.Top - b.Bottom), horizontalOverlap, tolerance, AttachmentEdge.ATopToBBottom, ref attachment, ref bestScore);
        }

        return bestScore != long.MinValue;
    }

    private static void ScoreAttachment(
        int gap,
        int overlap,
        int tolerance,
        AttachmentEdge candidate,
        ref AttachmentEdge bestAttachment,
        ref long bestScore)
    {
        if (gap > tolerance)
        {
            return;
        }

        var score = (long)overlap * 10000L - gap * 100L;
        if (score > bestScore)
        {
            bestScore = score;
            bestAttachment = candidate;
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

        if ((GetWindowExStyle(hwnd) & WsExToolWindow) != 0 || IsWindowCloaked(hwnd))
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

    private static bool TryGetWindowGeometry(IntPtr hwnd, out NativeRect raw, out NativeRect visual)
    {
        raw = default;
        visual = default;
        if (!GetWindowRect(hwnd, out raw) || raw.Width <= 0 || raw.Height <= 0)
        {
            return false;
        }

        if (!TryGetVisualRect(hwnd, out visual))
        {
            visual = raw;
        }

        return visual.Width > 0 && visual.Height > 0;
    }

    private static bool TryGetVisualRect(IntPtr hwnd, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0 &&
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

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static long GetWindowExStyle(IntPtr hwnd)
        => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() : GetWindowLong(hwnd, GwlExStyle);

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    private static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    private void RemovePair(LinkedPair pair)
    {
        _byWindow.Remove(pair.A);
        _byWindow.Remove(pair.B);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
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

    private enum AttachmentEdge
    {
        ARightToBLeft,
        ALeftToBRight,
        ABottomToBTop,
        ATopToBBottom
    }

    private readonly record struct AttachmentCandidate(IntPtr Hwnd, AttachmentEdge Attachment, long Score);
    private readonly record struct VisualPoint(int Left, int Top);

    private sealed class LinkedPair
    {
        private IntPtr _syntheticHwnd;
        private DateTime _syntheticUntilUtc;
        private IntPtr _stabilizationLeader;
        private DateTime _stabilizationUntilUtc;

        public LinkedPair(IntPtr a, IntPtr b, AttachmentEdge attachment, int alongOffset, NativeRect lastA, NativeRect lastB)
        {
            A = a;
            B = b;
            Attachment = attachment;
            AlongOffset = alongOffset;
            LastA = lastA;
            LastB = lastB;
        }

        public IntPtr A { get; }
        public IntPtr B { get; }
        public AttachmentEdge Attachment { get; }
        public int AlongOffset { get; }
        public NativeRect LastA { get; set; }
        public NativeRect LastB { get; set; }
        public bool Applying { get; set; }
        public IntPtr ActiveLeader { get; set; }

        public IntPtr Other(IntPtr hwnd) => hwnd == A ? B : hwnd == B ? A : IntPtr.Zero;

        public void SuppressSynthetic(IntPtr hwnd, TimeSpan duration)
        {
            _syntheticHwnd = hwnd;
            _syntheticUntilUtc = DateTime.UtcNow + duration;
        }

        public bool IsSyntheticSuppressed(IntPtr hwnd)
            => hwnd == _syntheticHwnd && DateTime.UtcNow <= _syntheticUntilUtc;

        public void BeginStabilization(IntPtr leader, TimeSpan duration)
        {
            _stabilizationLeader = leader;
            _stabilizationUntilUtc = DateTime.UtcNow + duration;
        }

        public bool TryGetStabilizationLeader(out IntPtr leader)
        {
            if (_stabilizationLeader != IntPtr.Zero && DateTime.UtcNow <= _stabilizationUntilUtc)
            {
                leader = _stabilizationLeader;
                return true;
            }

            _stabilizationLeader = IntPtr.Zero;
            leader = IntPtr.Zero;
            return false;
        }
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
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

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
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
