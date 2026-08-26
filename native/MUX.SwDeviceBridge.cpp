#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <swdevice.h>
#include <devpropdef.h>
#include <strsafe.h>

namespace
{
    constexpr size_t InstanceBufferChars = 512;

    struct CreateContext
    {
        HANDLE Event;
        HRESULT Result;
        wchar_t InstanceId[InstanceBufferChars];
    };

    VOID WINAPI CreationCallback(
        _In_ HSWDEVICE,
        _In_ HRESULT createResult,
        _In_opt_ PVOID context,
        _In_opt_ PCWSTR deviceInstanceId)
    {
        auto* state = static_cast<CreateContext*>(context);
        if (state == nullptr)
        {
            return;
        }

        state->Result = createResult;
        if (deviceInstanceId != nullptr)
        {
            StringCchCopyW(state->InstanceId, InstanceBufferChars, deviceInstanceId);
        }
        SetEvent(state->Event);
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI MuxSwDeviceCreate(
    _In_ PCWSTR enumeratorName,
    _In_ PCWSTR parentDeviceInstance,
    _In_ PCWSTR instanceId,
    _In_opt_ PCZZWSTR hardwareIds,
    _In_opt_ PCZZWSTR compatibleIds,
    _In_opt_ const GUID* containerId,
    _In_ ULONG capabilityFlags,
    _In_opt_ PCWSTR description,
    _In_opt_ PCWSTR location,
    _In_opt_ const GUID* propertyGuid,
    _In_ ULONG propertyPid,
    _In_ DEVPROPTYPE propertyType,
    _In_opt_ const BYTE* propertyData,
    _In_ ULONG propertyDataSize,
    _Out_ HSWDEVICE* device,
    _Out_ HRESULT* createResult,
    _Out_writes_opt_(deviceInstanceIdBufferChars) PWSTR deviceInstanceIdBuffer,
    _In_ ULONG deviceInstanceIdBufferChars,
    _In_ DWORD timeoutMs)
{
    if (enumeratorName == nullptr || parentDeviceInstance == nullptr || instanceId == nullptr ||
        device == nullptr || createResult == nullptr)
    {
        return E_INVALIDARG;
    }

    *device = nullptr;
    *createResult = E_PENDING;
    if (deviceInstanceIdBuffer != nullptr && deviceInstanceIdBufferChars > 0)
    {
        deviceInstanceIdBuffer[0] = L'\0';
    }

    SW_DEVICE_CREATE_INFO createInfo{};
    createInfo.cbSize = sizeof(createInfo);
    createInfo.pszInstanceId = instanceId;
    createInfo.pszzHardwareIds = hardwareIds;
    createInfo.pszzCompatibleIds = compatibleIds;
    createInfo.pContainerId = containerId;
    createInfo.CapabilityFlags = capabilityFlags;
    createInfo.pszDeviceDescription = description;
    createInfo.pszDeviceLocation = location;
    createInfo.pSecurityDescriptor = nullptr;

    DEVPROPERTY property{};
    const DEVPROPERTY* properties = nullptr;
    ULONG propertyCount = 0;
    if (propertyGuid != nullptr && propertyData != nullptr && propertyDataSize > 0)
    {
        property.CompKey.Key.fmtid = *propertyGuid;
        property.CompKey.Key.pid = propertyPid;
        property.CompKey.Store = DEVPROP_STORE_SYSTEM;
        property.CompKey.LocaleName = nullptr;
        property.Type = propertyType;
        property.BufferSize = propertyDataSize;
        property.Buffer = const_cast<BYTE*>(propertyData);
        properties = &property;
        propertyCount = 1;
    }

    CreateContext context{};
    context.Event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    context.Result = E_PENDING;
    context.InstanceId[0] = L'\0';
    if (context.Event == nullptr)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    HSWDEVICE nativeDevice = nullptr;
    const HRESULT initial = SwDeviceCreate(
        enumeratorName,
        parentDeviceInstance,
        &createInfo,
        propertyCount,
        properties,
        CreationCallback,
        &context,
        &nativeDevice);

    if (FAILED(initial))
    {
        CloseHandle(context.Event);
        return initial;
    }

    const DWORD wait = WaitForSingleObject(context.Event, timeoutMs);
    if (wait != WAIT_OBJECT_0)
    {
        if (nativeDevice != nullptr)
        {
            SwDeviceClose(nativeDevice);
        }
        CloseHandle(context.Event);
        return HRESULT_FROM_WIN32(wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
    }

    *device = nativeDevice;
    *createResult = context.Result;
    if (deviceInstanceIdBuffer != nullptr && deviceInstanceIdBufferChars > 0 && context.InstanceId[0] != L'\0')
    {
        StringCchCopyW(deviceInstanceIdBuffer, deviceInstanceIdBufferChars, context.InstanceId);
    }

    CloseHandle(context.Event);
    return S_OK;
}

extern "C" __declspec(dllexport) VOID WINAPI MuxSwDeviceClose(_In_opt_ HSWDEVICE device)
{
    if (device != nullptr)
    {
        SwDeviceClose(device);
    }
}
