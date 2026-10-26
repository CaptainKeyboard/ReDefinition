# Window

The settings window and everything around it: the ways to open it, the toolbar buttons it takes
over, and the controls that read keys and axes from the player.

## Start here

| File | Read it for |
|---|---|
| `src/Window/SettingsWindow/SettingsWindow.cs` | The window itself: `Toggle`, `Open`, `Close`, `Build`, and the fields every tab shares. |
| `src/Window/SettingsWindow/SettingsWindow.Apply.cs` | *Apply*, *Accept* and *Cancel* as one ordered pipeline. |
| `src/Window/SettingsWindow/SettingsWindow.BundledRows.cs` | A bundled mod's row, by the kind of setting. |
| `src/Window/KspSettingsSection.cs` | ReDefinition's section in KSP's own settings dialog, and `KspSettingsSection.Edit`. |
| `src/Window/ToolbarTakeover.cs` | Hiding the toolbar button of a mod whose settings the window holds. |
| `src/Window/Controls/KspKeyBindings.cs` | KSP's key bindings, read from `GameSettings`. |
| `src/Window/ModWindowsAddon.cs` | The add-on that follows scenes, the toolbar and the mods' own windows. |

## What it does

- `SettingsWindow` is one static class in several files, one for each tab or concern: `Axes`,
  `Keys`, `Interface` (*Mods / Toolbar*), `Profiles` (*Graphics*), `Navigation`, `Waiting`,
  `Folds`, `Layout`, `Ampere`, `BundledRows` and `Apply`.
- Four things open it: `ToolbarButton`, `MainMenuEntry`, `PauseMenuEntry` and
  `KspSettingsSection`. The settings hotkey is handled in `src/ReDefinitionAddon.cs`.
- `BundleNotice` and `ProxyNotice` speak in the main menu. `GameRestart` starts KSP again for
  settings that apply at a start.
- `ToolbarTakeover`, `ToolbarHides` and `ModWindowClose` deal with the bundled mods' toolbar
  buttons and their own windows, which *Advanced* opens.
- `Controls` holds `KeyCapture` and `AxisCapture`, which listen for the next key or axis, and
  `KspKeyBindings` and `KspAxes`, which list KSP's own.
- `Conflicts` marks bindings that share a combination in one situation.
- `WindowPause` holds game time while the window is open. `LoadingScreenWindows` keeps the
  toolbar's buttons and MechJeb's menu off the loading screen. `KspUiState` tells whether KSP's
  interface shows.

The area is a stop on [A setting's path](../../docs/development/flow-setting.md) and on
[Startup and scenes](../../docs/development/flow-startup.md). The player's side is in
[The settings window](../../docs/player/settings-window.md).

## What it uses and what uses it

It uses Core, Hud, Modules, Proxy, Settings, Ui and Vendor. Api, Diagnostics, Modules, Probe, Ui
and Vendor use it, and so do the files directly under `src/`.

## Rules of the area

- The window edits copies: ReDefinition's own settings in an `OwnSettings` copy, the bundled mods'
  values in `SettingsEdit`. Nothing reaches a mod before *Apply*, and *Cancel* leaves everything as
  it was (`src/Window/SettingsWindow/SettingsWindow.cs`).
- The window is built from KSP's own dialog elements, not IMGUI, so a UI theme such as ZTheme
  themes it (`src/Window/SettingsWindow/SettingsWindow.cs`, `src/Window/KspSettingsSection.cs`).
- `SettingsWindow.Close` must release what the window held: `WindowPause.Release`,
  `KeyCapture.Stop` and `AxisCapture.Stop`. A row still listening would keep KSP's controls locked.
- Escape cancels a listening row and does nothing else, through `EscapeGuard`
  (`src/Window/Controls/KeyCapture.cs`).
- A toolbar button is hidden only where ReDefinition's window opens the mod's own window or a
  dialog in its place (`src/Window/ToolbarTakeover.cs`, `IBundledMod.OwnWindow`).
- `ModWindowsAddon` calls `SettingsWindow.SceneChanged` once a scene's GUI is ready, so waiting
  changes of the last scene drop (`src/Window/ModWindowsAddon.cs`).
