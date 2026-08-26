#define INITGUID
#include "Driver.h"

using Microsoft::WRL::ComPtr;

DEFINE_DEVPROPKEY(
    DEVPKEY_MUX_VIRTUAL_CONFIG,
    0x4b3e5d11, 0x7c2a, 0x4a91,
    0x9f, 0x3e, 0x58, 0xa9, 0xd1, 0xc7, 0x2a, 0x10,
    2);

namespace
{
    IDDCX_MONITOR_MODE CreateMonitorMode(
        UINT width,
        UINT height,
        UINT refreshRate,
        IDDCX_MONITOR_MODE_ORIGIN origin)
    {
        IDDCX_MONITOR_MODE mode{};
        mode.Size = sizeof(mode);
        mode.Origin = origin;

        auto& signal = mode.MonitorVideoSignalInfo;
        signal.totalSize.cx = width;
        signal.totalSize.cy = height;
        signal.activeSize.cx = width;
        signal.activeSize.cy = height;
        signal.AdditionalSignalInfo.vSyncFreqDivider = 0;
        signal.AdditionalSignalInfo.videoStandard = 255;
        signal.vSyncFreq.Numerator = refreshRate;
        signal.vSyncFreq.Denominator = 1;
        signal.hSyncFreq.Numerator = refreshRate * height;
        signal.hSyncFreq.Denominator = 1;
        signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
        signal.pixelRate =
            static_cast<UINT64>(refreshRate) *
            static_cast<UINT64>(width) *
            static_cast<UINT64>(height);

        return mode;
    }

    IDDCX_TARGET_MODE CreateTargetMode(
        UINT width,
        UINT height,
        UINT refreshRate)
    {
        IDDCX_TARGET_MODE mode{};
        mode.Size = sizeof(mode);

        auto& signal =
            mode.TargetVideoSignalInfo.targetVideoSignalInfo;

        signal.totalSize.cx = width;
        signal.totalSize.cy = height;
        signal.activeSize.cx = width;
        signal.activeSize.cy = height;
        signal.AdditionalSignalInfo.vSyncFreqDivider = 1;
        signal.AdditionalSignalInfo.videoStandard = 255;
        signal.vSyncFreq.Numerator = refreshRate;
        signal.vSyncFreq.Denominator = 1;
        signal.hSyncFreq.Numerator = refreshRate * height;
        signal.hSyncFreq.Denominator = 1;
        signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
        signal.pixelRate =
            static_cast<UINT64>(refreshRate) *
            static_cast<UINT64>(width) *
            static_cast<UINT64>(height);

        return mode;
    }

    void DeviceCleanup(WDFOBJECT object)
    {
        auto* wrapper =
            GetDeviceContextWrapper(object);

        delete wrapper->Context;
        wrapper->Context = nullptr;
    }

    void MonitorCleanup(WDFOBJECT object)
    {
        auto* wrapper =
            GetMonitorContextWrapper(object);

        delete wrapper->Context;
        wrapper->Context = nullptr;
    }

    void SetFallbackConfiguration(
        MuxVirtualConfig& config)
    {
        ZeroMemory(&config, sizeof(config));
        config.Version = MUX_CONFIG_VERSION;
        config.MonitorCount = 1;
        config.Monitors[0].Width = 1920;
        config.Monitors[0].Height = 1080;
        config.Monitors[0].RefreshRate = 60;

        static const GUID fallbackId =
        {
            0x664b24a0, 0x5c8a, 0x4e31,
            { 0xa9, 0xb2, 0x30, 0x2c, 0x81, 0xcd, 0x0b, 0x3a }
        };

        config.Monitors[0].ContainerId = fallbackId;
    }
}

extern "C" BOOL WINAPI DllMain(
    HINSTANCE instance,
    UINT reason,
    LPVOID reserved)
{
    UNREFERENCED_PARAMETER(instance);
    UNREFERENCED_PARAMETER(reason);
    UNREFERENCED_PARAMETER(reserved);
    return TRUE;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(
    PDRIVER_OBJECT driverObject,
    PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(
        &config,
        MuxDeviceAdd);

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);

    return WdfDriverCreate(
        driverObject,
        registryPath,
        &attributes,
        &config,
        WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS MuxDeviceAdd(
    WDFDRIVER driver,
    PWDFDEVICE_INIT deviceInit)
{
    UNREFERENCED_PARAMETER(driver);

    WDF_PNPPOWER_EVENT_CALLBACKS powerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&powerCallbacks);
    powerCallbacks.EvtDeviceD0Entry = MuxDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(
        deviceInit,
        &powerCallbacks);

    IDD_CX_CLIENT_CONFIG iddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&iddConfig);

    iddConfig.EvtIddCxAdapterInitFinished =
        MuxAdapterInitFinished;
    iddConfig.EvtIddCxAdapterCommitModes =
        MuxAdapterCommitModes;
    iddConfig.EvtIddCxParseMonitorDescription =
        MuxParseMonitorDescription;
    iddConfig.EvtIddCxMonitorGetDefaultDescriptionModes =
        MuxMonitorGetDefaultModes;
    iddConfig.EvtIddCxMonitorQueryTargetModes =
        MuxMonitorQueryTargetModes;
    iddConfig.EvtIddCxMonitorAssignSwapChain =
        MuxMonitorAssignSwapChain;
    iddConfig.EvtIddCxMonitorUnassignSwapChain =
        MuxMonitorUnassignSwapChain;

    NTSTATUS status =
        IddCxDeviceInitConfig(
            deviceInit,
            &iddConfig);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &attributes,
        DeviceContextWrapper);

    attributes.EvtCleanupCallback =
        DeviceCleanup;

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(
        &deviceInit,
        &attributes,
        &device);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = IddCxDeviceInitialize(device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    auto* wrapper =
        GetDeviceContextWrapper(device);

    wrapper->Context =
        new (std::nothrow) DeviceContext(device);

    if (wrapper->Context == nullptr)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxDeviceD0Entry(
    WDFDEVICE device,
    WDF_POWER_DEVICE_STATE previousState)
{
    UNREFERENCED_PARAMETER(previousState);

    auto* wrapper =
        GetDeviceContextWrapper(device);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    wrapper->Context->InitializeAdapter();
    return STATUS_SUCCESS;
}

Direct3DDevice::Direct3DDevice(
    LUID adapterLuid)
    : m_adapterLuid(adapterLuid)
{
}

HRESULT Direct3DDevice::Initialize()
{
    HRESULT hr =
        CreateDXGIFactory2(
            0,
            IID_PPV_ARGS(&m_factory));

    if (FAILED(hr))
    {
        return hr;
    }

    hr = m_factory->EnumAdapterByLuid(
        m_adapterLuid,
        IID_PPV_ARGS(&m_adapter));

    if (FAILED(hr))
    {
        return hr;
    }

    return D3D11CreateDevice(
        m_adapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &Device,
        nullptr,
        &m_context);
}

SwapChainProcessor::SwapChainProcessor(
    IDDCX_SWAPCHAIN swapChain,
    std::shared_ptr<Direct3DDevice> device,
    HANDLE availableBufferEvent)
    : m_swapChain(swapChain),
      m_device(std::move(device)),
      m_availableBufferEvent(availableBufferEvent)
{
    m_terminateEvent =
        CreateEvent(
            nullptr,
            FALSE,
            FALSE,
            nullptr);

    if (m_terminateEvent != nullptr)
    {
        m_thread =
            CreateThread(
                nullptr,
                0,
                ThreadEntry,
                this,
                0,
                nullptr);
    }
}

SwapChainProcessor::~SwapChainProcessor()
{
    if (m_terminateEvent != nullptr)
    {
        SetEvent(m_terminateEvent);
    }

    if (m_thread != nullptr)
    {
        WaitForSingleObject(
            m_thread,
            INFINITE);
        CloseHandle(m_thread);
        m_thread = nullptr;
    }

    if (m_terminateEvent != nullptr)
    {
        CloseHandle(m_terminateEvent);
        m_terminateEvent = nullptr;
    }
}

DWORD WINAPI SwapChainProcessor::ThreadEntry(
    LPVOID argument)
{
    reinterpret_cast<SwapChainProcessor*>(argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    DWORD taskIndex = 0;
    HANDLE mmcss =
        AvSetMmThreadCharacteristicsW(
            L"Distribution",
            &taskIndex);

    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr =
        m_device->Device.As(&dxgiDevice);

    if (SUCCEEDED(hr))
    {
        IDARG_IN_SWAPCHAINSETDEVICE setDevice{};
        setDevice.pDevice = dxgiDevice.Get();

        hr = IddCxSwapChainSetDevice(
            m_swapChain,
            &setDevice);
    }

    while (SUCCEEDED(hr))
    {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer{};
        hr = IddCxSwapChainReleaseAndAcquireBuffer(
            m_swapChain,
            &buffer);

        if (hr == E_PENDING)
        {
            HANDLE waitHandles[] =
            {
                m_availableBufferEvent,
                m_terminateEvent
            };

            const DWORD wait =
                WaitForMultipleObjects(
                    ARRAYSIZE(waitHandles),
                    waitHandles,
                    FALSE,
                    16);

            if (wait == WAIT_OBJECT_0 ||
                wait == WAIT_TIMEOUT)
            {
                hr = S_OK;
                continue;
            }

            if (wait == WAIT_OBJECT_0 + 1)
            {
                break;
            }

            hr = HRESULT_FROM_WIN32(wait);
            break;
        }

        if (SUCCEEDED(hr))
        {
            ComPtr<IDXGIResource> acquired;
            acquired.Attach(buffer.MetaData.pSurface);
            acquired.Reset();

            hr =
                IddCxSwapChainFinishedProcessingFrame(
                    m_swapChain);
        }
    }

    WdfObjectDelete(
        reinterpret_cast<WDFOBJECT>(m_swapChain));

    m_swapChain = nullptr;

    if (mmcss != nullptr)
    {
        AvRevertMmThreadCharacteristics(mmcss);
    }
}

MonitorContext::MonitorContext(
    IDDCX_MONITOR monitor,
    const MuxMonitorConfig& config)
    : Monitor(monitor),
      Config(config)
{
}

MonitorContext::~MonitorContext()
{
    Processor.reset();
}

void MonitorContext::AssignSwapChain(
    IDDCX_SWAPCHAIN swapChain,
    LUID renderAdapter,
    HANDLE availableBufferEvent)
{
    Processor.reset();

    auto device =
        std::make_shared<Direct3DDevice>(
            renderAdapter);

    if (FAILED(device->Initialize()))
    {
        WdfObjectDelete(
            reinterpret_cast<WDFOBJECT>(swapChain));
        return;
    }

    Processor =
        std::make_unique<SwapChainProcessor>(
            swapChain,
            std::move(device),
            availableBufferEvent);
}

void MonitorContext::UnassignSwapChain()
{
    Processor.reset();
}

DeviceContext::DeviceContext(
    WDFDEVICE device)
    : Device(device)
{
    LoadConfiguration();
}

void DeviceContext::LoadConfiguration()
{
    SetFallbackConfiguration(Config);

    WDF_DEVICE_PROPERTY_DATA propertyData;
    WDF_DEVICE_PROPERTY_DATA_INIT(
        &propertyData,
        &DEVPKEY_MUX_VIRTUAL_CONFIG);

    ULONG requiredSize = 0;
    DEVPROPTYPE propertyType = DEVPROP_TYPE_EMPTY;
    MuxVirtualConfig incoming{};

    const NTSTATUS status =
        WdfDeviceQueryPropertyEx(
            Device,
            &propertyData,
            sizeof(incoming),
            &incoming,
            &requiredSize,
            &propertyType);

    if (!NT_SUCCESS(status) ||
        propertyType != DEVPROP_TYPE_BINARY ||
        requiredSize != sizeof(incoming) ||
        incoming.Version != MUX_CONFIG_VERSION ||
        incoming.MonitorCount == 0 ||
        incoming.MonitorCount > MUX_MAX_MONITORS)
    {
        return;
    }

    for (UINT i = 0; i < incoming.MonitorCount; ++i)
    {
        const auto& monitor =
            incoming.Monitors[i];

        if (monitor.Width < 320 ||
            monitor.Height < 200 ||
            monitor.RefreshRate < 24 ||
            monitor.RefreshRate > 240)
        {
            return;
        }
    }

    Config = incoming;
}

void DeviceContext::InitializeAdapter()
{
    IDDCX_ADAPTER_CAPS caps{};
    caps.Size = sizeof(caps);
    caps.MaxMonitorsSupported =
        Config.MonitorCount;

    caps.EndPointDiagnostics.Size =
        sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport =
        IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType =
        IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName =
        L"MUX Virtual Displays";
    caps.EndPointDiagnostics.pEndPointManufacturerName =
        L"Triple Axis Capital";
    caps.EndPointDiagnostics.pEndPointModelName =
        L"MUX Virtual Display";

    IDDCX_ENDPOINT_VERSION version{};
    version.Size = sizeof(version);
    version.MajorVer = 1;
    version.MinorVer = 0;

    caps.EndPointDiagnostics.pFirmwareVersion =
        &version;
    caps.EndPointDiagnostics.pHardwareVersion =
        &version;

    WDF_OBJECT_ATTRIBUTES adapterAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &adapterAttributes,
        DeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT input{};
    input.WdfDevice = Device;
    input.pCaps = &caps;
    input.ObjectAttributes =
        &adapterAttributes;

    IDARG_OUT_ADAPTER_INIT output{};
    const NTSTATUS status =
        IddCxAdapterInitAsync(
            &input,
            &output);

    if (!NT_SUCCESS(status))
    {
        return;
    }

    Adapter = output.AdapterObject;

    auto* wrapper =
        GetDeviceContextWrapper(
            output.AdapterObject);

    wrapper->Context = this;
}

void DeviceContext::FinishMonitor(
    UINT connectorIndex)
{
    if (connectorIndex >= Config.MonitorCount)
    {
        return;
    }

    const auto& config =
        Config.Monitors[connectorIndex];

    WDF_OBJECT_ATTRIBUTES monitorAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &monitorAttributes,
        MonitorContextWrapper);

    monitorAttributes.EvtCleanupCallback =
        MonitorCleanup;

    IDDCX_MONITOR_INFO info{};
    info.Size = sizeof(info);
    info.MonitorType =
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    info.ConnectorIndex =
        connectorIndex;
    info.MonitorContainerId =
        config.ContainerId;

    info.MonitorDescription.Size =
        sizeof(info.MonitorDescription);
    info.MonitorDescription.Type =
        IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = 0;
    info.MonitorDescription.pData = nullptr;

    IDARG_IN_MONITORCREATE input{};
    input.ObjectAttributes =
        &monitorAttributes;
    input.pMonitorInfo =
        &info;

    IDARG_OUT_MONITORCREATE output{};
    NTSTATUS status =
        IddCxMonitorCreate(
            Adapter,
            &input,
            &output);

    if (!NT_SUCCESS(status))
    {
        return;
    }

    auto* wrapper =
        GetMonitorContextWrapper(
            output.MonitorObject);

    wrapper->Context =
        new (std::nothrow) MonitorContext(
            output.MonitorObject,
            config);

    if (wrapper->Context == nullptr)
    {
        WdfObjectDelete(
            reinterpret_cast<WDFOBJECT>(
                output.MonitorObject));
        return;
    }

    IDARG_OUT_MONITORARRIVAL arrival{};
    IddCxMonitorArrival(
        output.MonitorObject,
        &arrival);
}

_Use_decl_annotations_
NTSTATUS MuxAdapterInitFinished(
    IDDCX_ADAPTER adapterObject,
    const IDARG_IN_ADAPTER_INIT_FINISHED* input)
{
    auto* wrapper =
        GetDeviceContextWrapper(
            adapterObject);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr ||
        !NT_SUCCESS(input->AdapterInitStatus))
    {
        return STATUS_SUCCESS;
    }

    for (UINT i = 0;
         i < wrapper->Context->Config.MonitorCount;
         ++i)
    {
        wrapper->Context->FinishMonitor(i);
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxAdapterCommitModes(
    IDDCX_ADAPTER adapterObject,
    const IDARG_IN_COMMITMODES* input)
{
    UNREFERENCED_PARAMETER(adapterObject);
    UNREFERENCED_PARAMETER(input);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxParseMonitorDescription(
    const IDARG_IN_PARSEMONITORDESCRIPTION* input,
    IDARG_OUT_PARSEMONITORDESCRIPTION* output)
{
    UNREFERENCED_PARAMETER(input);
    UNREFERENCED_PARAMETER(output);

    // MUX uses EDID-less software monitors. Their exact mode is supplied by
    // MuxMonitorGetDefaultModes below.
    return STATUS_INVALID_PARAMETER;
}

_Use_decl_annotations_
NTSTATUS MuxMonitorGetDefaultModes(
    IDDCX_MONITOR monitorObject,
    const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* input,
    IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* output)
{
    auto* wrapper =
        GetMonitorContextWrapper(
            monitorObject);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    output->DefaultMonitorModeBufferOutputCount = 1;

    if (input->DefaultMonitorModeBufferInputCount == 0)
    {
        output->PreferredMonitorModeIdx = 0;
        return STATUS_SUCCESS;
    }

    if (input->DefaultMonitorModeBufferInputCount < 1)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    const auto& config =
        wrapper->Context->Config;

    input->pDefaultMonitorModes[0] =
        CreateMonitorMode(
            config.Width,
            config.Height,
            config.RefreshRate,
            IDDCX_MONITOR_MODE_ORIGIN_DRIVER);

    output->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxMonitorQueryTargetModes(
    IDDCX_MONITOR monitorObject,
    const IDARG_IN_QUERYTARGETMODES* input,
    IDARG_OUT_QUERYTARGETMODES* output)
{
    auto* wrapper =
        GetMonitorContextWrapper(
            monitorObject);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    output->TargetModeBufferOutputCount = 1;

    if (input->TargetModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (input->TargetModeBufferInputCount < 1)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    const auto& config =
        wrapper->Context->Config;

    input->pTargetModes[0] =
        CreateTargetMode(
            config.Width,
            config.Height,
            config.RefreshRate);

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxMonitorAssignSwapChain(
    IDDCX_MONITOR monitorObject,
    const IDARG_IN_SETSWAPCHAIN* input)
{
    auto* wrapper =
        GetMonitorContextWrapper(
            monitorObject);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    wrapper->Context->AssignSwapChain(
        input->hSwapChain,
        input->RenderAdapterLuid,
        input->hNextSurfaceAvailable);

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS MuxMonitorUnassignSwapChain(
    IDDCX_MONITOR monitorObject)
{
    auto* wrapper =
        GetMonitorContextWrapper(
            monitorObject);

    if (wrapper == nullptr ||
        wrapper->Context == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    wrapper->Context->UnassignSwapChain();
    return STATUS_SUCCESS;
}
