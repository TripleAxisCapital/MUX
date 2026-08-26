<div align="center">
  <img src="./docs/assets/mux-logo.png" width="220" alt="MUX logo" />

  # MUX

  **One display. Many.**

  Turn a large Windows display into monitor-sized workspaces measured in real physical inches.

  <br />

  <table>
    <tr>
      <td align="center">
        <strong>MUX Standard</strong><br/>
        <sub>Lightweight managed-monitor architecture</sub><br/><br/>
        <a href="https://github.com/TripleAxisCapital/MUX/releases/download/latest-build/MUX-win-x64.zip">
          <img src="docs/assets/download-windows.svg" width="300" alt="Download MUX Standard for Windows" />
        </a>
      </td>
      <td align="center">
        <strong>MUX Virtual Displays</strong><br/>
        <sub>True Windows virtual-monitor architecture</sub><br/><br/>
        <a href="https://github.com/TripleAxisCapital/MUX/releases/download/latest-build/MUX-Virtual-win-x64.zip">
          <img src="docs/assets/download-virtual-windows.svg" width="340" alt="Download MUX Virtual Displays for Windows" />
        </a>
      </td>
    </tr>
  </table>

  <sub>Windows 10/11 · x64 · self-contained controller apps</sub>
  <br />
  <sub><a href="https://github.com/TripleAxisCapital/MUX/releases/tag/latest-build">View latest build details</a></sub>

  <br /><br />
  <img src="docs/assets/mux-hero.svg" alt="MUX arranging several virtual monitor workspaces inside one large physical display" />
</div>

## Two MUX editions

MUX now ships as **two independent Windows downloads**. The original program remains available and unchanged in purpose; the second edition adds the deeper Option B architecture.

| | MUX Standard | MUX Virtual Displays |
|---|---|---|
| Architecture | Managed monitor regions | Windows Indirect Display Driver (IddCx) |
| Windows sees each MUX box as a monitor | No | **Yes** |
| Normal maximize | MUX intercepts and constrains it | **Windows handles it naturally** |
| Browser F11 / fullscreen | Compatibility layer | **Native monitor fullscreen** |
| Installation | Simple app | App + display driver |
| Best for | Lightweight daily window management | Real monitor semantics |

Both editions use the same `MUX.Core` physical geometry and the same saved layout format.

> **Keep MUX Standard if it already does what you need.** MUX Virtual Displays exists specifically for applications that must ask Windows “what monitor am I on?” and receive the MUX monitor itself.

---

## What MUX does

MUX is a native Windows workspace manager for unusually large displays, TVs, projectors, ultrawides, and wall-sized panels.

Instead of dividing a screen into arbitrary percentages, MUX starts with **physical size**. Tell MUX that your real display is 100 inches, 80 inches, 55 inches, or any other size. Then create a 24", 27", 32", 49" ultrawide, or custom monitor. MUX converts those dimensions into the exact pixel footprint required on that physical panel.

Arrange the monitors directly on the full-size display and save the layout. From there you can run either the lightweight Standard engine or the new true-virtual-monitor engine.

### Why the name MUX?

**MUX** comes from **multiplexer** — engineering terminology for coordinating multiple channels through shared infrastructure. MUX applies that idea to screen space: one physical display becomes the shared surface for multiple logical workspaces.

The name is intentionally short, technical, and functional: **one display, many workspaces**.

---

## MUX Standard

MUX Standard is the original managed-monitor architecture. Windows still sees the underlying physical panel as one display while MUX manages monitor-like regions above it. No custom display driver is required.

### Three steps

#### 1. Tell MUX how large the real display is

MUX detects the Windows resolution and display position automatically. You enter the physical diagonal.

<img src="docs/assets/step-1-display.svg" alt="Set the physical display diagonal in MUX" />

For unusual panels, TVs with inaccurate EDID data, or installations where millimetre-level accuracy matters, select **Calibrate with ruler**. MUX displays a 10-inch reference line. Measure it, enter the measured length, and MUX corrects its physical pixel scale.

#### 2. Create the monitor you want

Choose a physical diagonal and aspect ratio. MUX handles the geometry.

<img src="docs/assets/step-2-create.svg" alt="Create a 27 inch virtual monitor in MUX" />

Included aspect ratios:

- 16:9
- 16:10
- 21:9
- 32:9
- 4:3
- 3:2

Monitor positions and sizes are stored in **inches**, not screen percentages. A saved 27" monitor remains a 27" monitor when MUX recalculates the layout.

#### 3. Arrange it on the real display

Select **Edit on display**. MUX opens a full-display editing surface on the selected physical screen. Every monitor is shown at its calibrated physical size.

<img src="docs/assets/step-3-arrange.svg" alt="Arrange virtual monitor regions on a physical display and save the layout" />

Drag the workspaces wherever you want. Edges magnetically align. Select **Save layout** and the editing surface disappears.

### Standard daily use

MUX stays active in the Windows system tray. Optional black monitor outlines remain visible when enabled, so empty virtual monitors are still clearly defined without intercepting mouse input.

| Action | Behavior |
|---|---|
| Maximize a normal application | MUX converts the maximize into a monitor-sized maximize for the region containing that window |
| Maximize it again | MUX restores the application's previous bounds |
| Show monitor outlines | Keep click-through black borders visible around empty or occupied MUX monitors |
| `Ctrl + Alt + M` | Maximize / restore the foreground window inside its current MUX monitor |
| `Ctrl + Alt + ←` | Move the foreground window to the previous MUX monitor |
| `Ctrl + Alt + →` | Move the foreground window to the next MUX monitor |
| Hold `Shift` while finishing a drag | Snap the dragged application to the MUX monitor beneath it |
| `Ctrl + Alt + E` | Open the full-display layout editor |
| Close the MUX window | Keep the engine active in the system tray |

---

## MUX Virtual Displays — Option B

MUX Virtual Displays is a separate executable and separate download backed by a real Windows **Indirect Display Driver**.

For every MUX zone in the selected layout, the driver exposes a real Windows monitor at that exact pixel resolution.

So if Edge is on a MUX virtual monitor:

```text
Edge → Windows: "What monitor am I on?"
Windows → Edge: "MUX monitor, 1037 × 583"
```

Edge, Windows, and other normal desktop applications then use those monitor bounds directly.

### Native behavior

With the Virtual edition active:

- Clicking **Maximize** fills only that true MUX monitor.
- Browser **F11** fills only that true MUX monitor.
- Fullscreen APIs see the MUX monitor rather than the entire physical television/panel.
- `MonitorFromWindow`, `GetMonitorInfo`, and normal Windows display APIs see actual display targets.
- MUX does not need to rewrite maximize behavior to make an application understand the monitor.

### How the monitor appears inside the physical display

Windows display topology does not permit real displays to overlap each other. MUX therefore keeps the true virtual monitors in an off-screen desktop strip and uses a 1:1 DWM-backed compositor portal to present each monitor inside its physical MUX rectangle.

When the pointer enters a portal, MUX maps it to the corresponding coordinate on the real virtual monitor. Dragging a window through the portal moves that real window onto the real Windows monitor.

Press:

```text
Ctrl + Alt + Esc
```

to release the pointer back to the physical desktop.

See **[MUX Virtual Displays architecture and setup](docs/VIRTUAL-DISPLAYS.md)** for the full design.

---

## Downloads

### MUX Standard

**[Download MUX Standard for Windows](https://github.com/TripleAxisCapital/MUX/releases/download/latest-build/MUX-win-x64.zip)**

Use this if you want the existing lightweight MUX architecture with no display driver.

### MUX Virtual Displays

**[Download MUX Virtual Displays for Windows](https://github.com/TripleAxisCapital/MUX/releases/download/latest-build/MUX-Virtual-win-x64.zip)**

Use this if you specifically need true Windows monitor semantics.

The Virtual ZIP contains the controller and its IddCx driver package. The driver build pipeline is in the repository, but a frictionless public retail installation requires the completed driver package to receive the appropriate Microsoft production signature.

GitHub Actions refreshes both rolling downloads only after their respective Windows builds succeed.

---

## Physical sizing

Both editions use the same geometry engine.

MUX uses the diagonal pixel count of the physical display to establish its initial pixel density:

```text
pixels per inch = √(width² + height²) / physical diagonal
```

For a 3840 × 2160 panel identified as 100 inches, MUX calculates roughly 44.1 pixels per inch. A 27" 16:9 workspace is therefore rendered at approximately 1037 × 583 physical pixels.

Calibration applies a correction factor on top of that calculation. This is why MUX can work with a 100" television, an 80" panel, a 49" ultrawide, or a conventional desktop monitor without hard-coded layouts.

The Virtual edition uses that exact resulting pixel rectangle as the preferred mode of its Windows virtual monitor.

---

## Architecture

```text
MUX.Core
  Physical geometry
  Inches ↔ pixels
  Layout models
  Zone selection

MUX.App                         MUX.Virtual.App
  Standard WPF UI                Virtual-display controller UI
  Full-display editor            Shared layout reader
  Win32 display discovery        SwDevice virtual-adapter lifecycle
  WinEvent window tracking       Display topology
  Standard window management     1:1 compositor portals
  Global hotkeys                 Mouse portal
            \                   /
             \                 /
              shared MUX.Core
                       |
                       v
              MUX.Virtual.Display
              UMDF 2 / IddCx driver
              real Windows monitors
```

The .NET applications target **.NET 10 LTS** and Windows x64. Published controller builds are self-contained.

The native display driver is built against Microsoft's pinned WDK/SDK NuGet packages.

See [Architecture](docs/ARCHITECTURE.md) and [Virtual Displays](docs/VIRTUAL-DISPLAYS.md).

---

## Build MUX Standard

### Requirements

- Windows 10 version 2004 (build 19041) or later
- Windows 11 recommended
- .NET 10 SDK

### One command

```powershell
./scripts/build.ps1
```

Output:

```text
artifacts/MUX-win-x64.zip
```

Or build manually:

```powershell
dotnet restore MUX.sln
dotnet run --project tests/MUX.Core.Tests/MUX.Core.Tests.csproj -c Release
dotnet build src/MUX.App/MUX.App.csproj -c Release
dotnet publish src/MUX.App/MUX.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/MUX
```

---

## Build MUX Virtual Displays

### Requirements

- Windows 11 recommended
- .NET 10 SDK
- Visual Studio 2026 C++ build tools
- `nuget.exe`

The build pins the Windows WDK/SDK through Microsoft NuGet packages.

```powershell
./scripts/build-virtual.ps1
```

Output:

```text
artifacts/MUX-Virtual-win-x64.zip
```

Every push to `main` validates `MUX.Core`, builds MUX Standard, builds the Virtual controller, compiles the IddCx driver, packages both downloads, and only then refreshes the rolling release.

---

## Where MUX stores settings

Both editions intentionally share the same local configuration:

```text
%LOCALAPPDATA%\MUX\state.json
```

That means a layout created, calibrated, cloned, or rearranged in MUX Standard is immediately available to MUX Virtual Displays.

No account is required. The applications contain no analytics SDK or cloud synchronization service.

---

## Current limitations

### MUX Standard

MUX Standard intentionally uses managed monitor regions rather than a display driver.

That means:

- Windows Display Settings still sees the original physical display.
- Normal desktop applications are the primary target.
- Exclusive-fullscreen applications that bypass normal top-level Windows positioning may ignore MUX.
- Some protected or unusual application windows may reject external positioning.
- MUX must remain running for managed monitor behavior to remain active.

If MUX Standard exits, the physical display immediately continues behaving like a normal Windows display.

### MUX Virtual Displays

MUX Virtual is the deeper architecture and therefore has different constraints:

- The controller runs elevated because creating a Windows software display device requires administrator access.
- The display driver must be installed.
- Up to eight MUX virtual monitors are currently exposed per adapter.
- The first Virtual UI deliberately reads the same layouts created by the mature Standard layout editor instead of duplicating physical-calibration logic.
- Public retail distribution requires Microsoft-compatible production driver signing.
- Stopping or exiting MUX Virtual removes the temporary virtual display adapter and returns Windows to the original physical topology.

---

## Troubleshooting

Start with [Troubleshooting](docs/TROUBLESHOOTING.md) for Standard calibration, maximize, DPI, multi-display, and startup guidance.

For Option B, see [Virtual Displays](docs/VIRTUAL-DISPLAYS.md).

---

<div align="center">
  <strong>MUX</strong><br />
  <sub>One display. Many.</sub>
</div>
