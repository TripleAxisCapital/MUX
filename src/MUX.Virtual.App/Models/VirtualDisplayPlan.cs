namespace MUX.Virtual.App.Models;

public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed record VirtualMonitorPlan(
    Guid ZoneId,
    string Name,
    ScreenRect HostRect,
    int Width,
    int Height,
    uint RefreshRate);

public sealed record ActiveVirtualMonitor(
    VirtualMonitorPlan Plan,
    string DeviceName,
    ScreenRect VirtualRect);
