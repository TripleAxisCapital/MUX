using MUX.Core.Models;

namespace MUX.Core.Geometry;

public readonly record struct SizeD(double Width, double Height);
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

public static class DisplayGeometry
{
    private const int TitleBarProbeOffsetPx = 24;

    public static SizeD PhysicalSizeFromDiagonal(double diagonalInches, double aspectWidth, double aspectHeight)
    {
        ValidatePositive(diagonalInches, nameof(diagonalInches));
        ValidatePositive(aspectWidth, nameof(aspectWidth));
        ValidatePositive(aspectHeight, nameof(aspectHeight));

        var hypotenuse = Math.Sqrt(aspectWidth * aspectWidth + aspectHeight * aspectHeight);
        return new SizeD(
            diagonalInches * aspectWidth / hypotenuse,
            diagonalInches * aspectHeight / hypotenuse);
    }

    public static double PixelsPerInch(DisplayProfile display)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidatePositive(display.DiagonalInches, nameof(display.DiagonalInches));
        ValidatePositive(display.WidthPx, nameof(display.WidthPx));
        ValidatePositive(display.HeightPx, nameof(display.HeightPx));
        ValidatePositive(display.CalibrationScale, nameof(display.CalibrationScale));

        var diagonalPixels = Math.Sqrt((double)display.WidthPx * display.WidthPx + (double)display.HeightPx * display.HeightPx);
        return diagonalPixels / display.DiagonalInches * display.CalibrationScale;
    }

    public static SizeD DisplayPhysicalSize(DisplayProfile display)
    {
        var ppi = PixelsPerInch(display);
        return new SizeD(display.WidthPx / ppi, display.HeightPx / ppi);
    }

    public static PixelRect ZoneToPixels(DisplayProfile display, VirtualMonitorZone zone, bool includeDisplayOffset = true)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(zone);

        var ppi = PixelsPerInch(display);
        var zoneInches = PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);
        var left = (int)Math.Round(zone.XInches * ppi);
        var top = (int)Math.Round(zone.YInches * ppi);
        var width = Math.Max(1, (int)Math.Round(zoneInches.Width * ppi));
        var height = Math.Max(1, (int)Math.Round(zoneInches.Height * ppi));

        if (includeDisplayOffset)
        {
            left += display.LeftPx;
            top += display.TopPx;
        }

        return new PixelRect(left, top, width, height);
    }

    public static void ClampZoneToDisplay(DisplayProfile display, VirtualMonitorZone zone)
    {
        var displaySize = DisplayPhysicalSize(display);
        var zoneSize = PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);

        var maxX = Math.Max(0, displaySize.Width - zoneSize.Width);
        var maxY = Math.Max(0, displaySize.Height - zoneSize.Height);

        zone.XInches = Math.Clamp(zone.XInches, 0, maxX);
        zone.YInches = Math.Clamp(zone.YInches, 0, maxY);
    }

    public static double IntersectionArea(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);

        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return (double)(right - left) * (bottom - top);
    }

    public static VirtualMonitorZone? FindBestZone(DisplayProfile display, LayoutProfile layout, PixelRect windowRect)
    {
        if (layout.Zones.Count == 0)
        {
            return null;
        }

        var centerMatch = FindZoneContainingPoint(display, layout, windowRect.CenterX, windowRect.CenterY);
        if (centerMatch is not null)
        {
            return centerMatch;
        }

        return layout.Zones
            .Select(zone => new { Zone = zone, Area = IntersectionArea(windowRect, ZoneToPixels(display, zone)) })
            .OrderByDescending(x => x.Area)
            .FirstOrDefault(x => x.Area > 0)?.Zone;
    }

    public static VirtualMonitorZone? FindZoneForMaximize(DisplayProfile display, LayoutProfile layout, PixelRect windowRect)
    {
        if (layout.Zones.Count == 0)
        {
            return null;
        }

        var maxProbeOffset = Math.Max(0, windowRect.Height - 1);
        var titleBarY = windowRect.Top + Math.Min(TitleBarProbeOffsetPx, maxProbeOffset);
        var titleBarMatch = FindZoneContainingPoint(display, layout, windowRect.CenterX, titleBarY);
        return titleBarMatch ?? FindBestZone(display, layout, windowRect);
    }

    private static VirtualMonitorZone? FindZoneContainingPoint(DisplayProfile display, LayoutProfile layout, int x, int y)
    {
        return layout.Zones.FirstOrDefault(zone =>
        {
            var rect = ZoneToPixels(display, zone);
            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        });
    }

    public static (double AspectWidth, double AspectHeight) SimplifyAspect(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0)
        {
            return (16, 9);
        }

        var gcd = GreatestCommonDivisor(widthPx, heightPx);
        var w = widthPx / gcd;
        var h = heightPx / gcd;

        if (w > 100 || h > 100)
        {
            var ratio = (double)widthPx / heightPx;
            var common = new[]
            {
                (16d, 9d), (16d, 10d), (21d, 9d), (32d, 9d), (4d, 3d), (3d, 2d)
            };
            return common.OrderBy(x => Math.Abs(x.Item1 / x.Item2 - ratio)).First();
        }

        return (w, h);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Abs(a);
    }

    private static void ValidatePositive(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, "Value must be finite and greater than zero.");
        }
    }
}
