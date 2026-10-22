namespace MUX.Core.Models;

public sealed class MuxState
{
    public int SchemaVersion { get; set; } = 7;
    public bool Enabled { get; set; } = true;
    public bool SnapOnDrag { get; set; } = true;
    public bool SnapRequiresShift { get; set; } = false;
    public bool ShowZoneOutlines { get; set; } = false;
    public string SnapHighlightColor { get; set; } = "#30D158";
    public double ZoneOutlineThickness { get; set; } = 2.0;
    public bool LaunchAtStartup { get; set; }
    public double AutoArrangeDiagonalInches { get; set; } = 20.0;
    public string ActiveDisplayDeviceName { get; set; } = string.Empty;
    public Guid? ActiveLayoutId { get; set; }
    public List<string> CaptionPillDisabledDisplayDeviceNames { get; set; } = new();
    public List<DisplayProfile> Displays { get; set; } = new();
    public List<LayoutProfile> Layouts { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
}
