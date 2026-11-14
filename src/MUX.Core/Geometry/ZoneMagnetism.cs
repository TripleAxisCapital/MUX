using MUX.Core.Models;

namespace MUX.Core.Geometry;

/// <summary>
/// Pure pixel-accurate magnetic alignment of MUX regions on the physical display.
/// No state is persisted or window position changed by these calculations.
/// </summary>
public static class ZoneMagnetism
{
    public const int DefaultThresholdPx = 16;

    public static PixelRect SnapMoved(
        DisplayProfile display, LayoutProfile layout, Guid movingId,
        PixelRect proposed, int thresholdPx = DefaultThresholdPx)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(layout);
        if (display.WidthPx <= 0 || display.HeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(display));

        var width = Math.Clamp(proposed.Width, 1, display.WidthPx);
        var height = Math.Clamp(proposed.Height, 1, display.HeightPx);
        var rect = new PixelRect(
            Math.Clamp(proposed.Left, 0, display.WidthPx - width),
            Math.Clamp(proposed.Top, 0, display.HeightPx - height), width, height);
        var others = Neighbors(display, layout, movingId);

        var dx = MoveCorrection(rect, others, display.WidthPx, true, thresholdPx);
        rect = new PixelRect(rect.Left + dx, rect.Top, rect.Width, rect.Height);
        var dy = MoveCorrection(rect, others, display.HeightPx, false, thresholdPx);
        return new PixelRect(rect.Left, rect.Top + dy, rect.Width, rect.Height);
    }

    /// <summary>Snap only the edge(s) being dragged; never move the opposite edge.</summary>
    public static PixelRect SnapResized(
        DisplayProfile display, LayoutProfile layout, Guid movingId,
        PixelRect proposed, int horizontalEdge, int verticalEdge,
        int thresholdPx = DefaultThresholdPx)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(layout);
        if (display.WidthPx <= 0 || display.HeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(display));

        var rect = proposed;
        var others = Neighbors(display, layout, movingId);
        var minimumWidth = Math.Min(96, display.WidthPx);
        var minimumHeight = Math.Min(72, display.HeightPx);

        if (horizontalEdge != 0)
        {
            var edge = horizontalEdge < 0 ? rect.Left : rect.Right;
            var lower = horizontalEdge < 0 ? 0 : rect.Left + minimumWidth;
            var upper = horizontalEdge < 0 ? rect.Right - minimumWidth : display.WidthPx;
            var target = ResizeTarget(edge, lower, upper, rect, others,
                display.WidthPx, true, horizontalEdge, thresholdPx);
            rect = horizontalEdge < 0
                ? new PixelRect(target, rect.Top, rect.Right - target, rect.Height)
                : new PixelRect(rect.Left, rect.Top, target - rect.Left, rect.Height);
        }

        if (verticalEdge != 0)
        {
            var edge = verticalEdge < 0 ? rect.Top : rect.Bottom;
            var lower = verticalEdge < 0 ? 0 : rect.Top + minimumHeight;
            var upper = verticalEdge < 0 ? rect.Bottom - minimumHeight : display.HeightPx;
            var target = ResizeTarget(edge, lower, upper, rect, others,
                display.HeightPx, false, verticalEdge, thresholdPx);
            rect = verticalEdge < 0
                ? new PixelRect(rect.Left, target, rect.Width, rect.Bottom - target)
                : new PixelRect(rect.Left, rect.Top, rect.Width, target - rect.Top);
        }

        return rect;
    }

    private static PixelRect[] Neighbors(DisplayProfile display, LayoutProfile layout, Guid movingId)
        => layout.Zones.Where(zone => zone.Id != movingId)
            .Select(zone => DisplayGeometry.ZoneToPixels(display, zone, includeDisplayOffset: false))
            .ToArray();

    private static bool TouchingProjection(int a0, int a1, int b0, int b1, int threshold)
        => (long)a0 <= (long)b1 + threshold && (long)b0 <= (long)a1 + threshold;

    private static int MoveCorrection(
        PixelRect source, PixelRect[] others, int physicalExtent, bool horizontal, int threshold)
    {
        threshold = Math.Max(0, threshold);
        var start = horizontal ? source.Left : source.Top;
        var end = horizontal ? source.Right : source.Bottom;
        var orthogonalStart = horizontal ? source.Top : source.Left;
        var orthogonalEnd = horizontal ? source.Bottom : source.Right;
        var bestDelta = 0;
        var bestDistance = (long)threshold + 1;
        var bestPriority = int.MaxValue;
        void Consider(int delta, int priority)
        {
            var distance = Math.Abs((long)delta);
            if (distance > threshold || distance > bestDistance ||
                (distance == bestDistance && priority >= bestPriority))
                return;
            bestDelta = delta;
            bestDistance = distance;
            bestPriority = priority;
        }

        // Physical display edges are valid magnetic anchors too.
        Consider(-start, 0);
        Consider(physicalExtent - end, 0);
        foreach (var other in others)
        {
            var o0 = horizontal ? other.Top : other.Left;
            var o1 = horizontal ? other.Bottom : other.Right;
            if (!TouchingProjection(orthogonalStart, orthogonalEnd, o0, o1, threshold))
                continue;

            var otherStart = horizontal ? other.Left : other.Top;
            var otherEnd = horizontal ? other.Right : other.Bottom;
            Consider(otherStart - end, 1);     // opposing edges meet
            Consider(otherEnd - start, 1);
            Consider(otherStart - start, 2);   // corresponding edges align
            Consider(otherEnd - end, 2);
        }
        return bestDelta;
    }

    private static int ResizeTarget(
        int edge, int lower, int upper, PixelRect source, PixelRect[] others,
        int physicalExtent, bool horizontal, int direction, int threshold)
    {
        threshold = Math.Max(0, threshold);
        if (lower > upper)
            return edge;

        var target = edge;
        var bestDistance = (long)threshold + 1;
        var bestPriority = int.MaxValue;
        void Consider(int candidate, int priority)
        {
            if (candidate < lower || candidate > upper)
                return;
            var distance = Math.Abs((long)candidate - edge);
            if (distance > threshold || distance > bestDistance ||
                (distance == bestDistance && priority >= bestPriority))
                return;
            target = candidate;
            bestDistance = distance;
            bestPriority = priority;
        }

        Consider(direction < 0 ? 0 : physicalExtent, 0);
        foreach (var other in others)
        {
            if (!TouchingProjection(horizontal ? source.Top : source.Left,
                horizontal ? source.Bottom : source.Right,
                horizontal ? other.Top : other.Left,
                horizontal ? other.Bottom : other.Right, threshold))
                continue;
            Consider(horizontal ? other.Left : other.Top, 1);
            Consider(horizontal ? other.Right : other.Bottom, 1);
        }
        return target;
    }
}
