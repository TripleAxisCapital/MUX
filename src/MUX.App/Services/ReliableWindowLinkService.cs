using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Process-wide rigid window grouping for the caption-pill Link Windows control.
/// Movement is coalesced to the render cadence and follower windows are repositioned in one
/// DeferWindowPos transaction from an immutable drag snapshot. That prevents the feedback loop,
/// incremental rounding drift, and one-window-at-a-time redraw that made linked groups jiggle.
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
    private const int ResizeTolerancePx = 2;

    private static readonly Lazy<ReliableWindowLinkService> SharedInstance = new(() => new ReliableWindowLinkService());
    private static readonly TimeSpan SyntheticSuppression = TimeSpan.FromMilliseconds(90);

    private readonly Dictionary<IntPtr, LinkedGroup> _byWindow = new();
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

    public static ReliableWindowLinkService Shared => SharedInstance.Value;

    public event EventHandler? Changed;

    public bool GroupSnappingEnabled { get; set; } = true;

    public bool IsLinked(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _byWindow.TryGetValue(hwnd, out var group) && group.Members.Count > 1;

    public int GetGroupSize(IntPtr hwnd)
        => _byWindow.TryGetValue(hwnd, out var group) ? group.Members.Count : 0;

    public IReadOnlyList<IntPtr> GetGroupMembers(IntPtr hwnd)
        => _byWindow.TryGetValue(hwnd, out var group)
            ? group.Members.ToArray()
            : hwnd == IntPtr.Zero ? Array.Empty<IntPtr>() : new[] { hwnd };

    public IntPtr GetPartner(IntPtr hwnd)
        => _byWindow.TryGetValue(hwnd, out var group)
            ? group.Members.FirstOrDefault(member => member != hwnd)
            : IntPtr.Zero;

    public IntPtr FindAttachablePartner(IntPtr hwnd)
    {
        if (_disposed || !IsEligibleWindow(hwnd))
        {
            return IntPtr.Zero;
        }

        var members = _byWindow.TryGetValue(hwnd, out var group)
            ? group.Members
            : new HashSet<IntPtr> { hwnd };

        foreach (var candidate in EnumerateEligibleWindows())
        {
            if (!members.Contains(candidate) && AnyTouching(members, candidate))
            {
                return candidate;
            }
        }

        return IntPtr.Zero;
    }

    public bool ToggleLink(IntPtr hwnd)
    {
        if (_disposed || !IsEligibleWindow(hwnd))
        {
            return false;
        }

        var wasLinked = _byWindow.TryGetValue(hwnd, out var existing);
        var connected = FindConnectedCluster(hwnd);

        if (wasLinked && existing is not null)
        {
            var hasNewMember = connected.Any(member => !existing.Members.Contains(member));
            if (!hasNewMember)
            {
                RemoveGroup(existing);
                RaiseChanged();
                return false;
            }
        }

        if (connected.Count < 2)
        {
            return false;
        }

        CreateOrReplaceGroup(connected, hwnd);
        RaiseChanged();
        return true;
    }

    private void CreateOrReplaceGroup(HashSet<IntPtr> requestedMembers, IntPtr preferredRoot)
    {
        var members = new HashSet<IntPtr>(requestedMembers.Where(IsEligibleWindow));
        foreach (var member in requestedMembers.ToArray())
        {
            if (!_byWindow.TryGetValue(member, out var prior))
            {
                continue;
            }

            foreach (var priorMember in prior.Members.Where(IsEligibleWindow))
            {
                members.Add(priorMember);
            }
        }

        foreach (var old in members
                     .Select(member => _byWindow.TryGetValue(member, out var group) ? group : null)
                     .Where(group => group is not null)
                     .Distinct()
                     .Cast<LinkedGroup>()
                     .ToArray())
        {
            RemoveGroupMappings(old);
        }

        if (members.Count < 2)
        {
            return;
        }

        var root = members.Contains(preferredRoot) ? preferredRoot : members.First();
        var linked = new LinkedGroup(members, root);
        CaptureCurrentGeometry(linked, resetDrag: true);
        foreach (var member in linked.Members)
        {
            _byWindow[member] = linked;
        }
    }

    private HashSet<IntPtr> FindConnectedCluster(IntPtr hwnd)
    {
        var candidates = EnumerateEligibleWindows().ToHashSet();
        candidates.Add(hwnd);

        var cluster = new HashSet<IntPtr>();
        if (_byWindow.TryGetValue(hwnd, out var existing))
        {
            foreach (var member in existing.Members.Where(IsEligibleWindow))
            {
                cluster.Add(member);
                candidates.Add(member);
            }
        }
        else
        {
            cluster.Add(hwnd);
        }

        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var candidate in candidates.ToArray())
            {
                if (cluster.Contains(candidate) || !IsEligibleWindow(candidate) || !AnyTouching(cluster, candidate))
                {
                    continue;
                }

                cluster.Add(candidate);
                grew = true;
                if (_byWindow.TryGetValue(candidate, out var candidateGroup))
                {
                    foreach (var member in candidateGroup.Members.Where(IsEligibleWindow))
                    {
                        cluster.Add(member);
                        candidates.Add(member);
                    }
                }
            }
        }

        return cluster;
    }

    private static bool AnyTouching(IEnumerable<IntPtr> members, IntPtr candidate)
    {
        if (!TryGetVisualRect(candidate, out var candidateRect))
        {
            return false;
        }

        var dpi = EffectiveDpi(candidate);
        var tolerance = ScaleForDpi(18, dpi);
        var minimumOverlap = ScaleForDpi(24, dpi);
        foreach (var member in members)
        {
            if (member == candidate || !TryGetVisualRect(member, out var rect))
            {
                continue;
            }

            if (RectsTouch(rect, candidateRect, tolerance, minimumOverlap))
            {
                return true;
            }
        }

        return false;
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
            // Do not queue every LOCATIONCHANGE at Send priority. Coalescing on the UI dispatcher is
            // the important part of keeping the group visually rigid during high-rate mouse input.
            _dispatcher.BeginInvoke(
                eventType == EventObjectLocationChange ? DispatcherPriority.Input : DispatcherPriority.Send,
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
            HandleDestroyedWindow(hwnd);
            return;
        }

        if (!_byWindow.TryGetValue(hwnd, out var group))
        {
            return;
        }

        PruneInvalidMembers(group);
        if (group.Members.Count < 2)
        {
            RemoveGroup(group);
            RaiseChanged();
            return;
        }

        if (eventType == EventSystemMoveSizeStart)
        {
            BeginInteractiveMove(group, hwnd);
            return;
        }

        if (eventType == EventSystemMoveSizeEnd)
        {
            EndInteractiveMove(group, hwnd);
            return;
        }

        if (eventType != EventObjectLocationChange || group.Applying || group.IsSyntheticSuppressed(hwnd))
        {
            return;
        }

        if (group.ActiveLeader != IntPtr.Zero)
        {
            if (hwnd == group.ActiveLeader)
            {
                ScheduleInteractiveSync(group);
            }
            return;
        }

        ScheduleProgrammaticSync(group, hwnd);
    }

    private void BeginInteractiveMove(LinkedGroup group, IntPtr leader)
    {
        if (!group.Members.Contains(leader) || !IsEligibleWindow(leader))
        {
            return;
        }

        group.ActiveLeader = leader;
        group.Resizing = false;
        group.PendingProgrammaticSource = IntPtr.Zero;
        group.DragStartVisual.Clear();
        foreach (var member in group.Members.ToArray())
        {
            if (TryGetVisualRect(member, out var rect))
            {
                group.DragStartVisual[member] = rect;
                group.LastVisual[member] = rect;
            }
        }
    }

    private void EndInteractiveMove(LinkedGroup group, IntPtr hwnd)
    {
        if (group.ActiveLeader == IntPtr.Zero || group.ActiveLeader != hwnd)
        {
            return;
        }

        // Flush the newest leader geometry synchronously at mouse-up instead of waiting for any
        // already queued intermediate WinEvent. This is what makes the group land as one object.
        ApplyInteractiveSnapshot(group, finalPass: true);
        group.ActiveLeader = IntPtr.Zero;
        group.Resizing = false;
        group.InteractiveSyncScheduled = false;
        CaptureCurrentGeometry(group, resetDrag: true);
    }

    private void ScheduleInteractiveSync(LinkedGroup group)
    {
        if (group.InteractiveSyncScheduled)
        {
            return;
        }

        group.InteractiveSyncScheduled = true;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                group.InteractiveSyncScheduled = false;
                if (_disposed || group.ActiveLeader == IntPtr.Zero || group.Applying)
                {
                    return;
                }

                ApplyInteractiveSnapshot(group, finalPass: false);
            }));
        }
        catch
        {
            group.InteractiveSyncScheduled = false;
        }
    }

    private void ApplyInteractiveSnapshot(LinkedGroup group, bool finalPass)
    {
        var leader = group.ActiveLeader;
        if (leader == IntPtr.Zero ||
            !group.DragStartVisual.TryGetValue(leader, out var leaderStart) ||
            !TryGetVisualRect(leader, out var leaderNow))
        {
            return;
        }

        if (Math.Abs(leaderNow.Width - leaderStart.Width) > ResizeTolerancePx ||
            Math.Abs(leaderNow.Height - leaderStart.Height) > ResizeTolerancePx)
        {
            group.Resizing = true;
            group.LastVisual[leader] = leaderNow;
            return;
        }

        var deltaX = leaderNow.Left - leaderStart.Left;
        var deltaY = leaderNow.Top - leaderStart.Top;

        if (finalPass && GroupSnappingEnabled)
        {
            var snap = ResolveFinalSnap(group, deltaX, deltaY);
            deltaX += snap.X;
            deltaY += snap.Y;
        }

        ApplySnapshotPositions(group, deltaX, deltaY, includeLeader: finalPass && (deltaX != leaderNow.Left - leaderStart.Left || deltaY != leaderNow.Top - leaderStart.Top));
    }

    private void ApplySnapshotPositions(LinkedGroup group, int deltaX, int deltaY, bool includeLeader)
    {
        var moves = new List<WindowMove>();
        foreach (var member in group.Members.ToArray())
        {
            if ((!includeLeader && member == group.ActiveLeader) ||
                !group.DragStartVisual.TryGetValue(member, out var start) ||
                !TryGetWindowGeometry(member, out var raw, out var visual))
            {
                continue;
            }

            moves.Add(WindowMove.FromVisualDestination(
                member,
                raw,
                visual,
                start.Left + deltaX,
                start.Top + deltaY));
        }

        ApplyMoves(group, moves);
    }

    private void ScheduleProgrammaticSync(LinkedGroup group, IntPtr changed)
    {
        group.PendingProgrammaticSource = changed;
        if (group.ProgrammaticSyncScheduled)
        {
            return;
        }

        group.ProgrammaticSyncScheduled = true;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                group.ProgrammaticSyncScheduled = false;
                var source = group.PendingProgrammaticSource;
                group.PendingProgrammaticSource = IntPtr.Zero;
                if (_disposed || source == IntPtr.Zero || group.ActiveLeader != IntPtr.Zero || group.Applying)
                {
                    return;
                }

                ApplyProgrammaticTranslation(group, source);
            }));
        }
        catch
        {
            group.ProgrammaticSyncScheduled = false;
        }
    }

    private void ApplyProgrammaticTranslation(LinkedGroup group, IntPtr changed)
    {
        if (!group.LastVisual.TryGetValue(changed, out var previous) || !TryGetVisualRect(changed, out var current))
        {
            CaptureCurrentGeometry(group, resetDrag: true);
            return;
        }

        if (Math.Abs(current.Width - previous.Width) > ResizeTolerancePx ||
            Math.Abs(current.Height - previous.Height) > ResizeTolerancePx)
        {
            CaptureCurrentGeometry(group, resetDrag: true);
            return;
        }

        var dx = current.Left - previous.Left;
        var dy = current.Top - previous.Top;
        if (dx == 0 && dy == 0)
        {
            group.LastVisual[changed] = current;
            return;
        }

        var moves = new List<WindowMove>();
        foreach (var member in group.Members.ToArray())
        {
            if (member == changed || !TryGetWindowGeometry(member, out var raw, out var visual))
            {
                continue;
            }

            moves.Add(WindowMove.FromVisualDestination(member, raw, visual, visual.Left + dx, visual.Top + dy));
        }

        ApplyMoves(group, moves);
        CaptureCurrentGeometry(group, resetDrag: true);
    }

    private void ApplyMoves(LinkedGroup group, IReadOnlyList<WindowMove> moves)
    {
        if (moves.Count == 0)
        {
            UpdateLastVisuals(group);
            return;
        }

        group.Applying = true;
        try
        {
            var defer = BeginDeferWindowPos(moves.Count);
            var deferredOk = defer != IntPtr.Zero;
            if (deferredOk)
            {
                foreach (var move in moves)
                {
                    defer = DeferWindowPos(
                        defer,
                        move.Hwnd,
                        IntPtr.Zero,
                        move.RawLeft,
                        move.RawTop,
                        0,
                        0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                    if (defer == IntPtr.Zero)
                    {
                        deferredOk = false;
                        break;
                    }
                }
            }

            if (deferredOk)
            {
                _ = EndDeferWindowPos(defer);
            }
            else
            {
                // Rare fallback when Windows cannot allocate a defer transaction.
                foreach (var move in moves)
                {
                    _ = SetWindowPos(
                        move.Hwnd,
                        IntPtr.Zero,
                        move.RawLeft,
                        move.RawTop,
                        0,
                        0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                }
            }

            foreach (var move in moves)
            {
                group.SuppressSynthetic(move.Hwnd, SyntheticSuppression);
            }
        }
        finally
        {
            group.Applying = false;
        }

        UpdateLastVisuals(group);
    }

    private NativePoint ResolveFinalSnap(LinkedGroup group, int deltaX, int deltaY)
    {
        var snapshots = group.DragStartVisual.Values.ToArray();
        if (snapshots.Length == 0)
        {
            return default;
        }

        var groupRect = new NativeRect(
            snapshots.Min(rect => rect.Left) + deltaX,
            snapshots.Min(rect => rect.Top) + deltaY,
            snapshots.Max(rect => rect.Right) + deltaX,
            snapshots.Max(rect => rect.Bottom) + deltaY);

        var snapDistance = ScaleForDpi(14, EffectiveDpi(group.ActiveLeader));
        int? bestX = null;
        int? bestY = null;

        foreach (var candidate in EnumerateEligibleWindows())
        {
            if (group.Members.Contains(candidate) || !TryGetVisualRect(candidate, out var rect))
            {
                continue;
            }

            var verticalOverlap = Overlap(groupRect.Top, groupRect.Bottom, rect.Top, rect.Bottom);
            var horizontalOverlap = Overlap(groupRect.Left, groupRect.Right, rect.Left, rect.Right);

            if (verticalOverlap > 0)
            {
                ConsiderSnap(ref bestX, rect.Left - groupRect.Right, snapDistance);
                ConsiderSnap(ref bestX, rect.Right - groupRect.Left, snapDistance);
            }

            if (horizontalOverlap > 0)
            {
                ConsiderSnap(ref bestY, rect.Top - groupRect.Bottom, snapDistance);
                ConsiderSnap(ref bestY, rect.Bottom - groupRect.Top, snapDistance);
            }
        }

        return new NativePoint { X = bestX ?? 0, Y = bestY ?? 0 };
    }

    private static void ConsiderSnap(ref int? best, int delta, int threshold)
    {
        if (Math.Abs(delta) > threshold)
        {
            return;
        }

        if (!best.HasValue || Math.Abs(delta) < Math.Abs(best.Value))
        {
            best = delta;
        }
    }

    private void HandleDestroyedWindow(IntPtr hwnd)
    {
        if (!_byWindow.TryGetValue(hwnd, out var group))
        {
            return;
        }

        _byWindow.Remove(hwnd);
        group.Members.Remove(hwnd);
        group.LastVisual.Remove(hwnd);
        group.DragStartVisual.Remove(hwnd);
        group.SyntheticUntil.Remove(hwnd);

        if (group.Members.Count < 2)
        {
            RemoveGroup(group);
        }
        else
        {
            CaptureCurrentGeometry(group, resetDrag: true);
        }
        RaiseChanged();
    }

    private void PruneInvalidMembers(LinkedGroup group)
    {
        foreach (var member in group.Members.Where(member => !IsEligibleWindow(member)).ToArray())
        {
            group.Members.Remove(member);
            _byWindow.Remove(member);
            group.LastVisual.Remove(member);
            group.DragStartVisual.Remove(member);
            group.SyntheticUntil.Remove(member);
        }
    }

    private void CaptureCurrentGeometry(LinkedGroup group, bool resetDrag)
    {
        group.LastVisual.Clear();
        if (resetDrag)
        {
            group.DragStartVisual.Clear();
        }

        foreach (var member in group.Members.ToArray())
        {
            if (!TryGetVisualRect(member, out var rect))
            {
                continue;
            }

            group.LastVisual[member] = rect;
            if (resetDrag)
            {
                group.DragStartVisual[member] = rect;
            }
        }
    }

    private static void UpdateLastVisuals(LinkedGroup group)
    {
        foreach (var member in group.Members.ToArray())
        {
            if (TryGetVisualRect(member, out var rect))
            {
                group.LastVisual[member] = rect;
            }
        }
    }

    private void RemoveGroup(LinkedGroup group)
    {
        RemoveGroupMappings(group);
        group.Members.Clear();
        group.LastVisual.Clear();
        group.DragStartVisual.Clear();
        group.SyntheticUntil.Clear();
    }

    private void RemoveGroupMappings(LinkedGroup group)
    {
        foreach (var member in group.Members.ToArray())
        {
            if (_byWindow.TryGetValue(member, out var mapped) && ReferenceEquals(mapped, group))
            {
                _byWindow.Remove(member);
            }
        }
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
            try { UnhookWinEvent(hook); } catch { }
        }
        _hooks.Clear();
        _byWindow.Clear();
    }

    private static IEnumerable<IntPtr> EnumerateEligibleWindows()
    {
        var result = new List<IntPtr>();
        _ = EnumWindows((hwnd, _) =>
        {
            if (IsEligibleWindow(hwnd))
            {
                result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsEligibleWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
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

        if (!TryGetVisualRect(hwnd, out var rect) || rect.Width < 80 || rect.Height < 60)
        {
            return false;
        }

        var title = new StringBuilder(256);
        _ = GetWindowText(hwnd, title, title.Capacity);
        return title.Length > 0;
    }

    private static bool RectsTouch(NativeRect a, NativeRect b, int tolerance, int minimumOverlap)
    {
        var horizontalOverlap = Overlap(a.Left, a.Right, b.Left, b.Right);
        var verticalOverlap = Overlap(a.Top, a.Bottom, b.Top, b.Bottom);

        var verticalEdgeTouch = verticalOverlap >= minimumOverlap &&
                                (Math.Abs(a.Right - b.Left) <= tolerance || Math.Abs(b.Right - a.Left) <= tolerance);
        var horizontalEdgeTouch = horizontalOverlap >= minimumOverlap &&
                                  (Math.Abs(a.Bottom - b.Top) <= tolerance || Math.Abs(b.Bottom - a.Top) <= tolerance);
        return verticalEdgeTouch || horizontalEdgeTouch;
    }

    private static int Overlap(int a0, int a1, int b0, int b1)
        => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    private static bool TryGetWindowGeometry(IntPtr hwnd, out NativeRect raw, out NativeRect visual)
    {
        visual = default;
        return GetWindowRect(hwnd, out raw) && raw.Width > 0 && raw.Height > 0 && TryGetVisualRect(hwnd, out visual);
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
        var value = 0;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out value, sizeof(int)) == 0 && value != 0;
    }

    private static uint EffectiveDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    private static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    private sealed class LinkedGroup
    {
        public LinkedGroup(HashSet<IntPtr> members, IntPtr root)
        {
            Members = members;
            Root = root;
        }

        public HashSet<IntPtr> Members { get; }
        public IntPtr Root { get; }
        public Dictionary<IntPtr, NativeRect> LastVisual { get; } = new();
        public Dictionary<IntPtr, NativeRect> DragStartVisual { get; } = new();
        public Dictionary<IntPtr, DateTime> SyntheticUntil { get; } = new();
        public IntPtr ActiveLeader { get; set; }
        public IntPtr PendingProgrammaticSource { get; set; }
        public bool Applying { get; set; }
        public bool Resizing { get; set; }
        public bool InteractiveSyncScheduled { get; set; }
        public bool ProgrammaticSyncScheduled { get; set; }

        public void SuppressSynthetic(IntPtr hwnd, TimeSpan duration)
            => SyntheticUntil[hwnd] = DateTime.UtcNow + duration;

        public bool IsSyntheticSuppressed(IntPtr hwnd)
            => SyntheticUntil.TryGetValue(hwnd, out var until) && DateTime.UtcNow < until;
    }

    private readonly record struct WindowMove(IntPtr Hwnd, int RawLeft, int RawTop)
    {
        public static WindowMove FromVisualDestination(
            IntPtr hwnd,
            NativeRect raw,
            NativeRect visual,
            int desiredVisualLeft,
            int desiredVisualTop)
        {
            var frameOffsetX = raw.Left - visual.Left;
            var frameOffsetY = raw.Top - visual.Top;
            return new WindowMove(hwnd, desiredVisualLeft + frameOffsetX, desiredVisualTop + frameOffsetY);
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

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

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
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int count);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr defer, IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(IntPtr defer);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
