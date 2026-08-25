# MUX architecture

MUX V0.1 is a managed-monitor system. It does not install a display driver and does not claim that its regions are physical Windows monitors. Instead, MUX maintains a physical coordinate model and applies monitor-like behavior to ordinary top-level application windows.

## Design goals

1. Physical dimensions are authoritative.
2. Failure is safe: closing MUX returns Windows to normal immediately.
3. Display resolution changes do not redefine a monitor's requested physical size.
4. The UI and window engine are separate from the geometry model.
5. A future virtual-display backend can consume the same layouts.

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

All persistent state is JSON under `%LOCALAPPDATA%\MUX\state.json`.

## Geometry

For an aspect ratio `a:b` and diagonal `d`:

```text
width  = d × a / √(a² + b²)
height = d × b / √(a² + b²)
```

Initial physical pixel density is:

```text
ppi = √(displayWidthPx² + displayHeightPx²) / displayDiagonalInches
```

The ruler calibration process multiplies that PPI by a correction factor. Zone pixel rectangles are generated only when required by the UI or window engine.

## Full-display editor

`LayoutOverlayWindow` is placed directly over the chosen physical monitor using its Win32 pixel bounds. Zone rectangles are converted from physical pixels into the overlay's WPF coordinate space. This allows the editor to remain correct under Windows DPI scaling.

The editor stores the result back in inches. It also performs small magnetic edge snaps against the display perimeter and neighboring zones.

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

If the user invokes maximize again, MUX restores the retained rectangle.

This is a pseudo-maximize by design. The application remains a normal top-level window after MUX places it.

### Drag snapping

When a move/resize operation ends while Shift is held, MUX finds the best zone by window-center containment and intersection area, then fills that zone.

### Hotkeys

The main window registers system-wide hotkeys with `RegisterHotKey`:

- `Ctrl + Alt + M`: MUX maximize / restore
- `Ctrl + Alt + Left`: previous virtual monitor
- `Ctrl + Alt + Right`: next virtual monitor
- `Ctrl + Alt + E`: full-display editor

## Startup and tray behavior

MUX can register itself in the current user's `Run` key with the `--background` argument. Background startup creates the main window and services, then hides the UI while leaving the window engine and tray icon active.

Closing the main window hides it to the tray. Choosing **Quit MUX** from the tray disposes hooks and exits the process.

## Why no driver in V0.1?

A virtual display driver requires a compositor and input mapping layer if virtual displays must be shown inside regions of the same physical panel. That increases installation, graphics, signing, update, HDR, and failure complexity.

The managed engine validates the central MUX interaction model first while keeping the architecture ready for a second backend later.

## Future MUX Native boundary

A future native backend should consume `DisplayProfile`, `LayoutProfile`, and `VirtualMonitorZone` from MUX.Core, while replacing the Win32 window-management layer with:

```text
IddCx virtual displays
        ↓
Direct3D surfaces
        ↓
MUX compositor
        ↓
physical display regions
```

The user-facing inch model does not need to change.
