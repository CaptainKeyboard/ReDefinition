# Planner

The route stack as a mod of its own, `ReDefinition.Planner.dll`: the planner window in the map and the editors, the navigation, the autopilot that flies the route, the orbit maths and the landing prediction. It needs neither ReDefinition nor Harmony nor ZTheme. MechJeb and ReDefinition are recommended.

## Start here

| Folder or file | Read it for |
|---|---|
| `src/Planner/Engineer/` | The planner's model and window, the editor's engineer window, the route driver and the map additions. |
| `src/Planner/Autopilot/` | The autopilot: planning the next step, flying it, and the background route that flies without the scene. |
| `src/Planner/Orbits/` | The orbit maths, free of Unity and KSP. |
| `src/Planner/Landing/` | The simulation behind the landing and re-entry predictions. |
| `src/Planner/Hud/` | The autopilot's buttons and its activity line on the flight screen. |
| `src/Planner/PlannerSeams.cs` | What ReDefinition fills in when it is installed, each with a default that works alone. |
| `src/Planner/PlannerSettings.cs` | The two switches the planner keeps itself where ReDefinition is not installed. |
| `src/Planner/PlannerAssumptions.cs` | What it takes for granted about MechJeb's and Parsek's code. |

## What it does

- The project references `src/Common/` and nothing of ReDefinition. `tests/ReDefinition.Tests/ArchitectureTests.cs` checks that its project file names neither the main mod nor Harmony nor ZTheme.
- ReDefinition fills `PlannerSeams` in `src/PlannerLink.cs` when it starts, and only if `PlannerSeams.Level` is the level it was made for. Another level leaves the planner on its defaults, with a warning in `KSP.log`.
- Where Harmony is not loaded, the hooks into the map's menu, the guide node and the SAS are left out. Each says so in the log.
- The package is `GameData/ReDefinitionPlanner`: the two DLLs, `Icons/`, this README and its version file.
- Its dialogs and panels open through `DialogWindow` and `WindowShield` in `src/Common/`, so a click on them stays on them with or without ReDefinition. Its settings are in `PluginData/` beside them.
