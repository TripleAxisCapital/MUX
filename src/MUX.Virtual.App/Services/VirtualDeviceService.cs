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
    private const string RootParentId = "HTREE\\ROOT\\0";
    private const uint CallbackTimeoutMs = 20_000;
    private const int InstanceBufferChars = 512;

    private static readonly Guid ConfigPropertyGuid =
        new("4B3E5D11-7C2A-4A91-9F3E-58A9D1C72A10");

    private static readonly Guid DeviceContainerId =
        new("5CFD15E8-FB01-4E89-8C55-BABAE7DA0829");

    private IntPtr _handle;

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
        var unmanaged = AllocateCommonStrings(includeOptionalMetadata: true);
        var containerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        var configPtr = Marshal.AllocHGlobal(configBytes.Length);

        try
        {
            Marshal.StructureToPtr(DeviceContainerId, containerPtr, false);
            Marshal.Copy(configBytes, 0, configPtr, configBytes.Length);

            await CreateWithNativeBridgeAsync(
                DeviceId,
                RootParentId,
                unmanaged.InstanceId,
                unmanaged.HardwareIds,
                unmanaged.CompatibleIds,
                containerPtr,
                Capabilities,
                unmanaged.Description,
                unmanaged.Location,
                ConfigPropertyGuid,
                2,
                DevPropTypeBinary,
                configPtr,
                (uint)configBytes.Length,
                "configured MUX software device",
                cancellationToken);
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
    /// properties, no container ID and no location string. This lets CI distinguish a
    /// general Software Device API failure from an IddCx-specific hosted-runner limitation.
    /// </summary>
    public async Task CreateBareMicrosoftSampleShapeAsync(
        CancellationToken cancellationToken = default)
    {
        DisposeHandle();
        DeviceInstanceId = null;

        var unmanaged = AllocateCommonStrings(includeOptionalMetadata: false);
        try
        {
            await CreateWithNativeBridgeAsync(
                DeviceId,
                RootParentId,
                unmanaged.InstanceId,
                unmanaged.HardwareIds,
                unmanaged.CompatibleIds,
                IntPtr.Zero,
                Capabilities,
                unmanaged.Description,
                IntPtr.Zero,
                null,
                0,
                0,
                IntPtr.Zero,
                0,
                "minimal Microsoft-sample-shape software device",
                cancellationToken);
        }
        finally
        {
            unmanaged.Free();
        }
    }

    /// <summary>
    /// Driver-independent control probe. The same native Software Device API path is used
    /// by the real MUX device, but this shape has no hardware IDs and requires no driver.
    /// </summary>
    public async Task CreateDriverIndependentApiProbeAsync(
        CancellationToken cancellationToken = default)
    {
        const string probeId = "MUXSoftwareDeviceApiProbe";
        DisposeHandle();
        DeviceInstanceId = null;

        var enumerator = Marshal.StringToHGlobalUni(probeId);
        var parent = Marshal.StringToHGlobalUni(RootParentId);
        var instanceId = Marshal.StringToHGlobalUni(probeId);
        var description = Marshal.StringToHGlobalUni("MUX Software Device API Probe");
        try
        {
            await CreateWithNativeBridgeAsync(
                enumerator,
                parent,
                instanceId,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CapabilityRemovable | CapabilitySilentInstall,
                description,
                IntPtr.Zero,
                null,
                0,
                0,
                IntPtr.Zero,
                0,
                "driver-independent Software Device API probe",
                cancellationToken,
                ownsEnumeratorPointers: false);
        }
        finally
        {
            Marshal.FreeHGlobal(enumerator);
            Marshal.FreeHGlobal(parent);
            Marshal.FreeHGlobal(instanceId);
            Marshal.FreeHGlobal(description);
        }
    }

    private Task CreateWithNativeBridgeAsync(
        string enumerator,
        string parent,
        IntPtr instanceId,
        IntPtr hardwareIds,
        IntPtr compatibleIds,
        IntPtr containerId,
        uint capabilityFlags,
        IntPtr description,
        IntPtr location,
        Guid? propertyGuid,
        uint propertyPid,
        uint propertyType,
        IntPtr propertyData,
        uint propertyDataSize,
        string shape,
        CancellationToken cancellationToken)
    {
        var enumeratorPtr = Marshal.StringToHGlobalUni(enumerator);
        var parentPtr = Marshal.StringToHGlobalUni(parent);
        return CreateWithNativeBridgeAsync(
            enumeratorPtr,
            parentPtr,
            instanceId,
            hardwareIds,
            compatibleIds,
            containerId,
            capabilityFlags,
            description,
            location,
            propertyGuid,
            propertyPid,
            propertyType,
            propertyData,
            propertyDataSize,
            shape,
            cancellationToken,
            ownsEnumeratorPointers: true);
    }

    private async Task CreateWithNativeBridgeAsync(
        IntPtr enumerator,
        IntPtr parent,
        IntPtr instanceId,
        IntPtr hardwareIds,
        IntPtr compatibleIds,
        IntPtr containerId,
        uint capabilityFlags,
        IntPtr description,
        IntPtr location,
        Guid? propertyGuid,
        uint propertyPid,
        uint propertyType,
        IntPtr propertyData,
        uint propertyDataSize,
        string shape,
        CancellationToken cancellationToken,
        bool ownsEnumeratorPointers)
    {
        IntPtr propertyGuidPtr = IntPtr.Zero;
        var instanceBuffer = Marshal.AllocHGlobal(InstanceBufferChars * sizeof(char));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < InstanceBufferChars * sizeof(char); i++)
            {
                Marshal.WriteByte(instanceBuffer, i, 0);
            }

            if (propertyGuid is Guid guid)
            {
                propertyGuidPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                Marshal.StructureToPtr(guid, propertyGuidPtr, false);
            }

            NativeCreateResult native;
            try
            {
                native = await Task.Run(() =>
                {
                    var initial = MuxSwDeviceCreate(
                        enumerator,
                        parent,
                        instanceId,
                        hardwareIds,
                        compatibleIds,
                        containerId,
                        capabilityFlags,
                        description,
                        location,
                        propertyGuidPtr,
                        propertyPid,
                        propertyType,
                        propertyData,
                        propertyDataSize,
                        out var handle,
                        out var callbackResult,
                        instanceBuffer,
                        InstanceBufferChars,
                        CallbackTimeoutMs);

                    var returnedInstanceId = Marshal.PtrToStringUni(instanceBuffer);
                    return new NativeCreateResult(initial, callbackResult, handle, returnedInstanceId);
                }, cancellationToken);
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "MUX.SwDeviceBridge.dll is missing from the MUX Virtual package. Re-download the complete Virtual build.", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "MUX.SwDeviceBridge.dll is incompatible with this MUX Virtual controller build. Re-download the complete Virtual build.", ex);
            }

            _handle = native.Handle;
            DeviceInstanceId = string.IsNullOrWhiteSpace(native.InstanceId) ? null : native.InstanceId;

            if (native.InitialHResult < 0)
            {
                throw BuildCreateException(native.InitialHResult, DeviceInstanceId,
                    $"SwDeviceCreate could not start {shape} enumeration");
            }

            if (native.CallbackHResult < 0)
            {
                throw BuildCreateException(native.CallbackHResult, DeviceInstanceId,
                    $"Windows PnP could not enumerate/start the {shape}");
            }
        }
        catch
        {
            DisposeHandle();
            throw;
        }
        finally
        {
            if (propertyGuidPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propertyGuidPtr);
            }
            Marshal.FreeHGlobal(instanceBuffer);
            if (ownsEnumeratorPointers)
            {
                Marshal.FreeHGlobal(enumerator);
                Marshal.FreeHGlobal(parent);
            }
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

        try
        {
            MuxSwDeviceClose(_handle);
        }
        catch (DllNotFoundException)
        {
            // Package validation prevents this in release builds. Do not mask shutdown.
        }
        finally
        {
            _handle = IntPtr.Zero;
            DeviceInstanceId = null;
        }
    }

    private static UnmanagedStrings AllocateCommonStrings(bool includeOptionalMetadata) => new(
        Marshal.StringToHGlobalUni(DeviceId),
        Marshal.StringToHGlobalUni(DeviceId + "\0\0"),
        Marshal.StringToHGlobalUni(DeviceId + "\0\0"),
        Marshal.StringToHGlobalUni("MUX Virtual Display Adapter"),
        includeOptionalMetadata ? Marshal.StringToHGlobalUni("MUX") : IntPtr.Zero);

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

    private sealed record NativeCreateResult(
        int InitialHResult,
        int CallbackHResult,
        IntPtr Handle,
        string? InstanceId);

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

    [DllImport("MUX.SwDeviceBridge.dll", EntryPoint = "MuxSwDeviceCreate", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern int MuxSwDeviceCreate(
        IntPtr enumeratorName,
        IntPtr parentDeviceInstance,
        IntPtr instanceId,
        IntPtr hardwareIds,
        IntPtr compatibleIds,
        IntPtr containerId,
        uint capabilityFlags,
        IntPtr description,
        IntPtr location,
        IntPtr propertyGuid,
        uint propertyPid,
        uint propertyType,
        IntPtr propertyData,
        uint propertyDataSize,
        out IntPtr device,
        out int createResult,
        IntPtr deviceInstanceIdBuffer,
        uint deviceInstanceIdBufferChars,
        uint timeoutMs);

    [DllImport("MUX.SwDeviceBridge.dll", EntryPoint = "MuxSwDeviceClose", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern void MuxSwDeviceClose(IntPtr device);
}
