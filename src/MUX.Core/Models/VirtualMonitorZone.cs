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

    public string AspectLabel => $"{AspectWidth:0.##}:{AspectHeight:0.##}";
}
