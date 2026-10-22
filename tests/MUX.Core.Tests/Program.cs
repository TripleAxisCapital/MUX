using MUX.Core.Geometry;
using MUX.Core.Models;

var failures = new List<string>();

Run("100-inch 16:9 physical dimensions", () =>
{
    var size = DisplayGeometry.PhysicalSizeFromDiagonal(100, 16, 9);
    Near(size.Width, 87.158, 0.01, "width");
    Near(size.Height, 49.026, 0.01, "height");
});

Run("27-inch zone converts to physical pixels", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var zone = new VirtualMonitorZone { DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 10, YInches = 5 };
    var rect = DisplayGeometry.ZoneToPixels(display, zone, includeDisplayOffset: false);
    Near(rect.Width, 1037, 2, "pixel width");
    Near(rect.Height, 583, 2, "pixel height");
    Near(rect.Left, 441, 2, "left");
    Near(rect.Top, 220, 2, "top");
});

Run("calibration changes pixels-per-inch", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1.05 };
    var expected = Math.Sqrt(3840d * 3840 + 2160d * 2160) / 100 * 1.05;
    Near(DisplayGeometry.PixelsPerInch(display), expected, 0.0001, "ppi");
});

Run("freeform diagonal uses calibrated display scale", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1.05 };
    var pixels = DisplayGeometry.PixelsFromPhysicalDiagonal(display, 25, 1600, 900);
    var measured = DisplayGeometry.PhysicalDiagonalFromPixels(display, pixels.Width, pixels.Height);
    Near(measured, 25, 0.03, "physical diagonal");
});

Run("freeform diagonal preserves supplied window aspect ratio", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var pixels = DisplayGeometry.PixelsFromPhysicalDiagonal(display, 25, 1400, 900);
    Near((double)pixels.Width / pixels.Height, 1400d / 900d, 0.002, "aspect ratio");
});

Run("freeform diagonal can force 16:9", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var pixels = DisplayGeometry.PixelsFromPhysicalDiagonal(display, 25, 16d, 9d);
    Near((double)pixels.Width / pixels.Height, 16d / 9d, 0.002, "16:9 aspect ratio");
    Near(DisplayGeometry.PhysicalDiagonalFromPixels(display, pixels.Width, pixels.Height), 25, 0.03, "16:9 diagonal");
});

Run("freeform diagonal can force 9:16", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var pixels = DisplayGeometry.PixelsFromPhysicalDiagonal(display, 25, 9d, 16d);
    Near((double)pixels.Width / pixels.Height, 9d / 16d, 0.002, "9:16 aspect ratio");
    Near(DisplayGeometry.PhysicalDiagonalFromPixels(display, pixels.Width, pixels.Height), 25, 0.03, "9:16 diagonal");
});

Run("zones clamp inside physical display", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var zone = new VirtualMonitorZone { DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 500, YInches = 500 };
    DisplayGeometry.ClampZoneToDisplay(display, zone);
    var displaySize = DisplayGeometry.DisplayPhysicalSize(display);
    var zoneSize = DisplayGeometry.PhysicalSizeFromDiagonal(zone.DiagonalInches, zone.AspectWidth, zone.AspectHeight);
    if (zone.XInches + zoneSize.Width > displaySize.Width + 0.0001 || zone.YInches + zoneSize.Height > displaySize.Height + 0.0001)
    {
        throw new Exception("zone remained outside the display");
    }
});

Run("best-zone selection prefers window center", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var layout = new LayoutProfile();
    var first = new VirtualMonitorZone { Name = "Left", DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 0, YInches = 0 };
    var second = new VirtualMonitorZone { Name = "Right", DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 30, YInches = 0 };
    layout.Zones.Add(first);
    layout.Zones.Add(second);
    var rightRect = DisplayGeometry.ZoneToPixels(display, second);
    var window = new PixelRect(rightRect.Left + 20, rightRect.Top + 20, 500, 300);
    var selected = DisplayGeometry.FindBestZone(display, layout, window);
    if (selected?.Id != second.Id)
    {
        throw new Exception("did not select the expected zone");
    }
});

Run("maximize selection prefers the title-bar zone", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 100, CalibrationScale = 1 };
    var layout = new LayoutProfile();
    var top = new VirtualMonitorZone { Name = "Top", DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 0, YInches = 0 };
    var bottom = new VirtualMonitorZone { Name = "Bottom", DiagonalInches = 27, AspectWidth = 16, AspectHeight = 9, XInches = 0, YInches = 13.3 };
    layout.Zones.Add(top);
    layout.Zones.Add(bottom);

    var topRect = DisplayGeometry.ZoneToPixels(display, top);
    var window = new PixelRect(topRect.Left + 80, topRect.Bottom - 40, 700, 500);
    var centerSelected = DisplayGeometry.FindBestZone(display, layout, window);
    var maximizeSelected = DisplayGeometry.FindZoneForMaximize(display, layout, window);

    if (centerSelected?.Id != bottom.Id)
    {
        throw new Exception("test window center did not land in the lower zone");
    }

    if (maximizeSelected?.Id != top.Id)
    {
        throw new Exception("maximize did not follow the title-bar zone");
    }
});

Run("drag target is chosen only when the cursor is inside a zone", () =>
{
    var display = new DisplayProfile { LeftPx = -2560, TopPx = 100, WidthPx = 2560, HeightPx = 1440, DiagonalInches = 50 };
    var layout = new LayoutProfile();
    var first = new VirtualMonitorZone { DiagonalInches = 20, XInches = 0, YInches = 0 };
    var second = new VirtualMonitorZone { DiagonalInches = 20, XInches = 22, YInches = 0 };
    layout.Zones.Add(first);
    layout.Zones.Add(second);
    var rect = DisplayGeometry.ZoneToPixels(display, second);
    if (DisplayGeometry.FindZoneAtPoint(display, layout, rect.CenterX, rect.CenterY)?.Id != second.Id)
        throw new Exception("hover did not select second target on negative-origin display");
    if (DisplayGeometry.FindZoneAtPoint(display, layout, display.LeftPx + 4, display.TopPx + display.HeightPx - 4) is not null)
        throw new Exception("out-of-zone cursor selected a target");
});

Run("physical ruler calibration uses real screen pixels", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 55, CalibrationScale = 1 };
    const double visibleReferencePixels = 620;
    const double measuredReferenceInches = 7.75;
    var scale = DisplayGeometry.CalibrationScaleFromReference(display, visibleReferencePixels, measuredReferenceInches);
    display.CalibrationScale = scale;
    Near(DisplayGeometry.PixelsPerInch(display), 80, 0.0001, "calibrated ppi");
    var zone = new VirtualMonitorZone { DiagonalInches = 25, AspectWidth = 16, AspectHeight = 9 };
    var rect = DisplayGeometry.ZoneToPixels(display, zone, false);
    Near(DisplayGeometry.PhysicalDiagonalFromPixels(display, rect.Width, rect.Height), 25, 0.02, "25-inch diagonal");
});

Run("invalid ruler input is rejected", () =>
{
    var display = new DisplayProfile { WidthPx = 1920, HeightPx = 1080, DiagonalInches = 27 };
    foreach (var invalid in new[] { double.NaN, 0d, double.PositiveInfinity })
    {
        try { DisplayGeometry.CalibrationScaleFromReference(display, 500, invalid); }
        catch (ArgumentOutOfRangeException) { continue; }
        throw new Exception("invalid measured ruler length was accepted");
    }
});

Run("10-inch reference frame calibrates exact diagonal", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 55, CalibrationScale = 1 };
    // The operator physically measured the 16:9 frame at precisely 10 inches.
    const double actualPpi = 91.0;
    var width = actualPpi * 10 * 16 / Math.Sqrt(16 * 16 + 9 * 9);
    var height = actualPpi * 10 * 9 / Math.Sqrt(16 * 16 + 9 * 9);
    display.CalibrationScale = DisplayGeometry.CalibrationScaleFromRectangle(display, width, height, 10);
    Near(DisplayGeometry.PixelsPerInch(display), actualPpi, 0.000001, "physical PPI");
});

Run("independent 15-inch calibration verification uses same physical dimensions", () =>
{
    var display = new DisplayProfile { WidthPx = 5120, HeightPx = 2880, DiagonalInches = 42 };
    const double ppi = 132.5;
    var hyp = Math.Sqrt(16d * 16 + 9d * 9);
    var width = ppi * 10 * 16 / hyp;
    var height = ppi * 10 * 9 / hyp;
    display.CalibrationScale = DisplayGeometry.CalibrationScaleFromRectangle(display, width, height, 10);
    var physical15 = DisplayGeometry.PixelsFromKnownDiagonal(display, 15, 16, 9);
    Near(DisplayGeometry.PhysicalDiagonalFromPixels(display, physical15.Width, physical15.Height), 15, 0.015, "verification");
});

Run("calibration remains consistent across 16:9, 4:3 and 9:16 zones", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 45 };
    display.CalibrationScale = DisplayGeometry.CalibrationScaleFromRectangle(display, 1000, 562.5, 10);
    foreach (var (w, h) in new[] { (16d, 9d), (4d, 3d), (9d, 16d) })
    {
        var zone = new VirtualMonitorZone { DiagonalInches = 25, AspectWidth = w, AspectHeight = h };
        var rect = DisplayGeometry.ZoneToPixels(display, zone, includeDisplayOffset: false);
        Near(DisplayGeometry.PhysicalDiagonalFromPixels(display, rect.Width, rect.Height),
             25, 0.02, "25-inch window aspect ratio");
    }
});

Run("high-DPI screen transform does not leak WPF dip units into physical PPI", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 48 };
    // 125% Windows display scale: UI frame 640x360 DIPs -> 800x450 actual pixels.
    const double wDip = 640, hDip = 360, scaleFactor = 1.25;
    display.CalibrationScale = DisplayGeometry.CalibrationScaleFromRectangle(
        display, wDip * scaleFactor, hDip * scaleFactor, 10);
    Near(DisplayGeometry.PixelsPerInch(display), Math.Sqrt(800d * 800 + 450d * 450) / 10,
         0.000001, "actual device pixel PPI");
});

Run("DWM invisible resize borders are compensated in window placement", () =>
{
    var raw = new PixelRect(90, 90, 820, 620);
    var visible = new PixelRect(98, 92, 804, 610); // 8px on left/right, 2px top, 8px bottom
    var target = new PixelRect(400, 200, 1200, 675);
    var placed = DisplayGeometry.RawRectForVisibleTarget(raw, visible, target);
    Near(placed.Left, 392, 0, "left");
    Near(placed.Top, 198, 0, "top");
    Near(placed.Width, 1216, 0, "width");
    Near(placed.Height, 685, 0, "height");
});

Run("DWM compensation leaves borderless windows unchanged", () =>
{
    var target = new PixelRect(-2200, 100, 1000, 600);
    var current = new PixelRect(10, 15, 500, 300);
    if (DisplayGeometry.RawRectForVisibleTarget(current, current, target) != target)
        throw new Exception("borderless target unexpectedly shifted");
});

Run("pathological invisible window margins do not distort target", () =>
{
    var target = new PixelRect(15, 20, 750, 500);
    var corrected = DisplayGeometry.RawRectForVisibleTarget(
        new PixelRect(0, 0, 900, 700), new PixelRect(130, 130, 500, 500), target);
    if (corrected != target)
        throw new Exception("unreasonable DWM margins were accepted");
});

Run("invalid rectangular reference length is rejected", () =>
{
    var display = new DisplayProfile { WidthPx = 3840, HeightPx = 2160, DiagonalInches = 40 };
    foreach (var invalid in new[] { 0d, double.NaN, double.PositiveInfinity })
    {
        try { DisplayGeometry.CalibrationScaleFromRectangle(display, invalid, 200, 10); }
        catch (ArgumentOutOfRangeException) { continue; }
        throw new Exception("invalid pixel measurement was accepted");
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"MUX.Core validation failed ({failures.Count}):");
    foreach (var failure in failures) Console.Error.WriteLine($"  - {failure}");
    return 1;
}

Console.WriteLine("MUX.Core validation passed: 21/21 checks.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL  {name}");
    }
}

static void Near(double actual, double expected, double tolerance, string label)
{
    if (Math.Abs(actual - expected) > tolerance)
    {
        throw new Exception($"{label}: expected {expected}, got {actual}");
    }
}
