# The engineer view

**For:** anyone changing the engineer view in the VAB and the SPH.
**You need:** the repository, and `src/Engineer` open.
**You get:** where the view's numbers come from, how it sits in KSP's editor, and where
each part of Kerbal Engineer Redux's build view went.

The player's side is in [player/engineer.md](../player/engineer.md).

## Where the numbers come from

The numbers are KSP's own. Since 1.6, KSP simulates the staging of the vessel in the
editor itself, in `VesselDeltaV` on `ShipConstruct.vesselDeltaV` **[src]**. Its
`OperatingStageInfo` holds one `DeltaVStageInfo` per stage. Each has delta-v, specific
impulse, thrust and TWR, each at sea level, in vacuum and in the chosen situation,
together with its start, end and dry mass and its burn time **[src]**. The situation is
`DeltaVGlobals.DeltaVAppValues`: a body, sea level, an altitude or vacuum. KSP's delta-v
app writes it and then calls `SetCalcsDirty(true)` **[src]**. The engineer view does the
same, and KSP's staging stack follows. KSP's delta-v app reads the situation only as it
opens (`DeltaVAppSituation.Awake`), and a change in it writes all of its own values back
**[src]**. While it stands open, it can show another situation than the view.

`EditorReadout` reads the simulation four times a second while the view or its summary
line is shown. It adds what KSP does not give:

| Worked out | How |
|---|---|
| parts and cost of each stage | from `DeltaVPartInfo.decoupleStage`: a part is attached while a stage burns if KSP decouples it at that stage or later, and dropped with it if it decouples at it. This is the test `DeltaVStageInfo.ProcessParts` uses **[src]**. Costs are counted as `ShipConstruct.GetShipCosts` counts them **[src]** |
| TWR at the end of a stage | the TWR KSP gives at the start, times start mass over end mass (`StageTable`) |
| delta-v left | the stage's delta-v and that of every stage after it (`StageTable`) |
| thrust torque | the sum of every burning engine's thrust about the centre of mass of what is attached. The thrust pushes against a thrust transform's forward axis |
| RCS | the RCS thrusters and their propellant on what is left at the last stage, with Tsiolkovsky's equation and KSP's g0, 9.80665 m/s² |
| resources | every part's resources, summed by name |
| part details | `PartDetails`, from the part's modules, read again four times a second. `Mouse.HoveredPart` is a raycast that ignores the UI **[src]**, so the part is not read while the pointer is over a window |
| mass and cost | `ShipConstruct.GetShipMass` and `GetShipCosts` with `ShipConstruction.ShipManifest`, so the crew and their inventory count as KSP's cost widget and mass limit count them **[src]** |

`StageTable` and `EngineerFormat` do not depend on Unity and have unit tests
(`tests/ReDefinition.Tests/EngineerTests.cs`).

## How the view sits in KSP's editor

KSP's mode buttons are four `Button`s in the editor's top bar: `partPanelBtn`,
`actionPanelBtn`, `crewPanelBtn` and `cargoPanelBtn` in `EditorLogic` **[src]**. They are
48 units wide, placed by hand rather than by a layout group, and each draws its tile as
its own sprite with sprite swap. The editor scene shows this (`sharedassets6`, read with
UnityPy) **[src]**. The active mode's button is the one KSP makes not interactable, so its
disabled sprite is the active tile. KSP places the buttons once, from the anchors
`crewBtnOrigPos`, `cargoBtnOrigPos` and `editorBtnOrigPos` **[src]**.

`EditorEngineer` copies the cargo button and gives the copy its own four tiles
(`GameData/ReDefinition/Icons/EngineerButton_*.png`, drawn by `tools/make_icon.py`). It
sets the copy one step right of the last visible mode button, and the switch-editor
button one step further. It does this every frame, before the canvases draw, so the row
follows whatever KSP does to it. KSP sets its buttons' interactable state only on events,
such as a changed input lock or a mode switch (`EditorLogic.UpdateUI`) **[src]**. So while
the view is open, `EditorEngineer` keeps the parts button clickable. On closing, it hands
the buttons back to `UpdateUI`.

The view itself is built from KSP's dialog elements into the parts panel,
`EditorPanels.partsEditor`, over the parts list. `EngineerPanel` creates it as a
`PopupDialog` creates its content, with `DialogGUIBase.Create` and the panel as the
layout's parent, and updates it every frame as a `PopupDialog` does **[src]**. So it
slides with the panel, scales with KSP's UI and takes a UI theme such as ZTheme. The
summary line (`EngineerSummary`) stands beside the panel rather than inside it, where the
panel's mask could cut it off. It is moved to the panel's top right corner every frame.

Opening the view from another mode switches KSP to the parts mode first
(`EditorLogic.SelectPanelParts`). Any change of mode closes the view
(`GameEvents.onEditorScreenChange`), and so does KSP's parts button.

## What Kerbal Engineer Redux's build view became

Kerbal Engineer Redux (CYBUTEK, jrbudda; GPL-3.0) was read for the list of what its
editor part offers (`KerbalEngineer/Editor` in its repository, version 1.1.9.5)
**[src]**:

| Kerbal Engineer | Here |
|---|---|
| stage table: parts, cost, mass, Isp, thrust, torque, TWR (max), delta-v, burn time | *Stages*: delta-v, delta-v left, TWR and burn time per row; the rest in the row's details |
| RCS columns per stage | *RCS, last stage* |
| body list | *<* and *>* over every body |
| atmospheric switch and altitude slider | *Sea level*, *Altitude* and *Vacuum*, KSP's delta-v app's situation |
| Mach slider | left out. KSP's simulation takes no Mach number, so air-breathing engines are computed as KSP computes them |
| *All stages* | the setting *Stages without delta-v* |
| compact mode, and the overlay's vessel tab | the summary line |
| the overlay's resources tab | *Resources* |
| part information on hover | *Part under the mouse* |
| vectored thrust | the setting *Thrust along the vessel*, from KSP's `vectoredThrustActual` |
| Real Fuels residuals, simulation log, minimum simulation delay | left out, since KSP runs the simulation |
| GUI size | KSP's own UI scale |
| overlay position | left out: the view has a fixed place |
| key bindings | *Engineer view (VAB / SPH)* in the *Controls* tab |
| flight engineer and its settings | not part of the editor view |

What KSP already shows in place stays KSP's. The delta-v in the staging stack, the
engineer's report and the parts list's tooltips are not shown a second time. The stage
table repeats KSP's per-stage numbers on purpose: comparing the stages needs them side by
side in one place, and since they are KSP's own, they cannot disagree.

When Kerbal Engineer is installed, `EditorEngineer` recommends uninstalling it once per
game start, and the view says so at its bottom.
