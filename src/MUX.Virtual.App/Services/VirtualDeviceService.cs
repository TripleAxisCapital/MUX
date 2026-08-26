using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class VirtualDeviceService : IDisposable
{
    private const uint DevPropTypeBinary = 0x00001003;
    private const uint Capabilities =
        0x00000001 | // Removable
        0x00000002 | // SilentInstall
        0x00000008;  // DriverRequired

    private static readonly Guid ConfigPropertyGuid =
        new("4B3E5D11-7C2A-4A91-9F3E-58A9D1C72A10");

    private static readonly Guid DeviceContainerId =
        new("5CFD15E8-FB01-4E89-8C55-BABAE7DA0829");

    private IntPtr _handle;
    private SwDeviceCreateCallback? _callback;

    public bool IsCreated => _handle != IntPtr.Zero;

    public async Task CreateAsync(
        IReadOnlyList<VirtualMonitorPlan> plans,
        CancellationToken cancellationToken = default)
    {
        if (plans.Count is < 1 or > VirtualDisplayPlanner.MaxVirtualMonitors)
        {
            throw new ArgumentOutOfRangeException(nameof(plans));
        }

        DisposeHandle();

        var configBytes = BuildDriverConfig(plans);
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _callback = (_, createResult, _, _) =>
        {
            completion.TrySetResult(createResult);
        };

        var instanceId = Marshal.StringToHGlobalUni("MUX-VIRTUAL-DISPLAY-ADAPTER");
        var hardwareIds = Marshal.StringToHGlobalUni("MUXVirtualDisplay\0\0");
        var compatibleIds = Marshal.StringToHGlobalUni("MUXVirtualDisplay\0\0");
        var description = Marshal.StringToHGlobalUni("MUX Virtual Display Adapter");
        var location = Marshal.StringToHGlobalUni("MUX");
        var containerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        var configPtr = Marshal.AllocHGlobal(configBytes.Length);

        try
        {
            Marshal.StructureToPtr(DeviceContainerId, containerPtr, false);
            Marshal.Copy(configBytes, 0, configPtr, configBytes.Length);

            var createInfo = new SwDeviceCreateInfo
            {
                cbSize = (uint)Marshal.SizeOf<SwDeviceCreateInfo>(),
                pszInstanceId = instanceId,
                pszzHardwareIds = hardwareIds,
                pszzCompatibleIds = compatibleIds,
                pContainerId = containerPtr,
                CapabilityFlags = Capabilities,
                pszDeviceDescription = description,
                pszDeviceLocation = location,
                pSecurityDescriptor = IntPtr.Zero
            };

            var property = new DevProperty
            {
                CompKey = new DevPropCompKey
                {
                    Key = new DevPropKey
                    {
                        fmtid = ConfigPropertyGuid,
                        pid = 2
                    },
                    Store = 0,
                    LocaleName = IntPtr.Zero
                },
                Type = DevPropTypeBinary,
                BufferSize = (uint)configBytes.Length,
                Buffer = configPtr
            };

            var hr = SwDeviceCreate(
                "MUX",
                "HTREE\\ROOT\\0",
                ref createInfo,
                1,
                ref property,
                _callback,
                IntPtr.Zero,
                out _handle);

            Marshal.ThrowExceptionForHR(hr);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var createResult = await completion.Task.WaitAsync(timeoutCts.Token);
            Marshal.ThrowExceptionForHR(createResult);
        }
        catch
        {
            DisposeHandle();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(instanceId);
            Marshal.FreeHGlobal(hardwareIds);
            Marshal.FreeHGlobal(compatibleIds);
            Marshal.FreeHGlobal(description);
            Marshal.FreeHGlobal(location);
            Marshal.FreeHGlobal(containerPtr);
            Marshal.FreeHGlobal(configPtr);
        }
    }

    public void Dispose()
    {
        DisposeHandle();
        GC.SuppressFinalize(this);
    }

    private void DisposeHandle()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SwDeviceClose(_handle);
        _handle = IntPtr.Zero;
    }

    private static byte[] BuildDriverConfig(
        IReadOnlyList<VirtualMonitorPlan> plans)
    {
        const int monitorSize = 28;
        const int maxMonitors = VirtualDisplayPlanner.MaxVirtualMonitors;
        var bytes = new byte[8 + monitorSize * maxMonitors];

        BitConverter.GetBytes(1u).CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)plans.Count).CopyTo(bytes, 4);

        for (var i = 0; i < plans.Count; i++)
        {
            var offset = 8 + i * monitorSize;
            var plan = plans[i];

            BitConverter.GetBytes((uint)plan.Width).CopyTo(bytes, offset);
            BitConverter.GetBytes((uint)plan.Height).CopyTo(bytes, offset + 4);
            BitConverter.GetBytes(plan.RefreshRate).CopyTo(bytes, offset + 8);
            plan.ZoneId.ToByteArray().CopyTo(bytes, offset + 12);
        }

        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwDeviceCreateInfo
    {
        public uint cbSize;
        public IntPtr pszInstanceId;
        public IntPtr pszzHardwareIds;
        public IntPtr pszzCompatibleIds;
        public IntPtr pContainerId;
        public uint CapabilityFlags;
        public IntPtr pszDeviceDescription;
        public IntPtr pszDeviceLocation;
        public IntPtr pSecurityDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropCompKey
    {
        public DevPropKey Key;
        public uint Store;
        public IntPtr LocaleName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevProperty
    {
        public DevPropCompKey CompKey;
        public uint Type;
        public uint BufferSize;
        public IntPtr Buffer;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SwDeviceCreateCallback(
        IntPtr hSwDevice,
        int createResult,
        IntPtr context,
        IntPtr deviceInstanceId);

    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int SwDeviceCreate(
        string pszEnumeratorName,
        string pszParentDeviceInstance,
        ref SwDeviceCreateInfo pCreateInfo,
        uint cPropertyCount,
        ref DevProperty pProperties,
        SwDeviceCreateCallback pCallback,
        IntPtr pContext,
        out IntPtr phSwDevice);

    [DllImport("Cfgmgr32.dll")]
    private static extern void SwDeviceClose(IntPtr hSwDevice);
}
