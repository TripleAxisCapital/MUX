using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MUX.App.Services;

/// <summary>
/// Process-wide rigid window grouping for the caption-pill Link Windows control.
/// Clicking Link on any member links every currently touching window in that connected cluster.
/// A linked cluster translates as one rigid body and can magnetically snap to external windows
/// from any member edge without accumulating positional drift.
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
    private const int VerificationPasses = 3;

    private static readonly Lazy<ReliableWindowLinkService> SharedInstance = new(() => new ReliableWindowLinkService());
    private static readonly TimeSpan SyntheticSuppression = TimeSpan.FromMilliseconds(120);

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

        var existingMembers = _byWindow.TryGetValue(hwnd, out var existing)
            ? existing.Members
            : new HashSet<IntPtr> { hwnd };

        foreach (var candidate in EnumerateEligibleWindows())
        {
            if (existingMembers.Contains(candidate))
            {
                continue;
            }

            if (AnyTouching(existingMembers, candidate))
            {
                return candidate;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// If unlinked, link the entire connected cluster touching <paramref name="hwnd"/>.
    /// If already linked and a new external window is touching any member, absorb that window and
    /// its connected cluster. If there is nothing new to absorb, unlink the existing group.
    /// </summary>
    public bool ToggleLink(IntPtr hwnd)
    {
        if (_disposed || !IsEligibleWindow(hwnd))
        {
            return false;
        }

        var wasLinked = _byWindow.TryGetValue(hwnd, out var oldGroup);
        var connected = FindConnectedCluster(hwnd);

        if (wasLinked && oldGroup is not null)
        {
            var hasNewMembers = connected.Any(member => !oldGroup.Members.Contains(member));
            if (!hasNewMembers)
            {
                RemoveGroup(oldGroup);
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
        var allMembers = new HashSet<IntPtr>(requestedMembers.Where(IsEligibleWindow));
        foreach (var member in requestedMembers.ToArray())
        {
            if (_byWindow.TryGetValue(member, out var prior))
            {
                foreach (var priorMember in prior.Members)
                {
                    if (IsEligibleWindow(priorMember))
                    {
                        allMembers.Add(priorMember);
                    }
                }
            }
        }

        var previousGroups = allMembers
            .Select(member => _byWindow.TryGetValue(member, out var group) ? group : null)
            .Where(group => group is not null)
            .Distinct()
            .Cast<LinkedGroup>()
            .ToList();
        foreach (var group in previousGroups)
        {
            RemoveGroupMappings(group);
        }

        if (allMembers.Count < 2)
        {
            return;
        }

        var root = allMembers.Contains(preferredRoot) ? preferredRoot : allMembers.First();
        var linked = new LinkedGroup(allMembers, root);
        CaptureCurrentGeometry(linked, resetDrag: true);

        foreach (var member in linked.Members)
        {
            _byWindow[member] = linked;
        }
    }

    private HashSet<IntPtr> FindConnectedCluster(IntPtr hwnd)
    {
        var eligible = EnumerateEligibleWindows().ToHashSet();
        eligible.Add(hwnd);

        var cluster = new HashSet<IntPtr>();
        if (_byWindow.TryGetValue(hwnd, out var existing))
        {
            foreach (var member in existing.Members.Where(IsEligibleWindow))
            {
                cluster.Add(member);
                eligible.Add(member);
            }
        }
        else
        {
            cluster.Add(hwnd);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in eligible.ToArray())
            {
                if (cluster.Contains(candidate) || !IsEligibleWindow(candidate) || !AnyTouching(cluster, candidate))
                {
                    continue;
                }

                cluster.Add(candidate);
                changed = true;

                if (_byWindow.TryGetValue(candidate, out var candidateGroup))
                {
                    foreach (var member in candidateGroup.Members.Where(IsEligibleWindow))
                    {
                        if (cluster.Add(member))
                        {
                            eligible.Add(member);
                        }
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

        var tolerance = ScaleForDpi(18, EffectiveDpi(candidate));
        var minimumOverlap = ScaleForDpi(24, EffectiveDpi(candidate));
        foreach (var member in members)
        {
            if (member == candidate || !TryGetVisualRect(member, out var memberRect))
            {
                continue;
            }

            if (TryBestAttachment(memberRect, candidateRect, tolerance, minimumOverlap, out _, out _))
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
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => HandleEvent(eventType, hwnd)));
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

        if (eventType != EventObjectLocationChange || group.Applying)
        {
            return;
        }

        if (group.ActiveLeader != IntPtr.Zero)
        {
            if (hwnd == group.ActiveLeader && !group.IsSyntheticSuppressed(hwnd))
            {
                ApplyInteractiveTranslation(group, hwnd, finalPass: false);
            }
            return;
        }

        if (group.IsSyntheticSuppressed(hwnd))
        {
            return;
        }

        ApplyProgrammaticTranslation(group, hwnd);
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

    private void BeginInteractiveMove(LinkedGroup group, IntPtr leader)
    {
        if (!group.Members.Contains(leader) || !IsWindow(leader))
        {
            return;
        }

        group.ActiveLeader = leader;
        group.Resizing = false;
        group.HorizontalAnchor = null;
        group.VerticalAnchor = null;
        group.DragStartVisual.Clear();
        group.SnapCandidates.Clear();

        foreach (var member in group.Members.ToArray())
        {
            if (TryGetVisualRect(member, out var visual))
            {
                group.DragStartVisual[member] = visual;
                group.LastVisual[member] = visual;
            }
        }

        foreach (var candidate in EnumerateEligibleWindows())
        {
            if (!group.Members.Contains(candidate))
            {
                group.SnapCandidates.Add(candidate);
            }
        }
    }

    private void EndInteractiveMove(LinkedGroup group, IntPtr hwnd)
    {
        if (group.ActiveLeader == IntPtr.Zero || group.ActiveLeader != hwnd)
        {
            return;
        }

        if (!group.Resizing)
        {
            ApplyInteractiveTranslation(group, hwnd, finalPass: true);
        }

        group.ActiveLeader = IntPtr.Zero;
        group.Resizing = false;
        group.HorizontalAnchor = null;
        group.VerticalAnchor = null;
        CaptureCurrentGeometry(group, resetDrag: true);
    }

    private void ApplyInteractiveTranslation(LinkedGroup group, IntPtr leader, bool finalPass)
    {
        if (!group.DragStartVisual.TryGetValue(leader, out var startLeader) ||
            !TryGetWindowGeometry(leader, out var leaderRaw, out var leaderVisual))
        {
            return;
        }

        if (Math.Abs(leaderVisual.Width - startLeader.Width) > ResizeTolerancePx ||
            Math.Abs(leaderVisual.Height - startLeader.Height) > ResizeTolerancePx)
        {
            group.Resizing = true;
            group.LastVisual[leader] = leaderVisual;
            return;
        }

        var deltaX = leaderVisual.Left - startLeader.Left;
        var deltaY = leaderVisual.Top - startLeader.Top;

        if (GroupSnappingEnabled)
        {
            var snapX = ResolveHorizontalGroupSnap(group, deltaX, deltaY, finalPass);
            var snapY = ResolveVerticalGroupSnap(group, deltaX + snapX, deltaY, finalPass);
            if (snapX != 0 || snapY != 0)
            {
                group.Applying = true;
                try
                {
                    MoveWindowToVisualOrigin(
                        leader,
                        leaderRaw,
                        leaderVisual,
                        leaderVisual.Left + snapX,
                        leaderVisual.Top + snapY);
                    group.SuppressSynthetic(leader, SyntheticSuppression);
                }
                finally
                {
                    group.Applying = false;
                }

                deltaX += snapX;
                deltaY += snapY;
            }
        }

        TranslateFollowersFromSnapshot(group, leader, deltaX, deltaY, finalPass);
    }

    private void TranslateFollowersFromSnapshot(LinkedGroup group, IntPtr leader, int deltaX, int deltaY, bool finalPass)
    {
        group.Applying = true;
        try
        {
            foreach (var member in group.Members.ToArray())
            {
                if (member == leader || !group.DragStartVisual.TryGetValue(member, out var startVisual) ||
                    !TryGetWindowGeometry(member, out var raw, out var visual))
                {
                    continue;
                }

                MoveWindowToVisualOrigin(member, raw, visual, startVisual.Left + deltaX, startVisual.Top + deltaY);
                group.SuppressSynthetic(member, SyntheticSuppression);
            }

            if (finalPass)
            {
                VerifyRigidGroup(group, leader, deltaX, deltaY);
            }
        }
        finally
        {
            group.Applying = false;
        }

        UpdateLastVisuals(group);
    }

    private void VerifyRigidGroup(LinkedGroup group, IntPtr leader, int deltaX, int deltaY)
    {
        for (var pass = 0; pass < VerificationPasses; pass++)
        {
            var corrected = false;
            foreach (var member in group.Members.ToArray())
            {
                if (member == leader || !group.DragStartVisual.TryGetValue(member, out var startVisual) ||
                    !TryGetWindowGeometry(member, out var raw, out var visual))
                {
                    continue;
                }

                var desiredLeft = startVisual.Left + deltaX;
                var desiredTop = startVisual.Top + deltaY;
                if (visual.Left == desiredLeft && visual.Top == desiredTop)
                {
                    continue;
                }

                MoveWindowToVisualOrigin(member, raw, visual, desiredLeft, desiredTop);
                group.SuppressSynthetic(member, SyntheticSuppression);
                corrected = true;
            }

            if (!corrected)
            {
                return;
            }
        }
    }

    private void ApplyProgrammaticTranslation(LinkedGroup group, IntPtr changed)
    {
        if (!group.LastVisual.TryGetValue(changed, out var previous) ||
            !TryGetVisualRect(changed, out var current))
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

        var deltaX = current.Left - previous.Left;
        var deltaY = current.Top - previous.Top;
        if (deltaX == 0 && deltaY == 0)
        {
            group.LastVisual[changed] = current;
            return;
        }

        group.Applying = true;
        try
        {
            foreach (var member in group.Members.ToArray())
            {
                if (member == changed || !group.LastVisual.TryGetValue(member, out var memberPrevious) ||
                    !TryGetWindowGeometry(member, out var raw, out var visual))
                {
                    continue;
                }

                MoveWindowToVisualOrigin(member, raw, visual, memberPrevious.Left + deltaX, memberPrevious.Top + deltaY);
                group.SuppressSynthetic(member, SyntheticSuppression);
            }
        }
        finally
        {
            group.Applying = false;
        }

        UpdateLastVisuals(group);
    }

    private int ResolveHorizontalGroupSnap(LinkedGroup group, int deltaX, int deltaY, bool finalPass)
    {
        var releaseThreshold = ScaleForDpi(34, EffectiveDpi(group.ActiveLeader));
        if (group.HorizontalAnchor is GroupSnapAnchor locked &&
            TryAnchorCorrection(group, locked, deltaX, deltaY, out var lockedCorrection) &&
            Math.Abs(lockedCorrection) <= releaseThreshold)
        {
            return lockedCorrection;
        }

        group.HorizontalAnchor = null;
        var threshold = ScaleForDpi(finalPass ? 18 : 14, EffectiveDpi(group.ActiveLeader));
        var bestDistance = threshold + 1;
        GroupSnapAnchor? best = null;
        var bestCorrection = 0;

        foreach (var member in group.Members)
        {
            if (!group.DragStartVisual.TryGetValue(member, out var start))
            {
                continue;
            }

            var moving = Translate(start, deltaX, deltaY);
            foreach (var candidate in group.SnapCandidates.ToArray())
            {
                if (!IsEligibleWindow(candidate) || group.Members.Contains(candidate) || !TryGetVisualRect(candidate, out var target) ||
                    !SpansNear(moving.Top, moving.Bottom, target.Top, target.Bottom, threshold))
                {
                    continue;
                }

                EvaluateHorizontal(member, candidate, moving, target, MovingEdge.Near, CandidateEdge.Near, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateHorizontal(member, candidate, moving, target, MovingEdge.Near, CandidateEdge.Far, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateHorizontal(member, candidate, moving, target, MovingEdge.Far, CandidateEdge.Near, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateHorizontal(member, candidate, moving, target, MovingEdge.Far, CandidateEdge.Far, threshold, ref bestDistance, ref best, ref bestCorrection);
            }
        }

        group.HorizontalAnchor = best;
        return bestCorrection;
    }

    private int ResolveVerticalGroupSnap(LinkedGroup group, int deltaX, int deltaY, bool finalPass)
    {
        var releaseThreshold = ScaleForDpi(34, EffectiveDpi(group.ActiveLeader));
        if (group.VerticalAnchor is GroupSnapAnchor locked &&
            TryAnchorCorrection(group, locked, deltaX, deltaY, out var lockedCorrection) &&
            Math.Abs(lockedCorrection) <= releaseThreshold)
        {
            return lockedCorrection;
        }

        group.VerticalAnchor = null;
        var threshold = ScaleForDpi(finalPass ? 18 : 14, EffectiveDpi(group.ActiveLeader));
        var bestDistance = threshold + 1;
        GroupSnapAnchor? best = null;
        var bestCorrection = 0;

        foreach (var member in group.Members)
        {
            if (!group.DragStartVisual.TryGetValue(member, out var start))
            {
                continue;
            }

            var moving = Translate(start, deltaX, deltaY);
            foreach (var candidate in group.SnapCandidates.ToArray())
            {
                if (!IsEligibleWindow(candidate) || group.Members.Contains(candidate) || !TryGetVisualRect(candidate, out var target) ||
                    !SpansNear(moving.Left, moving.Right, target.Left, target.Right, threshold))
                {
                    continue;
                }

                EvaluateVertical(member, candidate, moving, target, MovingEdge.Near, CandidateEdge.Near, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateVertical(member, candidate, moving, target, MovingEdge.Near, CandidateEdge.Far, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateVertical(member, candidate, moving, target, MovingEdge.Far, CandidateEdge.Near, threshold, ref bestDistance, ref best, ref bestCorrection);
                EvaluateVertical(member, candidate, moving, target, MovingEdge.Far, CandidateEdge.Far, threshold, ref bestDistance, ref best, ref bestCorrection);
            }
        }

        group.VerticalAnchor = best;
        return bestCorrection;
    }

    private static void EvaluateHorizontal(IntPtr member, IntPtr candidate, NativeRect moving, NativeRect target, MovingEdge movingEdge, CandidateEdge candidateEdge, int threshold, ref int bestDistance, ref GroupSnapAnchor? best, ref int bestCorrection)
    {
        var targetEdge = candidateEdge == CandidateEdge.Near ? target.Left : target.Right;
        var movingEdgePosition = movingEdge == MovingEdge.Near ? moving.Left : moving.Right;
        var correction = targetEdge - movingEdgePosition;
        var distance = Math.Abs(correction);
        if (distance > threshold)
        {
            return;
        }

        var anchor = new GroupSnapAnchor(member, candidate, SnapAxis.Horizontal, movingEdge, candidateEdge);
        var beatsTie = distance == bestDistance && best is GroupSnapAnchor existing && IsAbutment(anchor) && !IsAbutment(existing);
        if (distance < bestDistance || beatsTie)
        {
            bestDistance = distance;
            best = anchor;
            bestCorrection = correction;
        }
    }

    private static void EvaluateVertical(IntPtr member, IntPtr candidate, NativeRect moving, NativeRect target, MovingEdge movingEdge, CandidateEdge candidateEdge, int threshold, ref int bestDistance, ref GroupSnapAnchor? best, ref int bestCorrection)
    {
        var targetEdge = candidateEdge == CandidateEdge.Near ? target.Top : target.Bottom;
        var movingEdgePosition = movingEdge == MovingEdge.Near ? moving.Top : moving.Bottom;
        var correction = targetEdge - movingEdgePosition;
        var distance = Math.Abs(correction);
        if (distance > threshold)
        {
            return;
        }

        var anchor = new GroupSnapAnchor(member, candidate, SnapAxis.Vertical, movingEdge, candidateEdge);
        var beatsTie = distance == bestDistance && best is GroupSnapAnchor existing && IsAbutment(anchor) && !IsAbutment(existing);
        if (distance < bestDistance || beatsTie)
        {
            bestDistance = distance;
            best = anchor;
            bestCorrection = correction;
        }
    }

    private static bool TryAnchorCorrection(LinkedGroup group, GroupSnapAnchor anchor, int deltaX, int deltaY, out int correction)
    {
        correction = 0;
        if (!group.DragStartVisual.TryGetValue(anchor.Member, out var start) ||
            !IsEligibleWindow(anchor.Candidate) ||
            !TryGetVisualRect(anchor.Candidate, out var target))
        {
            return false;
        }

        var moving = Translate(start, deltaX, deltaY);
        if (anchor.Axis == SnapAxis.Horizontal)
        {
            var targetEdge = anchor.CandidateEdge == CandidateEdge.Near ? target.Left : target.Right;
            var movingEdge = anchor.MovingEdge == MovingEdge.Near ? moving.Left : moving.Right;
            correction = targetEdge - movingEdge;
        }
        else
        {
            var targetEdge = anchor.CandidateEdge == CandidateEdge.Near ? target.Top : target.Bottom;
            var movingEdge = anchor.MovingEdge == MovingEdge.Near ? moving.Top : moving.Bottom;
            correction = targetEdge - movingEdge;
        }

        return true;
    }

    private static bool IsAbutment(GroupSnapAnchor anchor)
        => anchor.MovingEdge != (MovingEdge)anchor.CandidateEdge;

    private static bool SpansNear(int aStart, int aEnd, int bStart, int bEnd, int tolerance)
        => aStart < bEnd + tolerance && aEnd > bStart - tolerance;

    private static NativeRect Translate(NativeRect rect, int dx, int dy)
        => new()
        {
            Left = rect.Left + dx,
            Top = rect.Top + dy,
            Right = rect.Right + dx,
            Bottom = rect.Bottom + dy
        };

    private static void MoveWindowToVisualOrigin(IntPtr hwnd, NativeRect raw, NativeRect visual, int desiredVisualLeft, int desiredVisualTop)
    {
        var deltaX = desiredVisualLeft - visual.Left;
        var deltaY = desiredVisualTop - visual.Top;
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, raw.Left + deltaX, raw.Top + deltaY, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static bool TryBestAttachment(NativeRect a, NativeRect b, int tolerance, int minimumOverlap, out AttachmentEdge attachment, out long bestScore)
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

    private static void ScoreAttachment(int gap, int overlap, int tolerance, AttachmentEdge candidate, ref AttachmentEdge bestAttachment, ref long bestScore)
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

    private static List<IntPtr> EnumerateEligibleWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsEligibleWindow(hwnd))
            {
                windows.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
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
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0 && rect.Width > 0 && rect.Height > 0)
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
        if (hwnd == IntPtr.Zero)
        {
            return 96;
        }
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : Math.Max(96u, dpi);
    }

    private static int ScaleForDpi(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96.0));

    private static void CaptureCurrentGeometry(LinkedGroup group, bool resetDrag)
    {
        group.LastVisual.Clear();
        foreach (var member in group.Members.ToArray())
        {
            if (TryGetVisualRect(member, out var visual))
            {
                group.LastVisual[member] = visual;
            }
        }

        if (resetDrag)
        {
            group.DragStartVisual.Clear();
            group.SnapCandidates.Clear();
            group.ActiveLeader = IntPtr.Zero;
            group.HorizontalAnchor = null;
            group.VerticalAnchor = null;
        }
    }

    private static void UpdateLastVisuals(LinkedGroup group)
    {
        foreach (var member in group.Members.ToArray())
        {
            if (TryGetVisualRect(member, out var visual))
            {
                group.LastVisual[member] = visual;
            }
        }
    }

    private void PruneInvalidMembers(LinkedGroup group)
    {
        foreach (var member in group.Members.Where(member => !IsWindow(member)).ToArray())
        {
            _byWindow.Remove(member);
            group.Members.Remove(member);
            group.LastVisual.Remove(member);
            group.DragStartVisual.Remove(member);
        }
    }

    private void RemoveGroup(LinkedGroup group)
    {
        RemoveGroupMappings(group);
        group.Members.Clear();
        group.LastVisual.Clear();
        group.DragStartVisual.Clear();
        group.SnapCandidates.Clear();
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
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }
        _hooks.Clear();
        _byWindow.Clear();
    }

    private enum AttachmentEdge { ARightToBLeft, ALeftToBRight, ABottomToBTop, ATopToBBottom }
    private enum SnapAxis { Horizontal, Vertical }
    private enum MovingEdge { Near = 0, Far = 1 }
    private enum CandidateEdge { Near = 0, Far = 1 }

    private readonly record struct GroupSnapAnchor(IntPtr Member, IntPtr Candidate, SnapAxis Axis, MovingEdge MovingEdge, CandidateEdge CandidateEdge);

    private sealed class LinkedGroup
    {
        private readonly Dictionary<IntPtr, DateTime> _syntheticUntil = new();

        public LinkedGroup(IEnumerable<IntPtr> members, IntPtr root)
        {
            Members = new HashSet<IntPtr>(members);
            Root = root;
        }

        public HashSet<IntPtr> Members { get; }
        public IntPtr Root { get; }
        public Dictionary<IntPtr, NativeRect> LastVisual { get; } = new();
        public Dictionary<IntPtr, NativeRect> DragStartVisual { get; } = new();
        public List<IntPtr> SnapCandidates { get; } = new();
        public IntPtr ActiveLeader { get; set; }
        public bool Applying { get; set; }
        public bool Resizing { get; set; }
        public GroupSnapAnchor? HorizontalAnchor { get; set; }
        public GroupSnapAnchor? VerticalAnchor { get; set; }

        public void SuppressSynthetic(IntPtr hwnd, TimeSpan duration)
            => _syntheticUntil[hwnd] = DateTime.UtcNow + duration;

        public bool IsSyntheticSuppressed(IntPtr hwnd)
        {
            if (!_syntheticUntil.TryGetValue(hwnd, out var until))
            {
                return false;
            }
            if (DateTime.UtcNow <= until)
            {
                return true;
            }
            _syntheticUntil.Remove(hwnd);
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

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);
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
