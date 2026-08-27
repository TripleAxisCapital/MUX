#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <swdevice.h>
#include <devpropdef.h>
#include <strsafe.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

#pragma comment(lib, "swdevice.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

namespace
{
    constexpr size_t InstanceBufferChars = 512;
    constexpr wchar_t PortalClassName[] = L"MUXVirtualFramePortal";
    constexpr wchar_t CursorClassName[] = L"MUXVirtualFrameCursor";
    constexpr COLORREF CursorColorKey = RGB(1, 2, 3);

#pragma pack(push, 4)
    struct MuxPortalDescriptor
    {
        GUID MonitorId;
        LONG HostLeft;
        LONG HostTop;
        UINT Width;
        UINT Height;
        LONG VirtualLeft;
        LONG VirtualTop;
    };
#pragma pack(pop)

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

    std::wstring SharedFrameName(const GUID& id)
    {
        wchar_t buffer[128]{};
        swprintf_s(
            buffer,
            L"Global\\MUX.Virtual.Frame.%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X",
            id.Data1,
            id.Data2,
            id.Data3,
            id.Data4[0], id.Data4[1],
            id.Data4[2], id.Data4[3], id.Data4[4], id.Data4[5], id.Data4[6], id.Data4[7]);
        return buffer;
    }

    LRESULT CALLBACK PortalWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        UNREFERENCED_PARAMETER(wParam);
        UNREFERENCED_PARAMETER(lParam);

        switch (message)
        {
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_ERASEBKGND:
            return 1;
        case WM_PAINT:
        {
            PAINTSTRUCT ps{};
            HDC dc = BeginPaint(hwnd, &ps);
            RECT rect{};
            GetClientRect(hwnd, &rect);
            FillRect(dc, &rect, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(145, 145, 145));
            DrawTextW(
                dc,
                L"MUX Virtual - connecting live display...",
                -1,
                &rect,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            EndPaint(hwnd, &ps);
            return 0;
        }
        default:
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    LRESULT CALLBACK CursorWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        UNREFERENCED_PARAMETER(wParam);
        UNREFERENCED_PARAMETER(lParam);

        switch (message)
        {
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_ERASEBKGND:
            return 1;
        case WM_PAINT:
        {
            PAINTSTRUCT ps{};
            HDC dc = BeginPaint(hwnd, &ps);
            RECT rect{};
            GetClientRect(hwnd, &rect);
            HBRUSH keyBrush = CreateSolidBrush(CursorColorKey);
            if (keyBrush != nullptr)
            {
                FillRect(dc, &rect, keyBrush);
                DeleteObject(keyBrush);
            }
            HCURSOR cursor = LoadCursorW(nullptr, IDC_ARROW);
            if (cursor != nullptr)
            {
                DrawIconEx(dc, 0, 0, cursor, 32, 32, 0, nullptr, DI_NORMAL);
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        default:
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    bool RegisterPortalClasses()
    {
        HINSTANCE instance = GetModuleHandleW(L"MUX.SwDeviceBridge.dll");
        if (instance == nullptr)
        {
            return false;
        }

        WNDCLASSEXW portalClass{};
        portalClass.cbSize = sizeof(portalClass);
        portalClass.lpfnWndProc = PortalWndProc;
        portalClass.hInstance = instance;
        portalClass.hCursor = nullptr;
        portalClass.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        portalClass.lpszClassName = PortalClassName;

        if (RegisterClassExW(&portalClass) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            return false;
        }

        WNDCLASSEXW cursorClass{};
        cursorClass.cbSize = sizeof(cursorClass);
        cursorClass.lpfnWndProc = CursorWndProc;
        cursorClass.hInstance = instance;
        cursorClass.hCursor = nullptr;
        cursorClass.lpszClassName = CursorClassName;

        if (RegisterClassExW(&cursorClass) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            return false;
        }

        return true;
    }

    struct PortalState
    {
        explicit PortalState(const MuxPortalDescriptor& descriptor)
            : Descriptor(descriptor),
              SharedName(SharedFrameName(descriptor.MonitorId))
        {
        }

        MuxPortalDescriptor Descriptor{};
        std::wstring SharedName;
        HWND Host = nullptr;
        HWND Cursor = nullptr;
        ComPtr<IDXGIFactory2> Factory;
        ComPtr<ID3D11Device> Device;
        ComPtr<ID3D11Device1> Device1;
        ComPtr<ID3D11DeviceContext> Context;
        ComPtr<ID3D11Texture2D> Source;
        ComPtr<IDXGIKeyedMutex> Mutex;
        ComPtr<IDXGISwapChain1> SwapChain;
        bool Connected = false;
    };

    class PortalCompositor
    {
    public:
        PortalCompositor(const MuxPortalDescriptor* descriptors, UINT count)
        {
            m_portals.reserve(count);
            for (UINT i = 0; i < count; ++i)
            {
                m_portals.push_back(std::make_unique<PortalState>(descriptors[i]));
            }
        }

        ~PortalCompositor()
        {
            Stop();
        }

        HRESULT Start()
        {
            if (m_thread != nullptr)
            {
                return S_FALSE;
            }

            m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (m_stopEvent == nullptr)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }

            m_thread = CreateThread(nullptr, 0, ThreadEntry, this, 0, nullptr);
            if (m_thread == nullptr)
            {
                const HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
                CloseHandle(m_stopEvent);
                m_stopEvent = nullptr;
                return hr;
            }

            return S_OK;
        }

        void Stop()
        {
            if (m_stopEvent != nullptr)
            {
                SetEvent(m_stopEvent);
            }

            if (m_thread != nullptr)
            {
                WaitForSingleObject(m_thread, INFINITE);
                CloseHandle(m_thread);
                m_thread = nullptr;
            }

            if (m_stopEvent != nullptr)
            {
                CloseHandle(m_stopEvent);
                m_stopEvent = nullptr;
            }
        }

        UINT ConnectedCount() const
        {
            return m_connectedCount.load();
        }

    private:
        static DWORD WINAPI ThreadEntry(LPVOID context)
        {
            auto* self = static_cast<PortalCompositor*>(context);
            self->Run();
            return 0;
        }

        void Run()
        {
            if (!RegisterPortalClasses())
            {
                return;
            }

            HINSTANCE instance = GetModuleHandleW(L"MUX.SwDeviceBridge.dll");
            if (instance == nullptr)
            {
                return;
            }

            for (auto& portal : m_portals)
            {
                portal->Host = CreateWindowExW(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                    PortalClassName,
                    L"",
                    WS_POPUP | WS_VISIBLE,
                    portal->Descriptor.HostLeft,
                    portal->Descriptor.HostTop,
                    static_cast<int>(portal->Descriptor.Width),
                    static_cast<int>(portal->Descriptor.Height),
                    nullptr,
                    nullptr,
                    instance,
                    nullptr);

                if (portal->Host != nullptr)
                {
                    SetWindowPos(
                        portal->Host,
                        HWND_TOPMOST,
                        portal->Descriptor.HostLeft,
                        portal->Descriptor.HostTop,
                        static_cast<int>(portal->Descriptor.Width),
                        static_cast<int>(portal->Descriptor.Height),
                        SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }

                portal->Cursor = CreateWindowExW(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED,
                    CursorClassName,
                    L"",
                    WS_POPUP,
                    0,
                    0,
                    32,
                    32,
                    nullptr,
                    nullptr,
                    instance,
                    nullptr);

                if (portal->Cursor != nullptr)
                {
                    SetLayeredWindowAttributes(portal->Cursor, CursorColorKey, 255, LWA_COLORKEY);
                }
            }

            MSG message{};
            while (WaitForSingleObject(m_stopEvent, 0) == WAIT_TIMEOUT)
            {
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }

                for (auto& portal : m_portals)
                {
                    Render(*portal);
                    UpdateCursor(*portal);
                }

                WaitForSingleObject(m_stopEvent, 8);
            }

            for (auto& portal : m_portals)
            {
                Disconnect(*portal);
                if (portal->Cursor != nullptr)
                {
                    DestroyWindow(portal->Cursor);
                    portal->Cursor = nullptr;
                }
                if (portal->Host != nullptr)
                {
                    DestroyWindow(portal->Host);
                    portal->Host = nullptr;
                }
            }
        }

        static DXGI_FORMAT DisplayFormat(DXGI_FORMAT format)
        {
            if (format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
            {
                return DXGI_FORMAT_B8G8R8A8_UNORM;
            }
            if (format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
            {
                return DXGI_FORMAT_R8G8B8A8_UNORM;
            }
            return format;
        }

        bool TryConnect(PortalState& portal)
        {
            if (portal.Host == nullptr)
            {
                return false;
            }

            ComPtr<IDXGIFactory2> factory;
            HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
            if (FAILED(hr))
            {
                return false;
            }

            for (UINT adapterIndex = 0; ; ++adapterIndex)
            {
                ComPtr<IDXGIAdapter1> adapter;
                hr = factory->EnumAdapters1(adapterIndex, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }
                if (FAILED(hr))
                {
                    continue;
                }

                ComPtr<ID3D11Device> device;
                ComPtr<ID3D11DeviceContext> context;
                D3D_FEATURE_LEVEL featureLevel{};
                hr = D3D11CreateDevice(
                    adapter.Get(),
                    D3D_DRIVER_TYPE_UNKNOWN,
                    nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    nullptr,
                    0,
                    D3D11_SDK_VERSION,
                    &device,
                    &featureLevel,
                    &context);
                if (FAILED(hr))
                {
                    continue;
                }

                ComPtr<ID3D11Device1> device1;
                hr = device.As(&device1);
                if (FAILED(hr))
                {
                    continue;
                }

                ComPtr<ID3D11Texture2D> source;
                hr = device1->OpenSharedResourceByName(
                    portal.SharedName.c_str(),
                    DXGI_SHARED_RESOURCE_READ,
                    IID_PPV_ARGS(&source));
                if (FAILED(hr))
                {
                    continue;
                }

                ComPtr<IDXGIKeyedMutex> keyedMutex;
                hr = source.As(&keyedMutex);
                if (FAILED(hr))
                {
                    continue;
                }

                D3D11_TEXTURE2D_DESC sourceDesc{};
                source->GetDesc(&sourceDesc);

                DXGI_SWAP_CHAIN_DESC1 swapDesc{};
                swapDesc.Width = portal.Descriptor.Width;
                swapDesc.Height = portal.Descriptor.Height;
                swapDesc.Format = DisplayFormat(sourceDesc.Format);
                swapDesc.Stereo = FALSE;
                swapDesc.SampleDesc.Count = 1;
                swapDesc.SampleDesc.Quality = 0;
                swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                swapDesc.BufferCount = 2;
                swapDesc.Scaling = DXGI_SCALING_STRETCH;
                swapDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                swapDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
                swapDesc.Flags = 0;

                ComPtr<IDXGISwapChain1> swapChain;
                hr = factory->CreateSwapChainForHwnd(
                    device.Get(),
                    portal.Host,
                    &swapDesc,
                    nullptr,
                    nullptr,
                    &swapChain);
                if (FAILED(hr))
                {
                    continue;
                }

                factory->MakeWindowAssociation(
                    portal.Host,
                    DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);

                portal.Factory = std::move(factory);
                portal.Device = std::move(device);
                portal.Device1 = std::move(device1);
                portal.Context = std::move(context);
                portal.Source = std::move(source);
                portal.Mutex = std::move(keyedMutex);
                portal.SwapChain = std::move(swapChain);
                portal.Connected = true;
                m_connectedCount.fetch_add(1);
                return true;
            }

            return false;
        }

        void Disconnect(PortalState& portal)
        {
            if (portal.Connected)
            {
                portal.Connected = false;
                m_connectedCount.fetch_sub(1);
            }
            portal.SwapChain.Reset();
            portal.Mutex.Reset();
            portal.Source.Reset();
            portal.Context.Reset();
            portal.Device1.Reset();
            portal.Device.Reset();
            portal.Factory.Reset();
        }

        void Render(PortalState& portal)
        {
            if (!portal.Connected)
            {
                TryConnect(portal);
                return;
            }

            const HRESULT lockResult = portal.Mutex->AcquireSync(1, 0);
            if (lockResult == WAIT_TIMEOUT || lockResult == DXGI_ERROR_WAIT_TIMEOUT)
            {
                return;
            }
            if (FAILED(lockResult))
            {
                Disconnect(portal);
                return;
            }

            ComPtr<ID3D11Texture2D> backBuffer;
            HRESULT hr = portal.SwapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (SUCCEEDED(hr))
            {
                portal.Context->CopyResource(backBuffer.Get(), portal.Source.Get());
            }

            const HRESULT releaseResult = portal.Mutex->ReleaseSync(0);
            if (FAILED(hr) || FAILED(releaseResult))
            {
                Disconnect(portal);
                return;
            }

            hr = portal.SwapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                return;
            }
            if (FAILED(hr))
            {
                Disconnect(portal);
            }
        }

        static void UpdateCursor(PortalState& portal)
        {
            if (portal.Cursor == nullptr)
            {
                return;
            }

            POINT point{};
            if (!GetCursorPos(&point))
            {
                ShowWindow(portal.Cursor, SW_HIDE);
                return;
            }

            const LONG virtualRight =
                portal.Descriptor.VirtualLeft + static_cast<LONG>(portal.Descriptor.Width);
            const LONG virtualBottom =
                portal.Descriptor.VirtualTop + static_cast<LONG>(portal.Descriptor.Height);

            const bool inside =
                point.x >= portal.Descriptor.VirtualLeft &&
                point.x < virtualRight &&
                point.y >= portal.Descriptor.VirtualTop &&
                point.y < virtualBottom;

            if (!inside)
            {
                ShowWindow(portal.Cursor, SW_HIDE);
                return;
            }

            const int x =
                portal.Descriptor.HostLeft +
                (point.x - portal.Descriptor.VirtualLeft);
            const int y =
                portal.Descriptor.HostTop +
                (point.y - portal.Descriptor.VirtualTop);

            SetWindowPos(
                portal.Cursor,
                HWND_TOPMOST,
                x,
                y,
                32,
                32,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(portal.Cursor, nullptr, FALSE);
        }

        std::vector<std::unique_ptr<PortalState>> m_portals;
        HANDLE m_stopEvent = nullptr;
        HANDLE m_thread = nullptr;
        std::atomic<UINT> m_connectedCount{ 0 };
    };

    std::mutex g_portalMutex;
    std::unique_ptr<PortalCompositor> g_portalCompositor;
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

extern "C" __declspec(dllexport) HRESULT WINAPI MuxPortalStart(
    _In_reads_(count) const MuxPortalDescriptor* descriptors,
    _In_ UINT count)
{
    if (descriptors == nullptr || count == 0 || count > 8)
    {
        return E_INVALIDARG;
    }

    std::lock_guard<std::mutex> lock(g_portalMutex);
    if (g_portalCompositor)
    {
        g_portalCompositor->Stop();
        g_portalCompositor.reset();
    }

    auto compositor = std::make_unique<PortalCompositor>(descriptors, count);
    const HRESULT hr = compositor->Start();
    if (FAILED(hr))
    {
        return hr;
    }

    g_portalCompositor = std::move(compositor);
    return S_OK;
}

extern "C" __declspec(dllexport) UINT WINAPI MuxPortalConnectedCount()
{
    std::lock_guard<std::mutex> lock(g_portalMutex);
    return g_portalCompositor ? g_portalCompositor->ConnectedCount() : 0;
}

extern "C" __declspec(dllexport) VOID WINAPI MuxPortalStop()
{
    std::lock_guard<std::mutex> lock(g_portalMutex);
    if (g_portalCompositor)
    {
        g_portalCompositor->Stop();
        g_portalCompositor.reset();
    }
}
