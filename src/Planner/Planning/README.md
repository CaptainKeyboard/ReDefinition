# Planning

The planner's plan: the route over the loaded system's bodies, its steps, which stage pays for which leg, and the model and state that hold it. Free of dialog elements: the windows in [Ui](../Ui/README.md) build their rows from it.

## Start here

| File | Read it for |
|---|---|
| `src/Planner/Planning/PlannerModel.cs` | The plan held and worked out; its other parts are `PlannerModel.*.cs`. |
| `src/Planner/Planning/PlannerState.cs` | One vessel's plan and where it is kept. |
| `src/Planner/Planning/RoutePlanner.cs` | The bodies, stops and legs, and the closed-form delta-v of a launch, a transfer and a landing. |
| `src/Planner/Planning/RouteSteps.cs` | The steps between two stops and who flies them. |
| `src/Planner/Planning/StageBudget.cs` | Which stage pays for which leg. |
| `src/Planner/Planning/PlanSections.cs` | The sections resolved against the vessel's parts. |
| `src/Planner/Planning/PlanThrust.cs`, `PlanFuel.cs`, `BurnCheck.cs` | The checks and figures over a plan: thrust of each step, the fuel plan, the burns. |
| `src/Planner/Planning/RouteSource.cs` | A plan's stops as the autopilot's destinations. |

## What it does

- `RoutePlanner` and `RouteSteps` turn a route over the bodies of `PlannerBodies` into steps, and `StageBudget` shares the vessel's stages over them.
- `PlannerModel` holds the route and its edits, evaluates it and writes the steps' texts. `PlanSections`, `PlanThrust` and `PlanFuel` are views over its figures.
- `LaunchShare` makes the air-breathing engines' share of a launch for a body, and hands it to `AirLaunch` in Engineer.
- A plan is kept per vessel name in `planner.cfg` in the mod's `PluginData` folder (`PlannerState`).

## What it uses and what uses it

It uses Engineer and the autopilot's planning (`Autopilot.Planning`). Ui, the hooks, the autopilot's driver and the probes name it.
