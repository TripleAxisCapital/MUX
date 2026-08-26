#pragma once

#include <windows.h>
#include <wdf.h>
#include <iddcx.h>
#include <d3d11.h>
#include <dxgi1_5.h>
#include <avrt.h>
#include <wrl.h>
#include <devpropdef.h>

#include <algorithm>
#include <memory>
#include <new>
#include <vector>
#include <utility>

constexpr UINT MUX_MAX_MONITORS = 8;
constexpr UINT MUX_CONFIG_VERSION = 1;

#pragma pack(push, 1)
struct MuxMonitorConfig
{
    UINT Width;
    UINT Height;
    UINT RefreshRate;
    GUID ContainerId;
};

struct MuxVirtualConfig
{
    UINT Version;
    UINT MonitorCount;
    MuxMonitorConfig Monitors[MUX_MAX_MONITORS];
};
#pragma pack(pop)

struct DeviceContext;
struct MonitorContext;

struct DeviceContextWrapper
{
    DeviceContext* Context;
};

struct MonitorContextWrapper
{
    MonitorContext* Context;
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceContextWrapper, GetDeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorContextWrapper, GetMonitorContextWrapper);

inline void MuxMonitorArrivalChecked(
    IDDCX_MONITOR monitor,
    IDARG_OUT_MONITORARRIVAL* arrival)
{
    const NTSTATUS status = IddCxMonitorArrival(monitor, arrival);
    if (!NT_SUCCESS(status))
    {
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(monitor));
    }
}

#define IddCxMonitorArrival(monitor, arrival) \
    MuxMonitorArrivalChecked((monitor), (arrival))

class Direct3DDevice
{
public:
    explicit Direct3DDevice(LUID adapterLuid);
    HRESULT Initialize();

    Microsoft::WRL::ComPtr<ID3D11Device> Device;

private:
    LUID m_adapterLuid{};
    Microsoft::WRL::ComPtr<IDXGIFactory4> m_factory;
    Microsoft::WRL::ComPtr<IDXGIAdapter> m_adapter;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_context;
};

class SwapChainProcessor
{
public:
    SwapChainProcessor(
        IDDCX_SWAPCHAIN swapChain,
        std::shared_ptr<Direct3DDevice> device,
        HANDLE availableBufferEvent);

    ~SwapChainProcessor();

private:
    static DWORD WINAPI ThreadEntry(LPVOID argument);
    void Run();

    IDDCX_SWAPCHAIN m_swapChain = nullptr;
    std::shared_ptr<Direct3DDevice> m_device;
    HANDLE m_availableBufferEvent = nullptr;
    HANDLE m_terminateEvent = nullptr;
    HANDLE m_thread = nullptr;
};

struct MonitorContext
{
    MonitorContext(
        IDDCX_MONITOR monitor,
        const MuxMonitorConfig& config);

    ~MonitorContext();

    void AssignSwapChain(
        IDDCX_SWAPCHAIN swapChain,
        LUID renderAdapter,
        HANDLE availableBufferEvent);

    void UnassignSwapChain();

    IDDCX_MONITOR Monitor = nullptr;
    MuxMonitorConfig Config{};
    std::unique_ptr<SwapChainProcessor> Processor;
};

struct DeviceContext
{
    explicit DeviceContext(WDFDEVICE device);

    void InitializeAdapter();
    void FinishMonitor(UINT connectorIndex);
    void LoadConfiguration();

    WDFDEVICE Device = nullptr;
    IDDCX_ADAPTER Adapter = nullptr;
    MuxVirtualConfig Config{};
};

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD MuxDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY MuxDeviceD0Entry;

EVT_IDD_CX_ADAPTER_INIT_FINISHED MuxAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES MuxAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION MuxParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES MuxMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES MuxMonitorQueryTargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN MuxMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN MuxMonitorUnassignSwapChain;
