namespace MUX.Core.Models;

public sealed class LayoutProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Workspace";
    public string DisplayDeviceName { get; set; } = string.Empty;
    public List<VirtualMonitorZone> Zones { get; set; } = new();

    public override string ToString() => Name;
}
