# Window

The settings window and everything around it: the ways to open it, the toolbar buttons it takes
over, and the controls that read keys and axes from the player.

## Start here

| File | Read it for |
|---|---|
| `src/Window/SettingsWindow/SettingsWindow.cs` | The window itself: `Toggle`, `Open`, `Close`, `Build`, and the state every tab shares. |
| `src/Window/SettingsWindow/SettingsApply.cs` | *Apply*, *Accept* and *Cancel* as one ordered pipeline. |
| `src/Window/SettingsWindow/BundledRows.cs` | A bundled mod's row, by the kind of setting. |
| `src/Window/SettingsWindow/SettingsSearch.cs` | The search field and which rows, groups and pages it leaves standing. |
| `src/Window/SettingsWindow/SettingsTabs.cs` | The tab column: the category tree and the mark on a tab that has a change waiting. |
| `src/Window/KspSettingsSection.cs` | ReDefinition's section in KSP's own settings dialog, and `KspSettingsSection.Edit`. |
| `src/Window/ToolbarTakeover.cs` | Hiding the toolbar button of a mod whose settings the window holds. |
| `src/Window/Controls/KspKeyBindings.cs` | KSP's key bindings, read from `GameSettings`. |
| `src/Window/ModWindowsAddon.cs` | The add-on that follows scenes, the toolbar and the mods' own windows. |

## What it does

- `SettingsWindow` is the static facade the rest of the mod uses. It opens, builds and closes the
  window and holds the state every tab shares: the dialog, the tab shown, the edit copies. Its
  partial files are `Interface` (*Mods / Toolbar*), `Folds`, `Ampere` and `Probe`.
- One static class in `src/Window/SettingsWindow/` for each concern: `ProfilesTab` (*Graphics*,
  Reset), `KeysTab`, `AxesTab` and `KeyboardLayoutRows` (*Controls*), `BundledRows`,
  `SettingsApply`, `WaitingNotice`, `SettingsSearch`, `SettingsTabs` and `SettingMarks`.
  Each keeps the caches of its concern.
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
