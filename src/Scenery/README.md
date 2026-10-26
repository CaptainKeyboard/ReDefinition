# Scenery

The fixes to what the game draws in the space centre and in flight, each a `KSPAddon` of its own.

## Start here

| File | Read it for |
|---|---|
| `src/Scenery/KscGrass.cs` | Parallax's grass lifted onto the space centre's lawns. |
| `src/Scenery/KscGround.cs` | The lawns drawn as Parallax's terrain beside them. |
| `src/Scenery/ParallaxScatterFields.cs` | How ReDefinition reads Parallax's scatters. |
| `src/Scenery/FireflyIdle.cs` | Firefly's re-entry effects, idle while they show nothing. |
| `src/Scenery/RunwayLights.cs` | The runway's lights dimmed. |
| `src/Scenery/ReflectionProbeFix.cs` | KSP's flight reflection probe switched on again. |

## What it does

- `KscGrass` grows Parallax's own clumps on the lawns, where they lay under the ground
  (`ReDefinitionKscGrass.compute`).
- `KscGround` gives the space centre's lawn Parallax's terrain look through
  `ReDefinitionKscGround.shader`.
- `ParallaxScatterFields` reads Parallax's active scatter renderers for `KscGrass` and for the rays
  (`RtKspMods` in `src/Raytracing/Ksp/RtKspMods.cs`).
- `FireflyIdle` empties Firefly's command buffer and stops its airstream camera below an entry
  strength (`FireflyIdle.SleepBelow`).
- `RunwayLights` gives each runway light material a property block with a scaled colour.
- `ReflectionProbeFix` clears `FlightReflectionProbe.turnOff` after an `Enable(true)` that came
  after an `Enable(false)`.

The area is a stop on [The features on screen](../../docs/development/flow-game-ui.md).

## What it uses and what uses it

It uses Core, Raytracing and Upscaler.
Modules, Probe and Raytracing name it.

## Rules of the area

- `KscGrass` and `KscGround` need Parallax. `KscGround` runs only while `OwnSettings.KscLawnTerrain`
  is on, and puts the materials back when it goes off or the scene ends.
- `KscGrass` runs only while `OwnSettings.KscGrass` is on, and `FireflyIdle` only while
  `OwnSettings.FireflyIdle` is on. In `src/Modules/OurModules.cs` the `FireflyIdle` row appears only
  with Firefly installed, and the `KscGrass` row is not interactive without Parallax.
- `FireflyIdle` has two thresholds, `WakeAt` and `SleepBelow`, so that a strength near one
  threshold does not switch the effects every frame.
- `RunwayLights` creates its `MaterialPropertyBlock` in `Start`, not in a constructor or field
  initializer. A development player throws there.
- `ParallaxScatterFields` reads Parallax through compiled getters and only for the version it was
  checked against (`ForeignAssumptions.ParallaxScatters`). Otherwise `Available` is false and
  both users leave the scatters out.
