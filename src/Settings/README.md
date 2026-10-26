# Settings

The store and the registrations: it reads each bundled mod's registration, reaches the mod's
settings, keeps the values the player chose, and applies graphics profiles.

## Start here

| File | Read it for |
|---|---|
| `src/Settings/BundledSettings.cs` | The static entry the rest of ReDefinition calls: the mods, `Set`, `Release`, `RestoreBackup`, `ReapplyStored`. |
| `src/Settings/BundledStore.cs` | The hand-over: what reaches a mod, when, and how a value lasts (`InModFiles`, `PerSave`, `AtEveryStart`). |
| `src/Settings/ModRegistry.cs` | Reading the `MOD_SETTINGS` nodes from the GameDatabase, and which registration wins. |
| `src/Settings/RegisteredMod.cs` | A mod built from its registration: member paths, behaviours, dropped settings. |
| `src/Settings/ModBehaviour.cs` | The steps a behaviour can override, and the table of names a registration may give. |
| `src/Settings/ProfileApplier.cs` | A graphics profile carried out as many rows set at once. |
| `src/Settings/SettingsEdit.cs` | What the settings window edits, and *Apply* as a series of steps. |

## What it does

- `ModRegistration` and `ModRegistry` turn registration nodes into data; `RegisteredMod` turns
  that data into a bundled mod with settings, dialogs and key bindings.
- `MemberPath` reads and writes another mod's members through reflection.
- The classes in `src/Settings/Behaviours` answer for what a member path cannot reach. The
  `ModBehaviours.known` table names eight; `ProvidedSettings` loads one a mod brings itself.
- `BundledStore` keeps the values, the values from before ReDefinition and the waiting hand-overs.
  `BundledFile` and `BundledLedger` hold that data, and `bundled.cfg` is its file.
- `BundledSettingsAddon` hands the values over at every scene change, in step with the mods' own
  per-scene applies.
- `ModDefaults`, `ModProfiles`, `GraphicsProfile` and `ProfileApplier` give the defaults and the
  profiles. `Requirements` lays what mods need of a setting over both.
- `WindowLayout` decides which row shows in which tab. `SettingsEdit` is the window's model.
- `KeyCombination` and `KeptBindings` hold key bindings, and `KspReset` is KSP's own reset.

The area is a stop on [A setting's path](../../docs/development/flow-setting.md) and on
[Startup and scenes](../../docs/development/flow-startup.md). The store is explained in
[The settings store](../../docs/development/settings-store.md), and a new setting is added as in
[Add a setting](../../docs/development/howto-add-a-setting.md).

## What it uses and what uses it

It uses Core, Proxy, Raytracing and Vendor. Api, Diagnostics, Modules, Probe, Upscaler and Window
use it, and so do the files directly under `src/`.

## Rules of the area

- A registration from outside `ReDefinition/` in GameData takes precedence over ReDefinition's
  own of the same name (`ModRegistry.ReadSources`).
- A registration names a behaviour only from `ModBehaviours.known`, or by a type name with a dot,
  which `ProvidedSettings` loads (`src/Settings/ModBehaviour.cs`).
- A mod that is not installed is skipped without a word. A missing member leaves out that setting
  only, and a missing `needs` or `required` member leaves out the mod
  (`src/Settings/RegisteredMod.cs`).
- Registrations and profiles are read only once `ModRegistry.Ready` is true, so that ModuleManager's
  patches have applied (`src/Settings/ProfileLibrary.cs`).
- `Requirements` is enforced with the bundling on, except for a rule `ModBehaviour.HeldAtRunTime`
  names (`src/Settings/Requirements.cs`).
- `SettingsEdit`, `BundledLedger` and `SettingValues` use no game UI, so tests run them without the
  game. Only `KeyCombination.Pressed`, `Held` and `Released` need it
  (`src/Settings/KeyCombination.cs`).
- `BundledSettingsAddon` subscribes at the start of the game and TUFX at the end of loading. KSP
  fires the last subscriber first, so ReDefinition's handlers run after TUFX's in the same frame
  (`src/Settings/BundledSettingsAddon.cs`).
