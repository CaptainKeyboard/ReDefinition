# Engineer

The vessel and stage maths of the planner: what the vessel is made of, what its stages give and what a burn takes. Nothing in it knows of goals, routes or windows. The route is [Planning](../Planning/README.md) and the windows are [Ui](../Ui/README.md).

## Start here

| File | Read it for |
|---|---|
| `src/Planner/Engineer/EditorReadout.cs` | What the planner takes of the vessel, from KSP's delta-v simulation. |
| `src/Planner/Engineer/StageTable.cs` | The stages as KSP's simulation gives them, with each stage's thrust at the surface of each body with air. |
| `src/Planner/Engineer/StageSim.cs` | The planner's own stage simulation, and `AirLaunch` for the air-breathing engines' part of a launch. |
| `src/Planner/Engineer/VesselShape.cs` | The vessel's parts, its sections and how the mass falls into groups (`VesselSplit`). |
| `src/Planner/Engineer/VesselSplit.cs` | The stages as the budget reads them (`BudgetStage`) and the groups the vessel falls into. |
| `src/Planner/Engineer/ClampedLaunch.cs` | The stages lit together on the pad, as launch clamps leave them. |
| `src/Planner/Engineer/Turning.cs` | How fast what flies a step can turn, and the RCS of a group of parts (`SectionRcs.cs`). |
| `src/Planner/Engineer/StageBurner.cs` | The burns of a plan shared over the vessel's stages, for the background route. |

## What it does

- `EditorReadout` and `StageTable` hold the vessel as KSP's delta-v simulation gives it, so that the planner and the staging stack never disagree.
- `StageSim` works the stages out again where an engine breathes air. `AirLaunch.Plan` takes the part of the launch those engines fly as a function of the Mach number; `LaunchShare` in Planning makes that function from a body.
- `VesselShape` finds the sections of a vessel and `VesselSplit` shares its mass and stages over them.
- `StageBurner` implements `IBurnShare` of the background route.

The area is a stop on [The features on screen](../../../docs/development/flow-game-ui.md). More: [the planner as its own package](../README.md).

## Rules of the area

- The sums are free of Unity where they can be, and tested: `EngineerFormat`, `StageTable`, `StageSim`, `VesselShape`, `ClampedLaunch`, `Turning` and `SectionRcs`.
- It uses nothing of `Planner.Planning`, `Planner.Ui` or the autopilot but the interfaces of `Autopilot.Background` that it implements. `ArchitectureTests` fails otherwise.

## What it uses and what uses it

It uses Core and `Autopilot.Background`. Planning, Ui, the hooks and the autopilot's driver name it.
