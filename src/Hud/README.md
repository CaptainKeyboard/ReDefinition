# Hud

The additions to KSP's flight interface, its staging stack, its space centre and its time panel,
mostly one `KSPAddon` per feature, and many with a flight switch.

## Start here

| File | Read it for |
|---|---|
| `src/Hud/FlightUiSwitch.cs` | `FlightUiSwitch.On`: how every feature asks whether it is on. |
| `src/Hud/Panel/FlightPanel.cs` | The maneuver panel with the player's profiles in place of the orbit tabs. |
| `src/Hud/Panel/FlightProfiles.cs` | The situations, the six built-in profiles and where they are kept. |
| `src/Hud/Readouts/ReadoutCatalog.cs` | The values the panel can show. |
| `src/Hud/RoundGauge.cs` | The round gauge and its modes. |
| `src/Hud/WorldButton.cs` | A button that stands in the world, used by the space centre's buttons. |
| `src/Hud/StageInfoColumns.cs` | The stage details in two columns, chosen in the delta-v tools. |

## What it does

- `Panel` replaces KSP's orbit tabs with one tab per profile, switches to the profile of the
  vessel's situation, and opens an editor window for the profiles.
- `Readouts` works out the values: `ReadoutFrame` takes the vessel once per refresh, and
  `ReadoutMath` holds the sums.
- `RoundGauge` and `GaugeReadings` show one value of the situation on a digital gauge.
- `NavballDrag`, `NavballSpeeds` and `NavballDockingMarker` move the navball and add the speeds and
  the docking marker.
- `StageInfoColumns`, `DeltaVTools`, `IconColumnsLayout` and `StageGroupFields` change the
  staging stack's details, in flight and in the editors.
- `AssemblyReturn`, `SpaceCentreButtons`, `KscButtons`, `FacilitySpot` and `WorldButton` put
  buttons over the space centre's buildings.
- `TimeWarpBar` and `TimeWarpButton` add Better Time Warp's button to the time panel.
- `AutoAltimeter`, `FlightStartMode`, `Impact`, `HudTooltips`, `CrewSlotButtons` and
  `ReDefinitionLegGrip` are smaller changes of one control or one part.

The area is a stop on [The features on screen](../../docs/development/flow-game-ui.md).
The player's side: [the flight panel](../../docs/player/flight-panel.md),
[the navball](../../docs/player/navball.md) and
[stage details](../../docs/player/stage-details.md).

## What it uses and what uses it

It uses Core, Engineer, Modules, Ui and Window.
Engineer, Modules and Window name it.

## Rules of the area

- A feature with a flight switch asks `FlightUiSwitch.On` with its setting. It counts as on where
  the add-on is not there to say.
- `src/Hud/Panel/FlightPanel.cs` installs its Harmony hooks once at the game's start
  (`FlightPanelHooks`), before any flight scene's panel starts.
- The six built-in profiles are never deleted, so the panel always has one to switch to
  (`FlightProfile.Builtin`). The player's profiles live in `flightpanel.cfg` in the mod's `PluginData` folder.
- `src/Hud/Readouts/ReadoutMath.cs` stays free of Unity and KSP. `ReadoutMathTests` tests it.
- `src/Hud/StageGroupFields.cs` reads KSP's private fields through refs, without a `FieldInfo`'s
  boxing, and the ref is null where `StageGroup` has no such field.
- `src/Hud/HeldVessel.cs` holds the vessel a static cache was worked out for without keeping it
  alive.
