using MUX.Core.Models;

namespace MUX.Core.Geometry;

public readonly record struct SizeD(double Width, double Height);
public readonly record struct PixelSize(int Width, int Height);
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

    /// <summary>
    /// Uses the measured physical length of a rendered reference between its two
    /// screen-pixel endpoints. This avoids mistaking WPF device-independent units
    /// for physical pixels on high-DPI and mixed-DPI displays.
    /// </summary>
    public static double CalibrationScaleFromReference(DisplayProfile display, double referencePixels, double measuredInches)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidatePositive(display.WidthPx, nameof(display.WidthPx));
        ValidatePositive(display.HeightPx, nameof(display.HeightPx));
        ValidatePositive(display.DiagonalInches, nameof(display.DiagonalInches));
        ValidatePositive(referencePixels, nameof(referencePixels));
        ValidatePositive(measuredInches, nameof(measuredInches));
        var displayDiagonalPx = Math.Sqrt((double)display.WidthPx * display.WidthPx +
                                          (double)display.HeightPx * display.HeightPx);
        return (referencePixels / measuredInches) * display.DiagonalInches / displayDiagonalPx;
    }

    /// <summary>
    /// Calibrate a physical display from the exact width and height of a rectangular
    /// reference measured in DEVICE pixels, not WPF device-independent pixels.
    /// The diagonal specified by the user is the outer-corner diagonal in inches.
    /// </summary>
    public static double CalibrationScaleFromRectangle(
        DisplayProfile display, double widthPixels, double heightPixels, double knownDiagonalInches)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidatePositive(widthPixels, nameof(widthPixels));
        ValidatePositive(heightPixels, nameof(heightPixels));
        ValidatePositive(knownDiagonalInches, nameof(knownDiagonalInches));
        var diagonalPixels = Math.Sqrt(widthPixels * widthPixels + heightPixels * heightPixels);
        return CalibrationScaleFromReference(display, diagonalPixels, knownDiagonalInches);
    }

    /// <summary>
    /// Device-pixel diagonal for a requested physical rectangular reference.
    /// Both dimensions use one calibrated PPI, independent of the reference's aspect ratio.
    /// </summary>
    public static PixelSize PixelsFromKnownDiagonal(DisplayProfile display, double diagonalInches, double aspectWidth, double aspectHeight)
        => PixelsFromPhysicalDiagonal(display, diagonalInches, aspectWidth, aspectHeight);

    /// <summary>
    /// Windows may include invisible resize margins in GetWindowRect. Convert a
    /// desired *visible* frame rectangle into the matching raw SetWindowPos rectangle
    /// using margins observed on the same HWND (DWM extended-frame bounds).
    /// </summary>
    public static PixelRect RawRectForVisibleTarget(PixelRect currentRaw, PixelRect currentVisible, PixelRect desiredVisible)
    {
        if (currentRaw.Width <= 0 || currentRaw.Height <= 0 ||
            currentVisible.Width <= 0 || currentVisible.Height <= 0 ||
            desiredVisible.Width <= 0 || desiredVisible.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentRaw), "All window rectangles must have positive dimensions.");

        var leftInset = currentVisible.Left - currentRaw.Left;
        var topInset = currentVisible.Top - currentRaw.Top;
        var rightInset = currentRaw.Right - currentVisible.Right;
        var bottomInset = currentRaw.Bottom - currentVisible.Bottom;

        // Unknown custom shadows or pathological DWM frames must not create a
        // runaway-sized SetWindowPos target; fall back to the requested rectangle.
        if (new[] { leftInset, topInset, rightInset, bottomInset }
            .Any(inset => inset < -16 || inset > 64))
            return desiredVisible;

        return new PixelRect(desiredVisible.Left - leftInset,
                             desiredVisible.Top - topInset,
                             Math.Max(1, desiredVisible.Width + leftInset + rightInset),
                             Math.Max(1, desiredVisible.Height + topInset + bottomInset));
    }

    /// <summary>Find the destination strictly under the physical mouse pointer.</summary>
    public static VirtualMonitorZone? FindZoneAtPoint(DisplayProfile display, LayoutProfile layout, int screenX, int screenY)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(layout);
        return FindZoneContainingPoint(display, layout, screenX, screenY);
    }

    public static SizeD DisplayPhysicalSize(DisplayProfile display)
    {
        var ppi = PixelsPerInch(display);
        return new SizeD(display.WidthPx / ppi, display.HeightPx / ppi);
    }

    public static double PhysicalDiagonalFromPixels(DisplayProfile display, int widthPx, int heightPx)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (widthPx <= 0) throw new ArgumentOutOfRangeException(nameof(widthPx));
        if (heightPx <= 0) throw new ArgumentOutOfRangeException(nameof(heightPx));

        var diagonalPixels = Math.Sqrt((double)widthPx * widthPx + (double)heightPx * heightPx);
        return diagonalPixels / PixelsPerInch(display);
    }

    public static PixelSize PixelsFromPhysicalDiagonal(DisplayProfile display, double diagonalInches, int aspectWidthPx, int aspectHeightPx)
    {
        if (aspectWidthPx <= 0) throw new ArgumentOutOfRangeException(nameof(aspectWidthPx));
        if (aspectHeightPx <= 0) throw new ArgumentOutOfRangeException(nameof(aspectHeightPx));
        return PixelsFromPhysicalDiagonal(display, diagonalInches, (double)aspectWidthPx, aspectHeightPx);
    }

    public static PixelSize PixelsFromPhysicalDiagonal(
        DisplayProfile display,
        double diagonalInches,
        double aspectWidth,
        double aspectHeight)
    {
        ArgumentNullException.ThrowIfNull(display);
        ValidatePositive(diagonalInches, nameof(diagonalInches));
        ValidatePositive(aspectWidth, nameof(aspectWidth));
        ValidatePositive(aspectHeight, nameof(aspectHeight));

        var physicalSize = PhysicalSizeFromDiagonal(diagonalInches, aspectWidth, aspectHeight);
        var ppi = PixelsPerInch(display);
        return new PixelSize(
            Math.Max(1, (int)Math.Round(physicalSize.Width * ppi)),
            Math.Max(1, (int)Math.Round(physicalSize.Height * ppi)));
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
