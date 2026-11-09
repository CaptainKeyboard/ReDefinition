# Common

The DLL both packages ship, `ReDefinition.Common.dll`: what ReDefinition and the planner stand on. The namespaces are `ReDefinition.Core` and `ReDefinition.Ui`.

## Start here

| File | Read it for |
|---|---|
| `src/Common/Log.cs` | The tag every line in `KSP.log` begins with. |
| `src/Common/TypeLookup.cs` | Another mod's type by its full name, from whatever assemblies are loaded. |
| `src/Common/ForeignAssumption.cs` | The check of an assumption about another mod's code against the build installed. |
| `src/Common/HarmonyHooks.cs` | Harmony hooks that are left out where Harmony is not loaded. |
| `src/Common/ModFolder.cs` | The settings folder of a mod: `GameData/<mod>/PluginData`. |
| `src/Common/UiHygiene.cs` | The helpers every dialog is built with: tooltips, mouse handling, scrolling. |
| `src/Common/DialogWindow.cs` | The one way a dialog is opened: shielded from clicks, kept within the screen, off the keyboard's navigation. |
| `src/Common/WindowShield.cs` | Clicks on a window of ours that do not reach the buildings, the switches, the parts or the map behind it. |
| `src/Common/HostSeams.cs` | What ReDefinition gives the planner when both are installed: the scale, two switches, the space centre's buttons. Each has a default that works alone. |
| `src/Common/PlannerHooks.cs` | What the planner gives ReDefinition: the guidance, the route state, the editor's plan rows, the dry marks. Null where the planner is not installed. |

## What it does

- It names no other part of ReDefinition. It holds what both mods share, such as the dialog helpers, the seams and the hooks, so that neither mod names the other. Harmony appears only in `HarmonyHooks`, in a method that runs after the check that Harmony is loaded.
- Both packages carry the same DLL. The game keeps one DLL of a name, the one with the higher assembly version, and on equal versions the one in `GameData/ReDefinition/Plugins`.
- A change in this folder needs a new `CommonVersion` in `Directory.Build.props`. `src/Common/version.lock` holds the version and a hash of the code, and `tests/ReDefinition.Tests/CommonVersionTests.cs` fails when they disagree.
