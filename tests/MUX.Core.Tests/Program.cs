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

if (failures.Count > 0)
{
    Console.Error.WriteLine($"MUX.Core validation failed ({failures.Count}):");
    foreach (var failure in failures) Console.Error.WriteLine($"  - {failure}");
    return 1;
}

Console.WriteLine("MUX.Core validation passed: 5/5 checks.");
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
