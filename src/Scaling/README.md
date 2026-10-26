# Scaling

What KSP leaves at a fixed number of pixels, scaled to KSP's UI scale, and the other mods'
scaling of the same things, turned off.

## Start here

| File | Read it for |
|---|---|
| `src/Scaling/ScalingAddon.cs` | The add-on that installs every piece, and the scene classes `FlightScaling`, `TrackingStationScaling`, `CommNetLines` and `ManeuverGizmos`. |
| `src/Scaling/UiScaling.cs` | The scale (`UiScaling.World`), the `ModWindows` switch and the names of the mods it turns off. |
| `src/Scaling/ScalingHooks.cs` | The Harmony hooks for the editor gizmos, the orbit lines and the portrait gallery. |
| `src/Scaling/ModWindowScale.cs` | Other mods' IMGUI windows at the UI scale. |
| `src/Scaling/OtherScalers.cs` | Turning off 4kSP, 4kSP Expanded and Maneuver Node Evolved where they scale the same thing. |

## What it does

- `ScalingAddon` installs `ScalingHooks`, `ModWindowScale`, `RnDNodeHooks` and `OtherScalers` once,
  as the game starts.
- `MapNodeScale` scales the map's icons and captions every frame, in flight and in the tracking
  station.
- `FlightScaling` scales the vessel labels every frame and runs `CommNetLines` and `ManeuverGizmos`.
  `TrackingStationScaling` runs `CommNetLines` in the tracking station.
- `SpaceCentreScaling` and `RnDNodeHooks` enlarge the R&D tree, its nodes and the parts list.
- `ModWindowScale` sets a `GUI.matrix` on every `GUI.Window` call so that a mod's window grows
  about its top left corner.

The area is a stop on [The features on screen](../../docs/development/flow-game-ui.md). What each
piece scales, and how KSP sizes it, is in
[UI scaling of what KSP leaves unscaled](../../docs/development/ui-scaling.md).

## What it uses and what uses it

It uses Core. Ui uses it.

## Rules of the area

- `UiScaling.World` is `GameSettings.UI_SCALE`, clamped to 0.25 to 8 (`src/Scaling/UiScaling.cs`).
- Each piece installs on its own. A piece that fails logs a compatibility warning and leaves the
  others installed (`ScalingAddon.Install`).
- Scaling of other mods' windows is switchable by `OwnSettings.ScaleModWindows`, read through
  `UiScaling.ModWindows` (`src/Scaling/UiScaling.cs`).
- Where another mod scales the same thing, `OtherScalers` turns that scaling off, so that nothing
  is scaled twice (`src/Scaling/OtherScalers.cs`).
- `ModWindowScale` puts the previous `GUI.matrix` back in a postfix and in a finalizer, so a
  throwing window call leaves it as it was (`src/Scaling/ModWindowScale.cs`).
