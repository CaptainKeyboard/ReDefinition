# Modules

ReDefinition's own features as modules with settings (the upscaler, frame generation, low
latency, raytracing and the others), the file that keeps those settings, and the add-on's code
that applies a change.

## Start here

| File | Read it for |
|---|---|
| `src/Modules/OurModules.cs` | Every module and every setting of it: key, title, kind, control, tooltip |
| `src/Modules/GraphicsModule.cs` | `IGraphicsModule`, `ModuleSetting` and what a setting can declare |
| `src/Modules/OwnSettings.cs` | The values themselves, and how `settings.cfg` loads and saves |
| `src/Modules/ReDefinitionAddon.Settings.cs` | `ReDefinitionAddon.Apply`: how a change takes effect |
| `src/Modules/AutoSettings.cs` | What *Auto* resolves to for the card and the screen |
| `src/Modules/TechniqueAvailability.cs` | Which technique the card and the proxy can run, and why a button is grey |

## What it does

- `OurModules` declares eleven modules: `upscaler`, `frameGeneration`, `latency`, `raytracing`,
  `hotkeys`, `flightUi`, `uiScaling`, `mainMenu`, `spaceCentre`, `reEntry` and `dashboard`.
- `OwnSettings` holds the values and reads and writes
  `GameData/ReDefinition/PluginData/settings.cfg`.
- `ModuleProfiles` puts a graphics profile's `MODULE` nodes onto those settings.
- `AutoSettings` turns *Auto* into a technique, a mode and Ray Reconstruction for the card.
- `TechniqueAvailability` decides which techniques are offered and which wait for files
  (`Vendor` fetches them).
- `OwnSettingsReset` gives the settings window's *Reset* what stays the player's.
- `ReDefinitionAddon.Settings.cs` and `ReDefinitionAddon.Setters.cs` are parts of the add-on,
  which is declared in `src/ReDefinitionAddon.cs`. They apply each change to the rig.

## What it uses and what uses it

It uses Core, Dashboard, Diagnostics, Hud, Probe, Proxy, Raytracing, Scenery, Settings,
Upscaler, Vendor and Window. Dashboard, Diagnostics, Hud, Menu, Probe, Raytracing, Vendor and
Window use it.

## Rules of the area

- A module's name and a setting's key are stable. Profiles name them in `MODULE` nodes
  (`src/Modules/GraphicsModule.cs`).
- A setting is declared once in `OurModules`, and `OwnSettings` lists it once with its key in
  the file. The settings window and KSP's settings dialog both read the declaration.
- A profile sets quality settings only. `ModuleProfiles.CanSet` refuses taste settings, the
  hotkeys and the interface (`src/Modules/ModuleProfiles.cs`).
- A setting with `ModuleSetting.NeedsProfile`, the default, changes only while a graphics
  profile is chosen. A setting with `ModuleSetting.OnlyActivates` is set by the
  profile that first makes ReDefinition active, and is the player's afterwards.
- *Auto* keeps the value it resolved to beside the flag, so everything that reads the technique,
  the mode or Ray Reconstruction reads what runs. `AutoSettings.Resolve` runs wherever settings
  are applied and when the screen changes.
- `ModuleProfiles.Values` and `TechniqueAvailability` run outside the game, in their tests
  (`tests/ReDefinition.Tests/OurModulesTests.cs`,
  `tests/ReDefinition.Tests/TechniqueAvailabilityTests.cs`).

[A setting's path](../../docs/development/flow-setting.md) follows a value through this area.

For the design see [Architecture](../../docs/development/architecture.md) and
[Add a setting](../../docs/development/howto-add-a-setting.md).
