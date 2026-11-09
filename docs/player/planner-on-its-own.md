# Use the planner without ReDefinition

**For:** players who want the planner and the autopilot but not the rest of ReDefinition.
**You need:** KSP 1.12 on Windows, and `ReDefinitionPlanner_<version>.zip` from the release
 page.
**You get:** the planner and the autopilot installed alone, what they do without ReDefinition,
and how to add ReDefinition later.

ReDefinition Planner is the route planner of ReDefinition as a mod of its own. It lays out a
route in the map and the editors and flies it with the autopilot. It needs neither
HarmonyKSP nor ZTheme nor ReDefinition. MechJeb and ReDefinition are recommended.

## Install it

1. Extract `ReDefinitionPlanner_<version>.zip` into the KSP folder, the one with
   `KSP_x64.exe`. `GameData/ReDefinitionPlanner` goes into `GameData`.
2. If you use CKAN, install *ReDefinition Planner* there. CKAN suggests MechJeb and
   ReDefinition.

The full ReDefinition zip carries the same folder. If you install ReDefinition, you do not
need this one.

## What it does without the others

| If this is missing | What changes |
|---|---|
| MechJeb | The planner works out routes. The steps that MechJeb flies are not offered, and the autopilot is grey for them. |
| HarmonyKSP | The map's menu has no entry to launch to orbit. The guide node is not saved with the vessel. The SAS does not steer to the navigation's direction. `KSP.log` names each one. |
| ReDefinition | The planner keeps two switches of its own, in the *Live* menu under *Planner Settings*. Everything else of ReDefinition's interface is not there. |
| ZTheme | The windows look like KSP's own. |

## Its two switches

*Planner Settings* in the autopilot's *Live* menu holds two switches. They are saved in
`GameData/ReDefinitionPlanner/PluginData/settings.cfg`.

| Switch | What it does |
|---|---|
| *Fly the Route in the Background* | The vessel flies the route on its own when you leave it. |
| *Buttons in the World* | The buttons that stand beside the facilities and the vessel show. |

## Add ReDefinition later

Install ReDefinition as its [installing page](installing.md) says. The planner finds it at
the next start. Its two switches then move into ReDefinition's settings window, and the
*Planner Settings* entry disappears. The planner's route, its icons and its settings file
stay where they are.

ReDefinition does not need the planner either: without it, ReDefinition shows no guidance
gauge, no burn overlay on the navball and no planner rows on the dashboard, and everything else
stays. The two share one small file, `ReDefinition.Common.dll`, and the game keeps the newer
copy. If ReDefinition and the planner come from different releases, update both to the same
release; `KSP.log` names a copy that is older than the one a mod was built against.
