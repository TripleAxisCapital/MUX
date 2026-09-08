namespace MUX.Core.Models;

public sealed class MuxState
{
    public int SchemaVersion { get; set; } = 6;
    public bool Enabled { get; set; } = true;
    public bool SnapOnDrag { get; set; } = true;
    public bool SnapRequiresShift { get; set; } = true;
    public bool ShowZoneOutlines { get; set; } = true;
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
