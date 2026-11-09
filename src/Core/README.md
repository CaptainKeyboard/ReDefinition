# Core

The small pieces every other area builds on: the log tag, paths, the command line, reflection
into other mods, Harmony hooks, and the catalogue of what ReDefinition assumes about other mods.

## Start here

| File | Read it for |
|---|---|
| `src/Core/ForeignAssumptions.cs` | The catalogue: every assumption about another mod's code, with its fingerprints |
| `src/Common/ForeignAssumption.cs` | One assumption, its `ForeignState`, and what `WhenChanged` does (in Common, shared with the planner) |
| `src/Common/ForeignCode.cs` | `ForeignCode.Fingerprint`, a method's instructions read as a hash (in Common) |
| `src/Common/HarmonyHooks.cs` | `HarmonyHooks.Install` and `HarmonyHooks.Safely` for patches of other mods (in Common) |
| `src/Common/TypeLookup.cs` | `TypeLookup.Find`, another mod's type by full name (in Common) |

## What it does

The small pieces both ReDefinition and the planner use are in [Common](../Common/README.md): the log
tag, the command line, reflection into other mods, Harmony hooks, `CompiledGetter`,
`CompatibilityLog` and `TexturePixels`. This area keeps what only ReDefinition has:

- `ForeignAssumptions` lists what ReDefinition relies on in other mods. A class asks
  `ForeignAssumption.Holds` before it relies on one. The planner's own entries are in
  `src/Planner/PlannerAssumptions.cs`, and this list joins them.
- `PluginData` and `CaptureFolder` find ReDefinition's settings folder and the capture folders.

## What it uses and what uses it

It uses no other area. Every area but Fsr3 uses it. Diagnostics reports on the assumptions.

## Rules of the area

- Core names no other area (`tests/ReDefinition.Tests/ArchitectureTests.cs`).
- `ForeignAssumption`, `ForeignAssumptions`, `ForeignCode`, `TypeLookup` and `CompiledGetter`
  (the last four in `src/Common`) stay free of Unity. `tools/checks/check_foreign_code.ps1` loads them outside the game.
- A new fingerprint goes into `ForeignAssumptions` only after you have read the new code of the
  other mod. Until then a changed build of that mod reads as `ForeignState.Changed`.
- `ForeignCode` and `TypeLookup` do not throw. What they cannot read counts as not there.
- A `WhenChanged.Fallback` assumption has a class that asks `ForeignAssumption.Holds` before it
  relies on it (`src/Core/ForeignAssumptions.cs`).
