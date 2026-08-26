#include <windows.h>
#include <swdevice.h>
#include <cstdio>

struct ProbeContext
{
    HANDLE Event;
    HRESULT CallbackResult;
};

VOID WINAPI CreationCallback(
    _In_ HSWDEVICE,
    _In_ HRESULT createResult,
    _In_opt_ PVOID context,
    _In_opt_ PCWSTR)
{
    auto* probe = static_cast<ProbeContext*>(context);
    probe->CallbackResult = createResult;
    SetEvent(probe->Event);
}

int wmain()
{
    ProbeContext context{};
    context.Event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    context.CallbackResult = E_PENDING;

    if (!context.Event)
    {
        std::printf("NATIVE_CONTROL_ERROR=CreateEvent:%lu\n", GetLastError());
        return 2;
    }

    HSWDEVICE device = nullptr;
    SW_DEVICE_CREATE_INFO createInfo{};
    const wchar_t* instanceId = L"MUXSoftwareDeviceApiProbe";
    const wchar_t* description = L"MUX Software Device API Probe";

    createInfo.cbSize = sizeof(createInfo);
    createInfo.pszInstanceId = instanceId;
    createInfo.pszDeviceDescription = description;
    createInfo.CapabilityFlags =
        SWDeviceCapabilitiesRemovable |
        SWDeviceCapabilitiesSilentInstall;

    const HRESULT initial = SwDeviceCreate(
        L"MUXSoftwareDeviceApiProbe",
        L"HTREE\\ROOT\\0",
        &createInfo,
        0,
        nullptr,
        CreationCallback,
        &context,
        &device);

    std::printf("NATIVE_INITIAL_HRESULT=0x%08lX\n", static_cast<unsigned long>(initial));

    if (SUCCEEDED(initial))
    {
        const DWORD wait = WaitForSingleObject(context.Event, 10000);
        if (wait == WAIT_OBJECT_0)
        {
            std::printf("NATIVE_CALLBACK_HRESULT=0x%08lX\n",
                static_cast<unsigned long>(context.CallbackResult));
        }
        else
        {
            std::printf("NATIVE_CALLBACK_WAIT=0x%08lX\n", wait);
        }
    }

    if (device)
    {
        SwDeviceClose(device);
    }
    CloseHandle(context.Event);

    // The workflow compares this native result against the managed probe. The native
    // control itself exits zero so an OS-level Software Device API limitation can still
    // be inspected instead of being mistaken for a compiler/test harness failure.
    return 0;
}
