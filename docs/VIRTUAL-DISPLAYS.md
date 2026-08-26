# MUX Virtual Displays

MUX Virtual Displays is the **Option B** edition of MUX. It keeps the existing MUX Standard application intact and adds a second Windows application backed by a real Indirect Display Driver (IddCx).

## What changes

MUX Standard manages normal Windows application windows inside regions on one physical display.

MUX Virtual Displays creates actual Windows display targets. Each saved MUX zone becomes a monitor with its own Windows display mode. Applications moved onto that monitor ask Windows for the monitor bounds and receive the MUX monitor resolution.

That means normal Windows monitor semantics apply:

- **Maximize** uses the MUX virtual monitor bounds.
- Browser **F11** uses the MUX virtual monitor bounds.
- `MonitorFromWindow`, `GetMonitorInfo`, display topology APIs, and normal per-monitor DPI logic see a real monitor.
- Applications no longer need the Standard edition's maximize/fullscreen interception to understand MUX monitor boundaries.

## Why the compositor exists

Windows does not allow two desktop display sources to occupy overlapping desktop coordinates. A true virtual monitor therefore cannot occupy the exact same desktop coordinates as the physical panel.

MUX solves that by placing the real virtual monitors in a contiguous desktop strip outside the physical desktop and creating a click-through compositor portal over each saved MUX rectangle. The portal displays the corresponding virtual monitor at 1:1 pixel scale.

The result is:

```text
Physical display
┌─────────────────────────────────────────────────────┐
│  MUX portal A          MUX portal B                 │
│  ┌──────────────┐      ┌──────────────┐             │
│  │ real virtual │      │ real virtual │             │
│  │ monitor A    │      │ monitor B    │             │
│  └──────────────┘      └──────────────┘             │
└─────────────────────────────────────────────────────┘

Windows desktop topology
[ physical display ][ virtual A ][ virtual B ]...
```

The compositor uses the Windows Magnification API as a DWM-backed desktop surface view. MUX does not screenshot the desktop on the CPU.

## Cursor portal

When the cursor enters a visible MUX portal, MUX maps the pointer to the same relative coordinate on the real virtual monitor. A window being dragged follows the pointer onto that Windows monitor.

Press:

```text
Ctrl + Alt + Esc
```

to release the pointer back to the physical display and bring the MUX Virtual controller forward.

Stopping MUX Virtual closes the software-device handle. Windows then removes the temporary virtual display adapter and its monitors.

## Layouts

The two editions deliberately share:

```text
%LOCALAPPDATA%\MUX\state.json
```

Create and physically calibrate layouts in MUX Standard. MUX Virtual reads the same display profile, physical diagonal, calibration scale, monitor sizes, and monitor positions, then converts each zone to its exact virtual display resolution using `MUX.Core`.

This keeps physical sizing identical between both editions.

## Driver architecture

```text
MUX.Virtual.exe
  ├─ Reads MUX Standard layout state
  ├─ Converts zones from inches → exact pixels
  ├─ Enumerates a temporary software device with SwDeviceCreate
  ├─ Supplies per-monitor resolution configuration as a custom device property
  ├─ Places virtual monitors outside the physical desktop
  ├─ Creates 1:1 compositor portals
  └─ Runs the mouse portal

MUXVirtualDisplay.dll
  ├─ UMDF 2 Indirect Display Driver
  ├─ IddCx adapter
  ├─ Up to 8 monitor connectors
  ├─ One exact preferred mode per MUX monitor
  └─ D3D11 swap-chain consumer
```

The device is deliberately temporary. If the controller exits, the software device disappears rather than leaving phantom displays attached to Windows.

## Build

### Controller + driver package

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

The ZIP contains:

```text
MUX.Virtual.exe
Driver/
  MUXVirtualDisplay.inf
  MUXVirtualDisplay.dll
  MUXVirtualDisplay.cat
  MUXVirtualDisplay-TestCertificate.cer
VIRTUAL-DISPLAYS.md
```

`MUXVirtualDisplay-TestCertificate.cer` contains only the public certificate from the test signer used for that build. The private signing key is never included in the package. GitHub Actions verifies this before publishing.

GitHub Actions also expands the finished ZIP before publishing and fails the build unless the controller executable, driver DLL, INF, catalog, public test certificate, and setup documentation are all present. This makes the rolling download a validated complete package rather than merely a successful compilation artifact.

## Driver signing

The repository can compile and package the driver in CI. A public retail Windows build still requires the completed driver package to be submitted through Microsoft's Hardware/Partner Center signing process.

The rolling GitHub build is a **development/test-signed package**. When Windows reports that the catalog chain terminates in an untrusted root, selecting **Install driver** in MUX offers to add the included public build certificate to the Local Computer **Trusted Root Certification Authorities** and **Trusted Publishers** stores, then automatically retries `pnputil`.

Only accept that prompt on a machine you control and only for a MUX package you obtained from the official repository. Trusting a certificate changes the machine trust configuration.

Some Windows configurations can still require **Test Mode** to load test-signed driver code. MUX deliberately does not disable Secure Boot, change BCD boot security, or reboot the computer automatically. If the trusted development package is still rejected, use a Microsoft production-signed driver for normal installation or configure a dedicated engineering/test machine according to Microsoft's driver-testing guidance.

Do not work around production distribution by permanently disabling Secure Boot or driver-signing enforcement on end-user machines.

## First use

1. Download **MUX Virtual Displays**.
2. Extract the ZIP.
3. Start `MUX.Virtual.exe` as administrator.
4. Select **Install driver**.
5. If prompted for the development signing certificate, review the warning and choose whether to trust the included public certificate on this machine. MUX retries the install automatically after trust is established.
6. Select the MUX layout you want.
7. Select **Activate virtual displays**.
8. Drag a normal application into one of the MUX portals.
9. Maximize it or press F11. Windows now treats that application as being on the corresponding real virtual monitor.
10. Press `Ctrl + Alt + Esc` whenever you want to return the pointer to the physical desktop.
11. Select **Stop** to remove the temporary virtual monitors.

## Current scope

This is the native virtual-monitor architecture, not another maximize hook.

The first implementation intentionally keeps the existing Standard layout editor as the source of truth for physical sizing and layout editing. That prevents two independent geometry implementations from drifting apart and keeps both downloads interoperable.

Future work can move the same editor UI directly into the Virtual edition without changing the driver protocol or `MUX.Core` geometry.
