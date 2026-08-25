namespace MUX.Core.Models;

public sealed class DisplayProfile
{
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = "Display";
    public int LeftPx { get; set; }
    public int TopPx { get; set; }
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public bool IsPrimary { get; set; }
    public double DiagonalInches { get; set; } = 27.0;
    public double CalibrationScale { get; set; } = 1.0;

    public override string ToString() => $"{FriendlyName} · {WidthPx}×{HeightPx}";
}
