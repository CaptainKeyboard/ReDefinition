# Planner

The route stack as a mod of its own, `ReDefinition.Planner.dll`: the planner window in the map and the editors, the navigation, the autopilot that flies the route, the orbit maths and the landing prediction. It needs neither ReDefinition nor Harmony nor ZTheme. MechJeb and ReDefinition are recommended.

## Start here

| Folder or file | Read it for |
|---|---|
| `src/Planner/Engineer/` | The vessel and stage maths: the vessel as KSP's simulation gives it, the stages, the sections. |
| `src/Planner/Planning/` | The plan: the route, its steps, the stage budget, and the model and state that hold them. |
| `src/Planner/Ui/` | The windows and buttons: the editor's engineer window, the planner window in the map, the menus. |
| `src/Planner/Autopilot/` | The autopilot: planning the next step, flying it, and the background route that flies without the scene. Its `Driver` folder follows the planner's route (`RouteDriver`) and hands it over when the player leaves. |
| `src/Planner/Orbits/` | The orbit maths, free of Unity and KSP. |
| `src/Planner/Landing/` | The simulation behind the landing and re-entry predictions. |
| `src/Planner/Hud/` | The autopilot's buttons and its activity line on the flight screen. |
| `src/Planner/PlannerStartup.cs` | The start of the planner: it fills the hooks ReDefinition reads and answers the shared seams from its own settings where ReDefinition is not there. |
| `src/Planner/Hooks/` | The adapters that fill `PlannerHooks`: the guidance, the route state, the editor's plan rows and the dry marks. |
| `src/Planner/PlannerSettings.cs` | The two switches the planner keeps itself where ReDefinition is not installed. |
| `src/Planner/PlannerAssumptions.cs` | What it takes for granted about MechJeb's and Parsek's code. |

## What it does

- The project references `src/Common/` and nothing of ReDefinition. `tests/ReDefinition.Tests/ArchitectureTests.cs` checks that its project file names neither the main mod nor Harmony nor ZTheme.
- ReDefinition and the planner do not name each other. They meet in `src/Common/`: ReDefinition assigns `HostSeams` in `src/HostLink.cs` when it starts, and the planner fills `PlannerHooks` at its own start. Each works where the other is not installed.
- Where Harmony is not loaded, the hooks into the map's menu, the guide node and the SAS are left out. Each says so in the log.
- The package is `GameData/ReDefinition/Planner`: `ReDefinition.Planner.dll`, `Icons/`, this README and its version file. The shared `ReDefinition.Common.dll` is in `GameData/ReDefinition/Common`, and the planner's zip carries both.
- Its dialogs and panels open through `DialogWindow` and `WindowShield` in `src/Common/`, so a click on them stays on them with or without ReDefinition. Its settings are in `PluginData/` in its folder.
