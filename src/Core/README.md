# Core

The small pieces every other area builds on: the log tag, paths, the command line, reflection
into other mods, Harmony hooks, and the catalogue of what ReDefinition assumes about other mods.

## Start here

| File | Read it for |
|---|---|
| `src/Core/ForeignAssumptions.cs` | The catalogue: every assumption about another mod's code, with its fingerprints |
| `src/Core/ForeignAssumption.cs` | One assumption, its `ForeignState`, and what `WhenChanged` does |
| `src/Core/ForeignCode.cs` | `ForeignCode.Fingerprint`, a method's instructions read as a hash |
| `src/Core/HarmonyHooks.cs` | `HarmonyHooks.Install` and `HarmonyHooks.Safely` for patches of other mods |
| `src/Core/TypeLookup.cs` | `TypeLookup.Find`, another mod's type by full name |

## What it does

- `ForeignAssumptions` lists what ReDefinition relies on in other mods. A class asks
  `ForeignAssumption.Holds` before it relies on one.
- `ForeignCode` and `TypeLookup` read other mods' assemblies by reflection and IL, free of Unity.
- `CompiledGetter` reads another mod's field or property without boxing every frame.
- `CompatibilityLog` limits each kind of warning to three lines, so a repeated failure cannot
  flood `KSP.log`.
- `Log.Tag` starts every line ReDefinition writes into `KSP.log`.
- `PluginData`, `CaptureFolder` and `CommandLine` find the settings folder, the capture folders
  and the `-redefinition` switches.
- `TexturePixels` reads the pixels of a sprite whose texture is not readable.

## What it uses and what uses it

It uses no other area. Every area but Fsr3 uses it. Diagnostics reports on the assumptions.

## Rules of the area

- Core names no other area (`tests/ReDefinition.Tests/ArchitectureTests.cs`).
- `ForeignAssumption`, `ForeignAssumptions`, `ForeignCode`, `TypeLookup` and `CompiledGetter`
  stay free of Unity. `tools/checks/check_foreign_code.ps1` loads them outside the game.
- A new fingerprint goes into `ForeignAssumptions` only after you have read the new code of the
  other mod. Until then a changed build of that mod reads as `ForeignState.Changed`.
- `ForeignCode` and `TypeLookup` do not throw. What they cannot read counts as not there.
- A `WhenChanged.Fallback` assumption has a class that asks `ForeignAssumption.Holds` before it
  relies on it (`src/Core/ForeignAssumptions.cs`).
