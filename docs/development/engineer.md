# The engineer window and the planner's place in the editor

**For:** anyone changing the engineer window or the planner's panel in the VAB and the
SPH.
**You need:** the repository, and `src/Engineer` open.
**You get:** where the numbers come from, how the window and the planner sit in KSP's
editor, and what the window shows and leaves to KSP.

The player's side is in [player/engineer.md](../player/engineer.md) and
[player/planner.md](../player/planner.md). How the planner works out a route is in
[planner.md](planner.md).

## Where the numbers come from

The numbers are KSP's own. Since 1.6, KSP simulates the staging of the vessel in the
editor itself, in `VesselDeltaV` on `ShipConstruct.vesselDeltaV` **[src]**. Its
`OperatingStageInfo` holds one `DeltaVStageInfo` per stage. Each has delta-v, specific
impulse, thrust and TWR, each at sea level, in vacuum and in the chosen situation,
together with its start, end and dry mass and its burn time **[src]**. The situation is
`DeltaVGlobals.DeltaVAppValues`: a body, sea level, an altitude or vacuum. KSP's delta-v
app writes it and then calls `SetCalcsDirty(true)` **[src]**. `EngineerSituation` does
the same, and KSP's staging stack follows. KSP's delta-v app reads the situation only as
it opens (`DeltaVAppSituation.Awake`), and a change in it writes all of its own values
back **[src]**. While it stands open, it can show another situation than the window.

`EditorReadout` reads the simulation four times a second. It adds what KSP does not
give:

| Worked out | How |
|---|---|
| parts and cost of each stage | from `DeltaVPartInfo.decoupleStage`: a part is attached while a stage burns if KSP decouples it at that stage or later, and dropped with it if it decouples at it. This is the test `DeltaVStageInfo.ProcessParts` uses **[src]**. Costs are counted as `ShipConstruct.GetShipCosts` counts them **[src]** |
| TWR at the end of a stage | the TWR KSP gives at the start, times start mass over end mass (`StageTable`) |
| thrust torque | the sum of every burning engine's thrust about the centre of mass of what is attached. The thrust pushes against a thrust transform's forward axis |
| the parts that burn in a stage | the engines active in it, and the parts that hold less propellant at its end than at its start (`DeltaVPartInfo.stageFuelMass`) **[src]** |
| RCS | the RCS thrusters and their propellant on what is left at the last stage, with Tsiolkovsky's equation and KSP's g0, 9.80665 m/s² |
| resources | every part's resources, summed by name |
| part details | `PartDetails`, from the part's modules |
| mass and cost | `ShipConstruct.GetShipMass` and `GetShipCosts` with `ShipConstruction.ShipManifest`, so the crew and their inventory count as KSP's cost widget and mass limit count them **[src]** |

`StageTable` and `EngineerFormat` do not depend on Unity and have unit tests
(`tests/ReDefinition.Tests/EngineerTests.cs`).

## How the engineer window sits in the editor

`EngineerWindow` is a panel of KSP's dialog elements in the editor's UI, beside the parts panel
rather than inside it. It shows while the parts list is shown, and the vessel is read only
while it or the planner needs it. Its background is the skin's window sprite, so a UI theme
such as ZTheme reaches it as it reaches KSP's dialogs. It is anchored at the bottom centre of
the screen and moved by dragging its background (`WindowDrag`). Unity hands a drag to the first
handler above the pointer: a button takes none, so the drag moves the window, and the table's
scroll list takes it for itself. The table's scroll list is settled as the planner's is
(`PlannerWidgets.Settle`), its rows as wide as the scroll view leaves them, and the header in
by the same 4 units. The part card is 380 units wide, so the longest names and their values
stand side by side. The window rebuilds its elements when it is folded, opened or switched
between compact and full. Its place and those choices are kept in
`GameData/ReDefinition/PluginData/engineer.cfg`. Every frame it is kept on the screen, whatever
the screen's size or the UI scale was when its place was saved. The table's rows are labels, as
the header is, so their columns line up. The rows' texts are written again only when the
readout, the selection or the table's form changes.

A click on a stage's row marks the parts that burn in it with KSP's own part
highlighter (`PartHighlight`: `Part.SetHighlightType`, `SetHighlightColor`,
`SetHighlightDefault`) **[src]**. KSP puts a part's highlight back to its default in
other places too, so every frame a mark that is no longer on in its colour is set again.
While the pointer is on a part of the vessel (`Mouse.HoveredPart`, and no window or list
under it), nothing is marked and KSP highlights as it does without the mod; the window's
stage is then no longer selected (`PartHighlight.Lost`). While the planner is open or the
window is folded, the window marks nothing. One marker at a time: whichever marks parts,
the window or the planner, takes the other's marks away.

The window and the planner share the list of bodies (`BodyPicker`). Whoever opens it
owns it, and only the owner's closing closes it.

`PartCard` shows the part under the pointer in a card beside it. `Mouse.HoveredPart` is
a raycast from the editor's camera that ignores the UI **[src]**, so the card stays
hidden while the pointer is over a window, and while a part hangs on the mouse
(`EditorLogic.SelectedPart`).

## How the planner sits in the editor

KSP's mode buttons are four `Button`s in the editor's top bar: `partPanelBtn`,
`actionPanelBtn`, `crewPanelBtn` and `cargoPanelBtn` in `EditorLogic` **[src]**. They are 48
units wide, placed by hand rather than by a layout group. Each draws its tile as its own sprite
with sprite swap, and keeps its aspect (`Image` with `preserveAspect`). A tile's sprite is 128
by 128 pixels, and its picture, 107 high, sits 19 pixels under the top (the sprite's texture
rectangle and its offset). The editor scene shows this (`sharedassets6`, read with UnityPy)
**[src]**. The active mode's button is the one KSP makes not interactable, so its disabled
sprite is the active tile. KSP places the buttons once, from the anchors `crewBtnOrigPos`,
`cargoBtnOrigPos` and `editorBtnOrigPos` **[src]**.

`EditorEngineer` copies the cargo button and puts the copy right before it among its
siblings. The buttons overlap, and KSP's order draws each over the left edge of the one
to its right: the parts button, leftmost, comes last **[src]**. Every frame, before the
canvases draw, it sets the copy one step right of the last visible mode button and the
switch-editor button one step further, and gives the copy the cargo button's size,
anchors, scale and image settings. The first frame writes both buttons' measures to
`KSP.log`, in a line that starts with `Mode buttons:`. KSP sets its buttons'
interactable state only on events, such as a changed input lock or a mode switch
(`EditorLogic.UpdateUI`) **[src]**. So while the planner is open, `EditorEngineer` keeps
the parts button clickable. On closing, it hands the buttons back to `UpdateUI`.

The copy's tiles are made in the game from the cargo button's tiles as they are
(`ModeButtonTiles`). A UI theme such as ZTheme replaces those through HUDReplacer, which
swaps the pixels of a texture by its name as the scene's UI is ready
(`onLevelWasLoadedGUIReady`); the cargo tiles are one texture, `VABCargo_Button`, with a
sprite per state (HUDReplacer's and ZTheme's sources, read) **[src]**. So the copy has
the theme's flat tiles where ZTheme is installed, and KSP's own where it is not. For
each state, the tile is copied through a render texture, since textures are not
readable from the CPU. Each row of its face is painted over between the face's own
colours at its edges, which takes the cargo glyph off. "Δv" is laid on in the colour of
that glyph, centred on the face at two thirds of its height, from a mask drawn by
`tools/make_icon.py` (`GameData/ReDefinition/Icons/EngineerGlyph_Fill.png`). The tiles
are made again after the scene's UI is ready and a second later, whichever comes after
the theme. Where a tile cannot be made, the copy falls back to the tiles
`tools/make_icon.py` draws after KSP's (`EngineerButton_*.png`).

KSP's four panels in the editor are `UIPanelTransition`s that its panel manager,
`EditorPanels.panelManager`, brings in and out (`EditorPanels.ShowCrewAssignment` and its
siblings) **[src]**. Bringing one in takes out every other panel the manager lists in
`UIPanelTransitionManager.panels` **[src]**. The crew panel is 500 units wide and holds a
background, `BackgroundMain`, and a content area, `anchorCrewManagement`, inset 10 units
at the sides and 44 from the top (the editor scene, read with UnityPy) **[src]**.

`PlannerPanel` copies the crew panel under an inactive holder, so that nothing of the crew
panel wakes up in the copy. It empties the content area, clears the transition's events,
lets its list reach out to 2 units short of the panel's right edge, and puts the copy
right after the crew panel among its siblings. It then adds the copy to the manager's
`panels`. So KSP brings the planner in and out as it does its own panels, with the same
background and the same transition: bringing it in takes the parts list and its categories
out, and bringing in any other panel takes the planner out. The parts list's icons, the
parts themselves drawn by a camera over the UI, go out with the list. The planner's rows
are built into the content area.

KSP still counts the parts mode as the one shown while the planner is in. Its own
`ShowPartsList` would therefore do nothing, and KSP's parts button brings the parts list
back through the panel manager itself (`PlannerPanel.BackToParts`).

Opening the planner from another mode switches KSP to the parts mode first
(`EditorLogic.SelectPanelParts`), and brings the planner in once the parts list is in, so
that the two transitions do not run against each other. Any change of mode closes it
(`GameEvents.onEditorScreenChange`), and so does KSP's parts button.

## What the window leaves to KSP

What KSP already shows in place stays KSP's. The delta-v in the staging stack, the
engineer's report and the parts list's tooltips are not shown a second time. The stage
table repeats KSP's per-stage numbers on purpose: comparing the stages needs them side by
side in one place, and since they are KSP's own, they cannot disagree.

Where Kerbal Engineer Redux is installed, `EditorEngineer` says once per game start that it
is no longer needed, since the engineer window and the planner show the same information.
Its windows stay as they are.
