# Ui

The planner's windows and buttons: the engineer window of the VAB and the SPH with the planner mode, the planner window in the map, the launch and marker menus, and the rows over the model of [Planning](../Planning/README.md).

## Start here

| File | Read it for |
|---|---|
| `src/Planner/Ui/EditorEngineer.cs` | The entry: the planner's button, the mode, and when the vessel is read. |
| `src/Planner/Ui/PlannerPanel.cs` | The panel KSP brings in and out like its own. |
| `src/Planner/Ui/PlannerView.cs` | The rows over the model; its other parts are `PlannerView.*.cs`. |
| `src/Planner/Ui/MapPlannerWindow.cs` | The planner window in the map, with `MapPlannerButton`, `MapLaunchMenu` and `MapMarkerMenu`. |
| `src/Planner/Ui/PlannerDashboard.cs` | The dashboard's VAB page: the planner's rows and handlers. |
| `src/Planner/Ui/PlannerWidgets.cs` | The symbols, tints, dropdown and bars the rows are built from. |

## What it does

- `EditorEngineer` adds the planner button beside KSP's mode buttons and reads the vessel again when it changes. It holds the vessel for a window at most once in `EditorEngineer.RefreshInterval`.
- `PlannerPanel` makes the panel as a copy of KSP's crew panel, and `ModeButtonTiles` makes the button's tiles from the cargo button's.
- `EditorLaunchButton` puts a launch button over the pad seen through the VAB's door and over the runway seen from the SPH.
- `PlannerView.*.cs` build the rows from KSP's dialog elements over the model: sections, route, bars, fuel plan and thrust. `PlannerView.Dashboard.cs` hands the view's calls on to `PlannerDashboard`, which changes the plan through the model's commands.
- `PartHighlight` marks parts on the vessel, one marker at a time: whichever marks parts last takes the other's marks away, and nothing is marked while the pointer is on a part of the vessel.
- `LandingMarker`, `LiveMenu` and `RoutePreview` show the route and the landing in the flight and the map.

The area is a stop on [The features on screen](../../../docs/development/flow-game-ui.md). More: [the planner as its own package](../README.md).

## What it uses and what uses it

It uses Planning, Engineer, the autopilot's driver, Core and Ui. The planner's hooks and its Hud name it.
