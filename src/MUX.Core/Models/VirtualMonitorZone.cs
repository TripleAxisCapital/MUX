namespace MUX.Core.Models;

public sealed class VirtualMonitorZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Monitor";
    public double DiagonalInches { get; set; } = 27.0;
    public double AspectWidth { get; set; } = 16.0;
    public double AspectHeight { get; set; } = 9.0;
    public double XInches { get; set; }
    public double YInches { get; set; }
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; }

    public string AspectLabel
    {
        get
        {
            // Keep familiar preset labels, but avoid showing 1037:583 for custom
            // pixel-precise rectangles. Never round stored dimensions themselves.
            if (AspectWidth <= 100 && AspectHeight <= 100)
                return $"{AspectWidth:0.##}:{AspectHeight:0.##}";

            var aw = (int)Math.Round(AspectWidth);
            var ah = (int)Math.Round(AspectHeight);
            if (aw > 0 && ah > 0 &&
                Math.Abs(AspectWidth - aw) < 0.0001 &&
                Math.Abs(AspectHeight - ah) < 0.0001)
            {
                var a = aw;
                var c = ah;
                while (c != 0)
                    (a, c) = (c, a % c);
                if (a > 0 && aw / a <= 100 && ah / a <= 100)
                    return $"{aw / a}:{ah / a}";
            }

            return $"{AspectWidth / AspectHeight:0.###}:1";
        }
    }
}
