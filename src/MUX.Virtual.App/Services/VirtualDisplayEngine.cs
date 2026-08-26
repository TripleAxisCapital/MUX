using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public enum VirtualActivationStage
{
    DriverSetup,
    SoftwareDevice,
    DisplayTopology,
    Compositor,
    InputPortal
}

public sealed class VirtualActivationException : Exception
{
    public VirtualActivationException(VirtualActivationStage stage, Exception inner)
        : base($"{StageLabel(stage)} failed: {inner.Message}", inner)
    {
        Stage = stage;
        HResult = inner.HResult;
    }

    public VirtualActivationStage Stage { get; }

    private static string StageLabel(VirtualActivationStage stage) => stage switch
    {
        VirtualActivationStage.DriverSetup => "Preparing the MUX virtual-display driver",
        VirtualActivationStage.SoftwareDevice => "Creating the Windows virtual-display device",
        VirtualActivationStage.DisplayTopology => "Attaching the Windows virtual monitors",
        VirtualActivationStage.Compositor => "Starting the physical MUX monitor portals",
        VirtualActivationStage.InputPortal => "Starting virtual-monitor input routing",
        _ => "MUX Virtual activation"
    };
}

public sealed class VirtualDisplayEngine : IDisposable
{
    private readonly DriverPackageService _driver = new();
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
            // Activation is intentionally self-preparing. A user must never have to know
            // that "Install driver" has to be pressed before "Activate". For the rolling
            // development package, MainWindow already requires Windows Test Mode before
            // reaching this point. Once the user explicitly activates MUX Virtual, stage
            // the bundled driver and, if Windows rejects the WDK test certificate, trust
            // only that bundled public certificate and retry.
            try
            {
                var install = await _driver.InstallAsync(allowTestCertificateTrust: true);
                if (!install.Success)
                {
                    throw new InvalidOperationException(install.Output);
                }
            }
            catch (Exception ex)
            {
                throw new VirtualActivationException(VirtualActivationStage.DriverSetup, ex);
            }

            try
            {
                await _device.CreateAsync(plans, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new VirtualActivationException(VirtualActivationStage.SoftwareDevice, ex);
            }

            try
            {
                ActiveMonitors = await _topology.ConfigureAsync(plans, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new VirtualActivationException(VirtualActivationStage.DisplayTopology, ex);
            }

            try
            {
                _compositor.Start(ActiveMonitors);
            }
            catch (Exception ex)
            {
                throw new VirtualActivationException(VirtualActivationStage.Compositor, ex);
            }

            try
            {
                _portal.Start(ActiveMonitors);
            }
            catch (Exception ex)
            {
                throw new VirtualActivationException(VirtualActivationStage.InputPortal, ex);
            }

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
