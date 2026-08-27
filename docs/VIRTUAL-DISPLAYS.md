# MUX Virtual Displays

MUX Virtual Displays is the **Option B** edition of MUX. It keeps MUX Standard intact and adds a second Windows application backed by a real Indirect Display Driver (IddCx).

## What changes

MUX Standard manages normal Windows application windows inside regions on one physical display.

MUX Virtual Displays creates actual Windows display targets. Each saved MUX zone becomes a monitor with its own Windows display mode. Applications moved onto that monitor ask Windows for the monitor bounds and receive the MUX monitor resolution.

That means normal Windows monitor semantics apply:

- **Maximize** uses the MUX virtual monitor bounds.
- Browser **F11** uses the MUX virtual monitor bounds.
- `MonitorFromWindow`, `GetMonitorInfo`, display topology APIs, and normal per-monitor DPI logic see a real monitor.
- Applications no longer need Standard's maximize/fullscreen interception to understand MUX monitor boundaries.

## Live compositor architecture

Windows does not allow two desktop display sources to occupy overlapping desktop coordinates. MUX therefore places the real virtual monitors in a contiguous desktop strip outside the physical desktop and creates a click-through portal over each saved MUX rectangle.

The portal is **not a screenshot and no longer uses the Magnification API**. The IddCx driver consumes each live DWM swap-chain frame and copies it on the render GPU into a named D3D11 shared texture. `MUX.SwDeviceBridge.dll` opens the matching texture from the controller process and presents it into the physical portal with a DXGI swap chain.

```text
Windows / DWM
    ↓
IddCx swap chain for MUX monitor
    ↓
MUXVirtualDisplay.dll
    ↓  D3D11 CopyResource + keyed mutex
named shared GPU texture
    ↓
MUX.SwDeviceBridge.dll
    ↓  DXGI swap chain / Present
physical MUX portal
```

Each frame channel is keyed by the saved MUX zone GUID, so the video shown in a physical portal is always paired with the same Windows virtual monitor used for input.

The controller waits for every configured live frame channel to connect during activation. If Windows creates the monitors but a frame channel does not become available, activation fails visibly instead of leaving a frozen portal on screen.

## Cursor portal

When the real pointer enters a physical MUX portal, MUX maps it to the same relative coordinate on the corresponding off-screen Windows monitor. From that point, Windows receives normal mouse input on the true virtual monitor, so clicks, dragging, focus, keyboard input, maximize, and fullscreen behavior apply to the real application on that monitor.

Because the system pointer is physically off-screen while it is operating a virtual monitor, the native compositor draws a matching cursor overlay inside the physical portal.

When the pointer crosses out of a virtual monitor, MUX maps it back to the corresponding physical portal edge. Press:

```text
Ctrl + Alt + Esc
```

to force-release the pointer back to the physical desktop at any time.

Stopping MUX Virtual closes the software-device handle. Windows then removes the temporary virtual display adapter and its monitors.

## Layouts and editor

Both editions deliberately share:

```text
%LOCALAPPDATA%\MUX\state.json
```

MUX Virtual reuses the mature Standard editor components and `MUX.Core` geometry rather than maintaining a second independent sizing implementation. Physical display selection, diagonal/calibration, layouts, monitor add/edit/remove/clone, aspect-ratio controls, edit-on-display, and configurable shortcuts stay consistent between the editions.

Each zone is converted to the exact pixel rectangle required on the calibrated physical panel, and that exact resolution becomes the preferred mode of the true Windows virtual monitor.

## Driver architecture

```text
MUX.Virtual.exe
  ├─ Standard-style layout/editor controls
  ├─ MUX.Core physical geometry
  ├─ driver setup / certificate flow
  ├─ SwDevice virtual-adapter lifecycle
  ├─ Windows display topology
  ├─ live frame compositor control
  └─ mouse portal

MUX.SwDeviceBridge.dll
  ├─ native SwDeviceCreate ABI bridge
  ├─ D3D11 shared-frame receiver
  ├─ DXGI portal presentation
  └─ virtual-cursor overlay

MUXVirtualDisplay.dll
  ├─ UMDF 2 Indirect Display Driver
  ├─ IddCx adapter
  ├─ up to 8 monitor connectors
  ├─ exact preferred mode per MUX monitor
  ├─ IddCx swap-chain consumer
  └─ named D3D11 shared-frame publisher
```

The software device is deliberately temporary. If the controller exits, the adapter disappears rather than leaving phantom displays attached to Windows.

## Build

Requirements:

- Windows 11 recommended
- .NET 10 SDK
- Visual Studio 2026 C++ build tools
- `nuget.exe`
- Windows Driver Kit supplied through the pinned Microsoft WDK NuGet packages

Run:

```powershell
./scripts/build-virtual.ps1
```

Output:

```text
artifacts/MUX-Virtual-win-x64.zip
```

The ZIP contains the self-contained controller, `MUX.SwDeviceBridge.dll`, the complete driver package, the public development test certificate, and this setup document.

GitHub Actions validates the controller launch, compiles the native bridge, builds the IddCx driver with warnings-as-errors/static analysis, validates package contents, stages the test driver on a Windows runner, and executes the real software-device creation path before refreshing the rolling release.

## Driver signing

The repository can compile, test-sign, validate, and package the driver in CI. A frictionless retail Windows build still requires the completed driver package to be submitted through Microsoft's Hardware/Partner Center signing process.

The rolling GitHub build is a **development/test-signed package**. When Windows reports that the catalog chain terminates in an untrusted root, selecting **Install driver** in MUX offers to add the included public build certificate to Local Computer **Trusted Root Certification Authorities** and **Trusted Publishers**, then retries `pnputil`.

The private signing key is never included in the ZIP.

Some Windows configurations can still require **Test Mode** to load test-signed driver code. MUX does not disable Secure Boot, change BCD boot security, or reboot the computer automatically. Use Microsoft production signing for normal public distribution.

## First use

1. Download **MUX Virtual Displays**.
2. Extract the ZIP into a fresh folder.
3. Start `MUX.Virtual.exe` as administrator.
4. Complete the driver setup prompt if required.
5. Select or create the MUX layout you want.
6. Select **Activate virtual displays**.
7. Wait for MUX to confirm the live portals are connected.
8. Move the pointer into a portal and use the Windows desktop shown there normally.
9. Maximize or press F11; Windows constrains the application to that true virtual monitor.
10. Press `Ctrl + Alt + Esc` whenever you want to force-release the pointer.
11. Select **Stop** to remove the temporary virtual monitors.

## Current scope

This is the native true-monitor architecture, not another maximize hook. The runtime path is now:

**real Windows monitor semantics + live IddCx frames + GPU portal presentation + mapped input.**
