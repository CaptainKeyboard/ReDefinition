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

## What it does

- It names no other part of ReDefinition. Harmony appears only in `HarmonyHooks`, in a method that runs after the check that Harmony is loaded.
- Both packages carry the same DLL. The game keeps one DLL of a name, the one with the higher assembly version, and on equal versions the one in `GameData/ReDefinition/Plugins`.
- A change in this folder needs a new `CommonVersion` in `Directory.Build.props`. `src/Common/version.lock` holds the version and a hash of the code, and `tests/ReDefinition.Tests/CommonVersionTests.cs` fails when they disagree.
