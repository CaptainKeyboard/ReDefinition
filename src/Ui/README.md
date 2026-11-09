# Ui

What ReDefinition adds to the dialogs that `src/Common` gives both mods: questions over the settings window, the see-through backdrop, and the click handling that only ReDefinition has. The foundation itself (the click shield, `DialogWindow`, `ScreenFit`, `UiHygiene`, tooltips, scrolling) is in [Common](../Common/README.md).

## Start here

| File | Read it for |
|---|---|
| `src/Ui/UnityMouseEvents.cs` | The redirected cameras that get the screen back for Unity's mouse events, and the windows only ReDefinition has that the shield must count. |
| `src/Ui/DialogOverWindow.cs` | A question that stands over the settings window. |
| `src/Ui/ConfirmDialog.cs` | A question with a button that does it and KSP's Cancel. |
| `src/Ui/WindowBackdrop.cs` | The see-through or darkened backdrop of a window. |
| `src/Ui/DialogRows.cs` | The rows a dialog is built from. |

## What it does

- `UnityMouseEvents` hooks into the shield's two passes (`WindowShield.BeforePass`, `AfterPass`). Before Unity's mouse events each camera the rig redirects gets the screen as its target, and after them the rig's texture again. It also tells the shield which windows count besides the dialogs: the windows of the bundled mods, a mod window drawn larger, and the diagnostics window.
- `DialogOverWindow`, `ConfirmDialog` and `WindowBackdrop` place, draw and shield the questions of the settings window.

The conventions every control follows are in
[The features on screen](../../docs/development/flow-game-ui.md). The scale `ScreenFit` works
against is explained in
[UI scaling of what KSP leaves unscaled](../../src/Scaling/README.md).

## What it uses and what uses it

It uses Common, Core, Scaling, Upscaler and Window. Diagnostics, Hud, Probe, Vendor and Window use it, and so do the files directly under `src/`.

## Rules of the area

- A dialog of ReDefinition's is opened through `DialogWindow.Show`, or passed to `WindowShield.Shield` where it is built another way. Then a click on it does not reach the space centre's buildings or the IVA switches behind it (`src/Common/DialogWindow.cs`, `src/Common/WindowShield.cs`).
- A dialog over the settings window is spawned not modal. The darkened screen takes the clicks and the `UI_DIALOGS` lock stands while it is there (`src/Ui/DialogOverWindow.cs`).
- `UnityMouseEvents.Install` adds nothing and logs a warning if `PreUpdate.SendMouseEvents` is not in the player loop (`src/Ui/UnityMouseEvents.cs`).
