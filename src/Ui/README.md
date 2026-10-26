# Ui

The helpers every ReDefinition dialog is built with: KSP's dialog elements made to behave, kept on
screen, shielded from clicks through them, and given tooltips.

## Start here

| File | Read it for |
|---|---|
| `src/Ui/UiHygiene.cs` | What every control ReDefinition makes itself must get right: keyboard navigation, layer, tooltip. |
| `src/Ui/UnityMouseEvents.cs` | Clicks on ReDefinition's windows, and Unity's mouse events on cameras the rig redirects. |
| `src/Ui/ScreenFit.cs` | Keeping a dialog within the screen at KSP's UI scale. |
| `src/Ui/DialogOverWindow.cs` | A question that stands over the settings window. |
| `src/Ui/EscapeGuard.cs` | An Escape that cancelled a row or a text field does nothing else. |
| `src/Ui/TabScrollList.cs` | The scroll list every tab of the settings window shares. |

## What it does

- `UiHygiene` takes controls off the keyboard's navigation (`MouseOnly`), puts new objects on the
  UI layer (`Child`), and gives them KSP's tooltip (`Tooltip`).
- `DialogTooltips` gives the elements of a created dialog their tooltip controller, and
  `TooltipText` breaks tooltip lines at `TooltipText.MaxLine` characters.
- `UnityMouseEvents` inserts two systems into Unity's player loop around
  `PreUpdate.SendMouseEvents`. They give redirected cameras their screen back for the pass and mask
  the cameras while the cursor is over a ReDefinition window.
- `ScreenFit`, `WindowBackdrop`, `DialogOverWindow` and `ConfirmDialog` place, draw and shield
  dialogs. `ScrollThrough` and `TabScrollList` make scrolling in them work.
- `IconSprites` makes sprites from GameData textures once and keeps them.

The conventions every control follows are in
[The features on screen](../../docs/development/flow-game-ui.md). The scale `ScreenFit` works
against is explained in
[UI scaling of what KSP leaves unscaled](../../src/Scaling/README.md).

## What it uses and what uses it

It uses Core, Scaling, Upscaler and Window. Diagnostics, Engineer, Hud, Probe, Vendor and Window
use it, and so do the files directly under `src/`.

## Rules of the area

- Every `Selectable` ReDefinition makes or adds is set to `MouseOnly`. Otherwise Space or Return
  presses a clicked button again, such as the staging key in flight (`src/Ui/UiHygiene.cs`).
- A tooltip line is at most `TooltipText.MaxLine` (83) characters; a longer one is wrapped
  (`src/Ui/TooltipText.cs`).
- A dialog over the settings window is spawned not modal. The darkened screen takes the clicks and
  the `UI_DIALOGS` lock stands while it is there (`src/Ui/DialogOverWindow.cs`).
- A dialog of ReDefinition's is passed to `UnityMouseEvents.Shield`, so that a click on it does not
  reach the space centre's buildings or the IVA switches behind it (`src/Ui/ConfirmDialog.cs`).
- `EscapeGuard` holds back `PopupDialog.Update` through a Harmony prefix while a taken Escape is
  down, and `KeyCapture` keeps its lock meanwhile (`src/Ui/EscapeGuard.cs`).
- `UnityMouseEvents.Install` logs a warning and installs nothing if `PreUpdate.SendMouseEvents` is
  not in the player loop (`src/Ui/UnityMouseEvents.cs`).
