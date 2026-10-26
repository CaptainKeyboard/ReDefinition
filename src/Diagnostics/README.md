# Diagnostics

The diagnostics window: the strip with what runs, the frame rates, the latency and the load,
the debug switches and the pages below them, and the report on other mods.

## Start here

| File | Read it for |
|---|---|
| `src/Diagnostics/ReDefinitionAddon.Diagnostics.cs` | The shell: `OnGUI`, the window, the page list, the foot |
| `src/Diagnostics/DebugOptions.cs` | `DebugOption`, `DebugOptionSet`, `DebugPins`: the options as data |
| `src/Diagnostics/ReDefinitionAddon.DiagnosticsOptions.cs` | Every option: where it is shown, how it is read and set |
| `src/Diagnostics/ReDefinitionAddon.DiagnosticsRows.cs` | How an option is drawn by its kind |
| `src/Diagnostics/ReDefinitionAddon.DiagnosticsLiveStrip.cs` | The strip, refreshed four times a second |
| `src/Diagnostics/ReDefinitionAddon.DiagnosticsMods.cs` | The *Other mods* page: state, registrations, code |

## What it does

- `ReDefinitionAddon.Diagnostics.cs`, `DiagnosticsPages`, `DiagnosticsMods`,
  `DiagnosticsLiveStrip`, `DiagnosticsRows` and `DiagnosticsOptions` are parts of the add-on
  (`src/ReDefinitionAddon.cs`) that draw the window's regions.
- `DebugOption` holds an option's page, kind and persistence. `DebugOptionSet` filters, resets
  and pins them.
- `ReDefinitionAddon.FrameRates.cs` measures the rendered and the shown frame rate once a second.
- `DiagnosticsStyles` and `DiagnosticsContent` hold the styles and the `GUIContent` the window
  draws with, made once.
- `ForeignAssumptionReport` logs what came of the assumptions about other mods' code when the
  main menu first opens. The *Other mods* page shows the same.
- `HookProbe` registers a handler on each of `ReDefinition.Api`'s hooks and logs that each fired.

## What it uses and what uses it

It uses Api, Core, Modules, Proxy, Raytracing, Settings, Ui, Upscaler and Window. Modules uses
it.

## Rules of the area

- The window draws with copies of KSP's skin (`HighLogic.Skin`), so a UI theme reaches it
  (`src/Diagnostics/DiagnosticsStyles.cs`). Do not copy Unity's default skin.
- `GUIContent`, widths and layout options are made once, not in each event, because IMGUI asks
  for them in the layout pass and again in the repaint (`src/Diagnostics/DiagnosticsContent.cs`).
- The strip's texts are made in `RefreshLiveStrip`. `OnGUI` only reads them.
- Each option states how long it lasts with `DebugPersistence`: `Saved`, `SessionOnly` or
  `NeedsRestart`. The window shows a marker for it on the row.
- `DebugOption` and `DebugOptionSet` stay free of Unity, so that their decisions are tested
  outside the game (`tests/ReDefinition.Tests/DebugOptionsTests.cs`).
- The window is not drawn while KSP's interface is hidden, and it does not let clicks through
  (`UnityMouseEvents.ImguiWindow` in `ReDefinitionAddon.OnGUI`).
- `HookProbe` is never saved, because it exists to find out what the hooks give a mod author.

[Startup and scenes](../../docs/development/flow-startup.md) lists the add-on of every scene.

For the words see [the glossary](../../docs/glossary.md).
