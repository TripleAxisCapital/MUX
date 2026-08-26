using MUX.Virtual.App.Models;

namespace MUX.Virtual.App.Services;

public sealed class VirtualDeviceService : IDisposable
{
    private const uint DevPropTypeBinary = 0x00001003;
    private const uint CapabilityRemovable = 0x00000001;
    private const uint CapabilitySilentInstall = 0x00000002;
    private const uint CapabilityDriverRequired = 0x00000008;
    private const uint Capabilities = CapabilityRemovable | CapabilitySilentInstall | CapabilityDriverRequired;
    private const string DeviceId = "MUXVirtualDisplay";

    private static readonly Guid ConfigPropertyGuid =
        new("4B3E5D11-7C2A-4A91-9F3E-58A9D1C72A10");

    private static readonly Guid DeviceContainerId =
        new("5CFD15E8-FB01-4E89-8C55-BABAE7DA0829");

    private IntPtr _handle;
    private SwDeviceCreateCallback? _callback;

    public bool IsCreated => _handle != IntPtr.Zero;
    public string? DeviceInstanceId { get; private set; }

    public async Task CreateAsync(
        IReadOnlyList<VirtualMonitorPlan> plans,
        CancellationToken cancellationToken = default)
    {
        if (plans.Count is < 1 or > VirtualDisplayPlanner.MaxVirtualMonitors)
        {
            throw new ArgumentOutOfRangeException(nameof(plans));
        }

        DisposeHandle();
        DeviceInstanceId = null;

        var configBytes = BuildDriverConfig(plans);
        var completion = CreateCompletionSource();
        var unmanaged = AllocateCommonStrings(includeOptionalMetadata: true);
        var containerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        var configPtr = Marshal.AllocHGlobal(configBytes.Length);

        try
        {
            Marshal.StructureToPtr(DeviceContainerId, containerPtr, false);
            Marshal.Copy(configBytes, 0, configPtr, configBytes.Length);

            var createInfo = BuildCreateInfo(unmanaged, containerPtr);
            var property = new DevProperty
            {
                CompKey = new DevPropCompKey
                {
                    Key = new DevPropKey { fmtid = ConfigPropertyGuid, pid = 2 },
                    Store = 0,
                    LocaleName = IntPtr.Zero
                },
                Type = DevPropTypeBinary,
                BufferSize = (uint)configBytes.Length,
                Buffer = configPtr
            };

            var hr = SwDeviceCreateConfigured(
                DeviceId,
                "HTREE\\ROOT\\0",
                ref createInfo,
                1,
                ref property,
                _callback!,
                IntPtr.Zero,
                out _handle);

            await FinishCreationAsync(hr, completion, cancellationToken, "configured MUX software device");
        }
        catch
        {
            DisposeHandle();
            throw;
        }
        finally
        {
            unmanaged.Free();
            Marshal.FreeHGlobal(containerPtr);
            Marshal.FreeHGlobal(configPtr);
        }
    }

    /// <summary>
    /// Uses the exact minimal parameter shape from Microsoft's IddSampleApp: no custom
    /// properties, no container ID and no location string. This is intentionally kept as
    /// a diagnostic probe so CI can distinguish Software Device API/PnP failures from
    /// MUX's dynamic configuration property path.
    /// </summary>
    public async Task CreateBareMicrosoftSampleShapeAsync(
        CancellationToken cancellationToken = default)
    {
        DisposeHandle();
        DeviceInstanceId = null;

        var completion = CreateCompletionSource();
        var unmanaged = AllocateCommonStrings(includeOptionalMetadata: false);
        try
        {
            var createInfo = BuildCreateInfo(unmanaged, IntPtr.Zero);
            createInfo.pszDeviceLocation = IntPtr.Zero;

            var hr = SwDeviceCreateBare(
                DeviceId,
                "HTREE\\ROOT\\0",
                ref createInfo,
                0,
                IntPtr.Zero,
                _callback!,
                IntPtr.Zero,
                out _handle);

            await FinishCreationAsync(hr, completion, cancellationToken, "minimal Microsoft-sample-shape software device");
        }
        catch
        {
            DisposeHandle();
            throw;
        }
        finally
        {
            unmanaged.Free();
        }
    }

    /// <summary>
    /// Control probe for the Windows Software Device API itself. Unlike the display probe,
    /// this device has no hardware/compatible IDs and does not require a driver. If this
    /// succeeds while the IddCx probe does not, the managed interop and SwDevice API path
    /// are healthy and the remaining limitation is in the display-driver environment.
    /// </summary>
    public async Task CreateDriverIndependentApiProbeAsync(
        CancellationToken cancellationToken = default)
    {
        const string probeId = "MUXSoftwareDeviceApiProbe";
        DisposeHandle();
        DeviceInstanceId = null;

        var completion = CreateCompletionSource();
        var instanceId = Marshal.StringToHGlobalUni(probeId);
        var description = Marshal.StringToHGlobalUni("MUX Software Device API Probe");
        try
        {
            var createInfo = new SwDeviceCreateInfo
            {
                cbSize = (uint)Marshal.SizeOf<SwDeviceCreateInfo>(),
                pszInstanceId = instanceId,
                pszzHardwareIds = IntPtr.Zero,
                pszzCompatibleIds = IntPtr.Zero,
                pContainerId = IntPtr.Zero,
                CapabilityFlags = CapabilityRemovable | CapabilitySilentInstall,
                pszDeviceDescription = description,
                pszDeviceLocation = IntPtr.Zero,
                pSecurityDescriptor = IntPtr.Zero
            };

            var hr = SwDeviceCreateBare(
                probeId,
                "HTREE\\ROOT\\0",
                ref createInfo,
                0,
                IntPtr.Zero,
                _callback!,
                IntPtr.Zero,
                out _handle);

            await FinishCreationAsync(hr, completion, cancellationToken, "driver-independent Software Device API probe");
        }
        catch
        {
            DisposeHandle();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(instanceId);
            Marshal.FreeHGlobal(description);
        }
    }

    private TaskCompletionSource<DeviceCreateCompletion> CreateCompletionSource()
    {
        var completion = new TaskCompletionSource<DeviceCreateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callback = (_, createResult, _, deviceInstanceId) =>
        {
            var instance = deviceInstanceId == IntPtr.Zero ? null : Marshal.PtrToStringUni(deviceInstanceId);
            completion.TrySetResult(new DeviceCreateCompletion(createResult, instance));
        };
        return completion;
    }

    private static UnmanagedStrings AllocateCommonStrings(bool includeOptionalMetadata) => new(
        Marshal.StringToHGlobalUni(DeviceId),
        Marshal.StringToHGlobalUni(DeviceId + "\0\0"),
        Marshal.StringToHGlobalUni(DeviceId + "\0\0"),
        Marshal.StringToHGlobalUni("MUX Virtual Display Adapter"),
        includeOptionalMetadata ? Marshal.StringToHGlobalUni("MUX") : IntPtr.Zero);

    private static SwDeviceCreateInfo BuildCreateInfo(UnmanagedStrings unmanaged, IntPtr containerId) => new()
    {
        cbSize = (uint)Marshal.SizeOf<SwDeviceCreateInfo>(),
        pszInstanceId = unmanaged.InstanceId,
        pszzHardwareIds = unmanaged.HardwareIds,
        pszzCompatibleIds = unmanaged.CompatibleIds,
        pContainerId = containerId,
        CapabilityFlags = Capabilities,
        pszDeviceDescription = unmanaged.Description,
        pszDeviceLocation = unmanaged.Location,
        pSecurityDescriptor = IntPtr.Zero
    };

    private async Task FinishCreationAsync(
        int initialHResult,
        TaskCompletionSource<DeviceCreateCompletion> completion,
        CancellationToken cancellationToken,
        string shape)
    {
        if (initialHResult < 0)
        {
            throw BuildCreateException(initialHResult, null, $"SwDeviceCreate could not start {shape} enumeration");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        var completed = await completion.Task.WaitAsync(timeoutCts.Token);
        DeviceInstanceId = completed.InstanceId;
        if (completed.HResult < 0)
        {
            throw BuildCreateException(completed.HResult, completed.InstanceId,
                $"Windows PnP could not enumerate/start the {shape}");
        }
    }

    private static Exception BuildCreateException(int hr, string? instanceId, string heading)
    {
        var code = unchecked((uint)hr);
        var native = Marshal.GetExceptionForHR(hr)?.Message ?? "Unknown Windows error";
        var instance = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : $" Device: {instanceId}.";
        var hint = code == 0x8007007E
            ? " Windows reported ERROR_MOD_NOT_FOUND before the MUX display stack became usable."
            : string.Empty;

        return new InvalidOperationException($"{heading} (0x{code:X8}: {native}).{instance}{hint}");
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
        DeviceInstanceId = null;
    }

    private static byte[] BuildDriverConfig(IReadOnlyList<VirtualMonitorPlan> plans)
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

    private sealed record DeviceCreateCompletion(int HResult, string? InstanceId);

    private readonly record struct UnmanagedStrings(
        IntPtr InstanceId,
        IntPtr HardwareIds,
        IntPtr CompatibleIds,
        IntPtr Description,
        IntPtr Location)
    {
        public void Free()
        {
            foreach (var pointer in new[] { InstanceId, HardwareIds, CompatibleIds, Description, Location })
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }
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
    private struct DevPropKey { public Guid fmtid; public uint pid; }

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
    private delegate void SwDeviceCreateCallback(IntPtr hSwDevice, int createResult, IntPtr context, IntPtr deviceInstanceId);

    [DllImport("Cfgmgr32.dll", EntryPoint = "SwDeviceCreate", CharSet = CharSet.Unicode)]
    private static extern int SwDeviceCreateConfigured(
        string pszEnumeratorName,
        string pszParentDeviceInstance,
        ref SwDeviceCreateInfo pCreateInfo,
        uint cPropertyCount,
        ref DevProperty pProperties,
        SwDeviceCreateCallback pCallback,
        IntPtr pContext,
        out IntPtr phSwDevice);

    [DllImport("Cfgmgr32.dll", EntryPoint = "SwDeviceCreate", CharSet = CharSet.Unicode)]
    private static extern int SwDeviceCreateBare(
        string pszEnumeratorName,
        string pszParentDeviceInstance,
        ref SwDeviceCreateInfo pCreateInfo,
        uint cPropertyCount,
        IntPtr pProperties,
        SwDeviceCreateCallback pCallback,
        IntPtr pContext,
        out IntPtr phSwDevice);

    [DllImport("Cfgmgr32.dll")]
    private static extern void SwDeviceClose(IntPtr hSwDevice);
}
