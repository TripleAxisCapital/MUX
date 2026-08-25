namespace MUX.Core.Models;

public sealed class ShortcutSettings
{
    public ShortcutBinding ToggleMaximize { get; set; } = ShortcutBinding.CtrlAlt("M");
    public ShortcutBinding ToggleFullscreen { get; set; } = ShortcutBinding.CtrlAlt("F");
    public ShortcutBinding PreviousMonitor { get; set; } = ShortcutBinding.CtrlAlt("Left");
    public ShortcutBinding NextMonitor { get; set; } = ShortcutBinding.CtrlAlt("Right");
    public ShortcutBinding EditLayout { get; set; } = ShortcutBinding.CtrlAlt("E");
}

public sealed class ShortcutBinding
{
    public bool Control { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Windows { get; set; }
    public string Key { get; set; } = "M";

    public static ShortcutBinding CtrlAlt(string key) => new()
    {
        Control = true,
        Alt = true,
        Key = key
    };
}
