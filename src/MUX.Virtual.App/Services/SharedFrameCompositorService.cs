using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class SharedFrameCompositorService : IDisposable
{
    private bool _running;

    public async Task StartAsync(
        IReadOnlyList<ActiveVirtualMonitor> monitors,
        CancellationToken cancellationToken = default)
    {
        Stop();

        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("MUX has no active virtual monitors to present.");
        }

        var descriptors = monitors.Select(x => new PortalDescriptor
        {
            MonitorId = x.Plan.ZoneId,
            HostLeft = x.Plan.HostRect.Left,
            HostTop = x.Plan.HostRect.Top,
            Width = checked((uint)x.Plan.HostRect.Width),
            Height = checked((uint)x.Plan.HostRect.Height),
            VirtualLeft = x.VirtualRect.Left,
            VirtualTop = x.VirtualRect.Top
        }).ToArray();

        var hr = MuxPortalStart(descriptors, checked((uint)descriptors.Length));
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        _running = true;

        try
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var connected = MuxPortalConnectedCount();
                if (connected >= descriptors.Length)
                {
                    return;
                }

                await Task.Delay(100, cancellationToken);
            }

            var finalCount = MuxPortalConnectedCount();
            throw new InvalidOperationException(
                $"MUX connected {finalCount} of {descriptors.Length} live display frame channels. " +
                "The IddCx monitors exist, but Windows did not expose all live swap-chain frames to the portal compositor.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        MuxPortalStop();
        _running = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PortalDescriptor
    {
        public Guid MonitorId;
        public int HostLeft;
        public int HostTop;
        public uint Width;
        public uint Height;
        public int VirtualLeft;
        public int VirtualTop;
    }

    [DllImport("MUX.SwDeviceBridge.dll", ExactSpelling = true)]
    private static extern int MuxPortalStart(
        [In] PortalDescriptor[] descriptors,
        uint count);

    [DllImport("MUX.SwDeviceBridge.dll", ExactSpelling = true)]
    private static extern uint MuxPortalConnectedCount();

    [DllImport("MUX.SwDeviceBridge.dll", ExactSpelling = true)]
    private static extern void MuxPortalStop();
}
