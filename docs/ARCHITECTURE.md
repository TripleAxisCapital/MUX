# MUX architecture

MUX now has two independent Windows backends built on one physical geometry model:

- **MUX Standard** — the original managed-monitor system.
- **MUX Virtual Displays** — the Option B backend using a real Windows Indirect Display Driver (IddCx).

The Standard backend remains intentionally lightweight. The Virtual backend exists for applications that must receive true Windows monitor semantics.

## Shared design goals

1. Physical dimensions are authoritative.
2. Display resolution changes do not redefine a monitor's requested physical size.
3. UI/backend behavior is separate from physical geometry.
4. Both editions consume the same `DisplayProfile`, `LayoutProfile`, and `VirtualMonitorZone` models.
5. Stopping either engine has a safe recovery path.
6. The Standard path never depends on the display driver.

## State model

`DisplayProfile` describes the selected physical Windows display:

- Windows device name
- physical pixel bounds
- physical diagonal entered by the user
- calibration scale

`VirtualMonitorZone` stores:

- requested diagonal in inches
- aspect ratio
- X/Y position in inches from the physical display's top-left corner

`LayoutProfile` groups zones for one physical display.

Both editions intentionally read the same local JSON state:

```text
%LOCALAPPDATA%\MUX\state.json
```

## Shared geometry

For an aspect ratio `a:b` and diagonal `d`:

```text
width  = d × a / √(a² + b²)
height = d × b / √(a² + b²)
```

Initial physical pixel density is:

```text
ppi = √(displayWidthPx² + displayHeightPx²) / displayDiagonalInches
```

The ruler calibration process multiplies that PPI by a correction factor.

`DisplayGeometry.ZoneToPixels` is the final authority for converting a saved physical monitor zone into a Windows pixel rectangle. Standard uses the rectangle as a managed window target. Virtual uses the rectangle's width and height as the preferred mode of a real Windows monitor.

---

# MUX Standard backend

## Full-display editor

`LayoutOverlayWindow` is placed directly over the chosen physical monitor using its Win32 pixel bounds. Zone rectangles are converted from physical pixels into the overlay's WPF coordinate space. This allows the editor to remain correct under Windows DPI scaling.

The editor stores the result back in inches and performs magnetic edge snaps against the display perimeter and neighboring zones.

## Window engine

`WindowManagerService` uses `SetWinEventHook` to observe:

- foreground changes
- move/size start
- move/size end
- top-level window location changes

MUX ignores its own process and tool windows.

### Managed maximize

When Windows reports that an eligible foreground application became maximized:

1. MUX reads the application's normal restore rectangle.
2. MUX determines which virtual zone contains that rectangle.
3. The native maximize is restored.
4. MUX places the window at the zone's physical pixel rectangle.
5. The original restore rectangle is retained.

This remains a managed/pseudo-maximize by design.

### Drag snapping

When a move/resize operation ends while Shift is held, MUX finds the best zone by window-center containment and intersection area, then fills that zone.

### Hotkeys

The Standard application registers system-wide hotkeys with `RegisterHotKey`:

- `Ctrl + Alt + M`: MUX maximize / restore
- `Ctrl + Alt + Left`: previous managed monitor
- `Ctrl + Alt + Right`: next managed monitor
- `Ctrl + Alt + E`: full-display editor

## Startup and tray behavior

Standard can register itself in the current user's `Run` key with the `--background` argument. Closing the main window keeps its services active in the tray.

---

# MUX Virtual Displays backend

The Virtual edition replaces Standard's maximize/window interpretation layer with actual Windows monitor targets.

```text
saved MUX layout
      ↓
MUX.Core geometry
      ↓
exact zone pixel sizes
      ↓
MUX.Virtual.exe
      ↓
SwDeviceCreate + custom configuration property
      ↓
MUXVirtualDisplay.dll (UMDF 2 / IddCx)
      ↓
real Windows monitors
```

## Temporary software display adapter

`VirtualDeviceService` creates a software device with `SwDeviceCreate`.

The adapter uses hardware ID:

```text
MUXVirtualDisplay
```

The controller passes a fixed, versioned binary configuration property containing up to eight monitors:

```text
version
monitor count
monitor[0]
  width
  height
  refresh
  stable container GUID
...
```

The monitor container GUID comes from the saved MUX zone ID so Windows can distinguish the logical monitors consistently while the adapter is alive.

The software-device handle is intentionally owned by the controller. Closing it tells Plug and Play to remove the temporary adapter.

## IddCx driver

`MUXVirtualDisplay.dll` is a UMDF 2 Indirect Display Driver.

At startup it:

1. Reads the custom MUX device property with `WdfDeviceQueryPropertyEx`.
2. Initializes an IddCx adapter.
3. Reports one connector per MUX monitor.
4. Creates an EDID-less software monitor for each connector.
5. Reports exactly one preferred monitor mode matching the MUX zone pixel dimensions.
6. Reports a matching target mode.
7. Consumes IddCx swap-chain surfaces on a dedicated D3D11 processing thread.

The driver does not decide physical layout. That remains in `MUX.Core`.

## Windows desktop topology

Windows desktop sources cannot occupy overlapping desktop coordinates. The controller therefore places the real MUX monitors in a contiguous strip outside the physical desktop:

```text
[ physical desktop ][ MUX 1 ][ MUX 2 ][ MUX 3 ] ...
```

Applications on those displays are genuinely on separate Windows monitors.

## Physical compositor portals

The user still needs to see those monitors inside the intended rectangles on the large physical panel.

`MagnifierCompositorService` creates one click-through, no-activate, topmost host window for every MUX zone. Each host contains a Windows Magnifier control whose source rectangle is the corresponding off-screen true virtual monitor.

Because source and destination dimensions are identical, the compositor is 1:1 rather than a scaled screenshot.

```text
virtual monitor source
        ↓
Windows Magnification / DWM
        ↓
click-through portal
        ↓
saved physical MUX rectangle
```

The portal is a presentation surface only. The application itself stays on the true virtual monitor.

## Mouse portal

`MousePortalService` installs a low-level mouse hook while Virtual mode is active.

When the cursor crosses into a physical portal:

1. MUX determines which portal contains the point.
2. It calculates the relative X/Y coordinate inside the portal.
3. It moves the system pointer to the same coordinate on the real virtual monitor.
4. Windows continues input and drag operations on that monitor.

This is what allows a normal drag to cross from the physical display into a real MUX monitor.

`Ctrl + Alt + Esc` releases the pointer back to the physical display and brings the controller forward.

## Why native maximize and F11 work

No special maximize code is required once an application window is on the IddCx monitor.

The application calls normal Windows monitor APIs. Windows resolves the window to the MUX display path and returns that display's mode/work area.

Therefore:

```text
normal Maximize → MUX monitor work area
browser F11     → MUX monitor bounds
MonitorFromWindow / GetMonitorInfo → MUX monitor
```

That behavior comes from Windows itself rather than from MUX rewriting the application's window state.

## Failure and shutdown

Stopping Virtual mode performs this order:

1. Stop mouse portal.
2. Destroy compositor host windows.
3. Close the software-device handle.
4. Windows removes the temporary virtual display adapter and monitor paths.

If the process exits unexpectedly, Windows also loses the software-device handle and removes the temporary device.

## Driver signing boundary

The repository owns the driver source, deterministic WDK version, CI compilation, packaging, and INF/catalog generation.

Production distribution still crosses a platform trust boundary: the completed driver package must go through Microsoft's current driver-signing process before it can install frictionlessly on retail Windows systems.

MUX does not disable Secure Boot or code-integrity enforcement as part of normal installation.

## Project boundaries

```text
src/MUX.Core
  shared physical geometry + models

src/MUX.App
  MUX Standard

src/MUX.Virtual.App
  MUX Virtual controller
  software-device lifecycle
  display topology
  compositor
  mouse portal

driver/MUX.Virtual.Display
  UMDF 2 / IddCx display driver
```

The driver is deliberately outside `MUX.sln` so normal .NET development and Standard builds do not require the Windows Driver Kit.
