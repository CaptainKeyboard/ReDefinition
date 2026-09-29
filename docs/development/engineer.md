# The planner's place in the editor

**For:** anyone changing how the planner sits in the VAB and the SPH.
**You need:** the repository, and `src/Engineer` open.
**You get:** where the planner's numbers come from, and how its button and its panel
sit in KSP's editor.

The player's side is in [player/planner.md](../player/planner.md). How the planner works
out a route is in [planner.md](planner.md).

## Where the numbers come from

The numbers are KSP's own. Since 1.6, KSP simulates the staging of the vessel in the
editor itself, in `VesselDeltaV` on `ShipConstruct.vesselDeltaV` **[src]**. Its
`OperatingStageInfo` holds one `DeltaVStageInfo` per stage. Each has delta-v, specific
impulse, thrust and TWR, each at sea level, in vacuum and in the chosen situation,
together with its start, end and dry mass and its burn time **[src]**. The situation is
`DeltaVGlobals.DeltaVAppValues`: a body, sea level, an altitude or vacuum. KSP's delta-v
app writes it, and KSP's staging stack follows it **[src]**. If the body has no
atmosphere, `EditorReadout` takes vacuum.

While the planner is open, `EditorReadout` reads the simulation four times a second. It
adds what KSP does not give:

| Worked out | How |
|---|---|
| the situation's figures | delta-v and thrust as KSP's staging stack picks them for the situation: at sea level, at the app's altitude (KSP's "Actual") or in vacuum (`DeltaVStageInfo.GetSituationDeltaV`, `GetSituationThrust`) **[src]** |
| thrust at the surface | each stage's thrust at the sea level of every body with air (`ModuleEngines.MaxThrustOutputAtm`) **[src]** |
| solid stages | a stage whose engines cannot be shut down or throttled (`ModuleEngines.throttleLocked`, `allowShutdown`) **[src]** |
| electric charge | what a stage's engines burn at full thrust, and what the vessel holds |
| every part | its place in the part tree, what it is and its masses (`PartFacts`), for the planner's sections and its own simulation ([planner.md](planner.md)) |
| mass | `ShipConstruct.GetShipMass` with `ShipConstruction.ShipManifest`, so the crew and their inventory count as KSP's mass limit counts them **[src]** |

`StageTable` and `EngineerFormat` do not depend on Unity and have unit tests
(`tests/ReDefinition.Tests/EngineerTests.cs`).

## How the planner button sits in the editor

KSP's mode buttons are four `Button`s in the editor's top bar: `partPanelBtn`,
`actionPanelBtn`, `crewPanelBtn` and `cargoPanelBtn` in `EditorLogic` **[src]**. They
are 48 units wide, placed by hand rather than by a layout group. Each draws its tile as
its own sprite with sprite swap, and keeps its aspect (`Image` with `preserveAspect`). A
tile's sprite is 128 by 128 pixels, and its picture, 107 high, sits 19 pixels under the
top (the sprite's texture rectangle and its offset). The editor scene shows this
(`sharedassets6`, read with UnityPy) **[src]**. The active mode's button is the one KSP
makes not interactable, so its disabled sprite is the active tile. KSP places the
buttons once, from the anchors `crewBtnOrigPos`, `cargoBtnOrigPos` and
`editorBtnOrigPos` **[src]**.

`EditorEngineer` copies the cargo button and puts the copy right before it among its
siblings. The buttons overlap, and KSP's order draws each over the left edge of the one
to its right: the parts button, leftmost, comes last **[src]**. Every frame, before the
canvases draw, it sets the copy one step right of the last visible mode button and the
switch-editor button one step further. It gives the copy the cargo button's size,
anchors, scale and image settings. The first frame writes both buttons' measures to
`KSP.log`, in a line that starts with `Mode buttons:`. KSP sets its buttons'
interactable state only on events, such as a changed input lock or a mode switch
(`EditorLogic.UpdateUI`) **[src]**. So while the planner is open, `EditorEngineer`
keeps the parts button clickable. On closing, it hands the buttons back to `UpdateUI`.

The copy's tiles are made in the game from the cargo button's tiles as they are
(`ModeButtonTiles`). A UI theme such as ZTheme replaces those through HUDReplacer, which
swaps the pixels of a texture by its name as the scene's UI is ready
(`onLevelWasLoadedGUIReady`). The cargo tiles are one texture, `VABCargo_Button`, with a
sprite per state (HUDReplacer's and ZTheme's sources, read) **[src]**. So the copy has
the theme's flat tiles if ZTheme is installed, and KSP's own if it is not. For each
state, the tile is copied through a render texture, since textures are not readable from
the CPU. Each row of its face is painted over between the face's own colours at its
edges, which takes the cargo glyph off. "Δv" is laid on in the colour of that glyph,
centred on the face at two thirds of its height, from a mask drawn by
`tools/make_icon.py` (`GameData/ReDefinition/Icons/EngineerGlyph_Fill.png`). The tiles
are made again after the scene's UI is ready and a second later, whichever comes after
the theme. If a tile cannot be made, the copy falls back to the tiles
`tools/make_icon.py` draws after KSP's (`EngineerButton_*.png`).

## How the planner panel sits in the editor

KSP's four panels in the editor are `UIPanelTransition`s that its panel manager,
`EditorPanels.panelManager`, brings in and out (`EditorPanels.ShowCrewAssignment` and
its siblings) **[src]**. Bringing one in takes out every other panel the manager lists
in `UIPanelTransitionManager.panels` **[src]**. The crew panel is 500 units wide and
holds a background, `BackgroundMain`, and a content area, `anchorCrewManagement`, inset
10 units at the sides and 44 from the top (the editor scene, read with UnityPy)
**[src]**.

`PlannerPanel` copies the crew panel under an inactive holder, so that nothing of the
crew panel wakes up in the copy. It empties the content area, clears the transition's
events, lets its list reach out to 2 units short of the panel's right edge, and puts the
copy right after the crew panel among its siblings. It then adds the copy to the
manager's `panels`. So KSP brings the planner in and out as it does its own panels, with
the same background and the same transition. Bringing it in takes the parts list and its
categories out, and bringing in any other panel takes the planner out. The parts list's
icons, the parts themselves drawn by a camera over the UI, go out with the list. The
planner's rows are built into the content area.

KSP still counts the parts mode as the one shown while the planner is in. Its own
`ShowPartsList` would therefore do nothing, and KSP's parts button brings the parts list
back through the panel manager itself (`PlannerPanel.BackToParts`).

Opening the planner from another mode switches KSP to the parts mode first
(`EditorLogic.SelectPanelParts`). The planner comes in once the parts list is in, so
that the two transitions do not run against each other. Any change of mode closes it
(`GameEvents.onEditorScreenChange`), and so does KSP's parts button.

## The notice about Kerbal Engineer Redux

If Kerbal Engineer Redux is installed, `EditorEngineer` says once per game start that it
is no longer needed: KSP's own staging stack and the planner show the same information.
Its windows stay as they are.
