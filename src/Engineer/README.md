# Engineer

The engineer window of the VAB and the SPH: the planner mode with its button and panel, and the
planner that lays out a route and shares the vessel's stages over its legs.

## Start here

| File | Read it for |
|---|---|
| `src/Engineer/EditorEngineer.cs` | The entry: the planner's button, the mode, and when the vessel is read. |
| `src/Engineer/EditorReadout.cs` | What the planner takes of the vessel, from KSP's delta-v simulation. |
| `src/Engineer/PlannerPanel.cs` | The panel KSP brings in and out like its own. |
| `src/Engineer/Planner/PlannerView.cs` | The plan held and worked out; its other parts are `PlannerView.*.cs`. |
| `src/Engineer/Planner/PlannerState.cs` | One vessel's plan and where it is kept. |
| `src/Engineer/Planner/RouteSteps.cs` | The steps between two stops and who flies them. |
| `src/Engineer/Planner/StageBudget.cs` | Which stage pays for which leg. |

## What it does

- `EditorEngineer` adds the planner button beside KSP's mode buttons and reads the vessel again
  when it changes.
- `EditorReadout` and `StageTable` hold the vessel as KSP's delta-v simulation gives it, with each
  stage's thrust at the surface of each body with air.
- `PlannerPanel` makes the panel as a copy of KSP's crew panel, and `ModeButtonTiles` makes the
  button's tiles from the cargo button's.
- `EditorLaunchButton` puts a launch button over the pad seen through the VAB's door and over the
  runway seen from the SPH.
- `Planner/PlannerView.*.cs` build the rows from KSP's dialog elements: sections, route, bars, fuel
  plan and thrust.
- `Planner/RouteSteps.cs`, `Planner/RoutePlanner.cs` and `Planner/PlannerBodies.cs` turn a route over
  the loaded system's bodies into steps.
- `Planner/StageBudget.cs`, `Planner/StageSim.cs`, `Planner/ClampedLaunch.cs`,
  `Planner/BurnCheck.cs`, `Planner/Turning.cs` and `Planner/SectionRcs.cs` work out what each step
  needs and what the stages give.
- `Planner/PartHighlight.cs` marks parts on the vessel.
- `Planner/PlannerView.Dashboard.cs` gives the dashboard's VAB page the planner's rows and handlers.

The area is a stop on [The features on screen](../../docs/development/flow-game-ui.md).
More: [the planner's place in the editor](../../src/Engineer/README.md) and
[the planner](../../src/Engineer/README.md).

## What it uses and what uses it

It uses Core, Hud, Ui and Window.
Dashboard and Hud name it.

## Rules of the area

- The planner's vessel numbers are KSP's delta-v simulation's (`EditorReadout`), so the planner and
  the staging stack never disagree.
- The vessel is read at most once in `EditorEngineer.RefreshInterval`, so that a change and the
  simulation it starts make one or two reads.
- The sums are free of Unity and tested: `src/Engineer/EngineerFormat.cs`,
  `src/Engineer/StageTable.cs`, `src/Engineer/Planner/RoutePlanner.cs` and the files `BurnCheck`,
  `ClampedLaunch`, `SectionRcs`, `StageBudget`, `StageSim` and `VesselShape` in `Planner`.
- `Planner/PartHighlight.cs` has one marker at a time. Whichever marks parts last takes the other's
  marks away, and nothing is marked while the pointer is on a part of the vessel.
- A plan is kept per vessel name in `planner.cfg` in the mod's `PluginData` folder
  (`PlannerState`).
- The dashboard changes the plan through the handlers the buttons use (`PlannerView.Dashboard.cs`).
