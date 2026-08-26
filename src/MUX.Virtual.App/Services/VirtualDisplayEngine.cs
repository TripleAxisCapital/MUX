using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class VirtualDisplayEngine : IDisposable
{
    private readonly VirtualDeviceService _device = new();
    private readonly DisplayTopologyService _topology = new();
    private readonly MagnifierCompositorService _compositor = new();
    private readonly MousePortalService _portal = new();

    public bool IsRunning { get; private set; }

    public IReadOnlyList<ActiveVirtualMonitor> ActiveMonitors { get; private set; } =
        Array.Empty<ActiveVirtualMonitor>();

    public async Task StartAsync(
        IReadOnlyList<VirtualMonitorPlan> plans,
        CancellationToken cancellationToken = default)
    {
        Stop();

        try
        {
            await _device.CreateAsync(plans, cancellationToken);
            ActiveMonitors = await _topology.ConfigureAsync(
                plans,
                cancellationToken);

            _compositor.Start(ActiveMonitors);
            _portal.Start(ActiveMonitors);
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void ReleaseCursor() => _portal.ReleaseToHost();

    public void Stop()
    {
        IsRunning = false;
        _portal.Stop();
        _compositor.Stop();
        _device.Dispose();
        ActiveMonitors = Array.Empty<ActiveVirtualMonitor>();
    }

    public void Dispose()
    {
        Stop();
        _portal.Dispose();
        _compositor.Dispose();
        GC.SuppressFinalize(this);
    }
}
