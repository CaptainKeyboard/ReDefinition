# GameData/ReDefinition

The static files of the mod that ship as they are. They are the registrations of the bundled mods,
the graphics profiles, the ModuleManager patches, the dashboard's pages and the icons.

## Start here

| File | Read it for |
|---|---|
| `GameData/ReDefinition/Mods/KSP.cfg` | A registration at full size: KSP's own settings, with defaults and notes |
| `GameData/ReDefinition/Profiles/ReDefinition-Profiles.cfg` | The five graphics profiles and ReDefinition's own modules at each |
| `GameData/ReDefinition/Mods/Scatterer.cfg` | A registration of a graphics mod, with `PROFILE` blocks |
| `GameData/ReDefinition/Dashboard/index.html` | The page the dashboard's server hands out |
| `GameData/ReDefinition/Patches/LegGrip.cfg` | A ModuleManager patch that adds a module to every landing leg |

## What it does

- `Mods` holds one registration for each bundled mod: Deferred, Distant Object Enhancement, EVE,
  Firefly, KSP itself, Parallax, Persistent Rotation, Scatterer, TUFX and Waterfall. A registration
  is a `MOD_SETTINGS` node. It lists the mod's settings, defaults, profile values and key bindings.
- `Profiles/ReDefinition-Profiles.cfg` defines the profiles Low, Medium, High, Ultra and Max as
  `GRAPHICS_PROFILE` nodes. A profile sets quality, never taste. High is every mod as its authors
  ship it.
- `Patches` holds two ModuleManager patches. `LegGrip.cfg` adds `ReDefinitionLegGrip` to landing
  legs, and `ParallaxGrassCapacity.cfg` gives Parallax room for all of Kerbin's grass clumps.
- `Dashboard` is the web pages for a phone, a browser or a Corsair iCUE touch screen. The mod serves
  them, and `manifest.json` describes them to iCUE.
- `Icons` holds the toolbar button and the pictures of the flight panel, the planner and the
  navball.
- `PluginData` is not in the repository. The mod makes it at run time and keeps `bundled.cfg`,
  `settings.cfg`, `bindings.cfg` and the files of the planner, the flight panel and the gauge
  there.

A player gets this folder with `Plugins/ReDefinition.dll` and `Shaders/redefinition.shaders`
beside it, and `dxgi.dll` next to `KSP_x64.exe`.
See [Packaging](../../docs/development/packaging.md) for the zip,
[Registering a mod](../../docs/modders/registering-a-mod.md) for the format of a registration, and
[A setting's path](../../docs/development/flow-setting.md) for what the mod does with one.

## Rules of the area

- Every build copies these files into the game. It first removes `Profiles/ReDefinition-*.cfg`,
  `Mods/*.cfg` and `Patches/*.cfg` there, so a file dropped from the repository leaves the game
  too. It never touches `PluginData` (`src/ReDefinition.csproj`, target `DeployToGameData`).
- A registration names its mod with `detect`. A mod is bundled only when it is installed and its
  registration was read. A setting marked `required` that a build lacks leaves the mod out as a
  whole ([Registration reference](../../docs/modders/registration-reference.md)).
- A registration's members and defaults are held to the installed mods by
  `tools/checks/check_bundled_mods.ps1`, and every file must read without a problem
  (`tests/ReDefinition.Tests/ShippedRegistrationTests.cs`).
- What a profile sets for a mod stands in that mod's registration, in `PROFILE` blocks. The
  profile file holds only what the chooser shows and the values of ReDefinition's own modules.
- The patches are read by ModuleManager only.
- The dashboard listens on port 8765 and serves this folder's `Dashboard`
  ([The dashboard](../../docs/development/flow-dashboard.md)). The build packs the same folder as
  `ksp-dashboard.icuewidget`, which the player installs in iCUE.
