using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public static class VirtualDisplayPlanner
{
    public const int MaxVirtualMonitors = 8;

    public static IReadOnlyList<VirtualMonitorPlan> Build(
        MuxState state,
        LayoutProfile layout)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(layout);

        var display = state.Displays.FirstOrDefault(x =>
            string.Equals(
                x.DeviceName,
                layout.DisplayDeviceName,
                StringComparison.OrdinalIgnoreCase));

        if (display is null)
        {
            throw new InvalidOperationException(
                "The physical display used by this layout is not currently available. Open MUX Standard, select the display, and save the layout again.");
        }

        if (layout.Zones.Count == 0)
        {
            throw new InvalidOperationException(
                "This layout has no monitors. Add at least one monitor in MUX Standard.");
        }

        if (layout.Zones.Count > MaxVirtualMonitors)
        {
            throw new InvalidOperationException(
                $"MUX Virtual currently supports up to {MaxVirtualMonitors} true virtual monitors per adapter.");
        }

        var plans = new List<VirtualMonitorPlan>(layout.Zones.Count);

        foreach (var zone in layout.Zones)
        {
            var rect = DisplayGeometry.ZoneToPixels(display, zone);

            if (rect.Width < 320 || rect.Height < 200)
            {
                throw new InvalidOperationException(
                    $"{zone.Name} resolves to {rect.Width}×{rect.Height}. A virtual monitor must be at least 320×200 pixels.");
            }

            plans.Add(new VirtualMonitorPlan(
                zone.Id,
                zone.Name,
                new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height),
                rect.Width,
                rect.Height,
                60));
        }

        return plans;
    }
}
