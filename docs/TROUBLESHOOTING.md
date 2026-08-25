# MUX troubleshooting

## A monitor does not look physically correct

1. Confirm the physical diagonal in the right-side **Physical display** panel.
2. Select **Calibrate with ruler**.
3. Measure the white reference line with a real ruler or tape measure.
4. Enter the measured length and select **Calibrate**.
5. Reopen **Edit on display**.

MUX deliberately trusts manual calibration over unreliable physical-size metadata from televisions and unusual panels.

## Windows scaling is 125%, 150%, or 200%

MUX is Per-Monitor-V2 DPI aware. The full-display editor derives its conversion from the selected monitor's physical pixel bounds and the actual overlay size, so Windows UI scaling should not change the physical monitor dimensions.

If the result is still visibly wrong, use ruler calibration.

## Clicking maximize fills the entire physical screen

MUX must be enabled and running. Open MUX and confirm the **MUX Engine** indicator is active.

If the application uses an unusual window implementation, use `Ctrl + Alt + M` as the explicit MUX maximize command.

Exclusive-fullscreen software may bypass the normal top-level window behavior used by MUX V0.1.

## Shift-drag did not snap

Keep Shift held when the move operation ends. MUX evaluates the target zone on the Windows move/size-end event.

You can disable or enable this behavior with **Shift-drag snaps to monitor**.

## A hotkey does nothing

Another application may already own that system-wide keyboard combination. Restart MUX after closing the conflicting application.

Default hotkeys are:

- `Ctrl + Alt + M`
- `Ctrl + Alt + Left`
- `Ctrl + Alt + Right`
- `Ctrl + Alt + E`

## MUX disappeared when I closed the window

That is expected. MUX keeps the window engine active in the system tray. Double-click the MUX tray icon or choose **Open MUX**.

Choose **Quit MUX** from the tray to fully stop the application.

## Launch at sign in does not work during development

The startup entry points at the currently running executable. For predictable startup behavior, test the self-contained published build rather than a temporary Visual Studio host process.

## My physical resolution changed

MUX stores virtual monitor positions and sizes in inches. On the next launch, display discovery refreshes the physical pixel bounds and the geometry engine recalculates the zone rectangles.

A major hardware or scaling change is a good reason to run ruler calibration again.

## A virtual monitor is larger than the display

MUX blocks creation or resizing when the requested physical workspace cannot fit inside the calibrated physical screen area. Reduce the diagonal, choose a different aspect ratio, or correct the physical display size/calibration.

## Reset all MUX settings

Quit MUX and remove:

```text
%LOCALAPPDATA%\MUX\state.json
```

Start MUX again. A new default workspace will be created.
