# UI scaling of what KSP leaves unscaled

**For:** anyone changing `src/Scaling`.
**You need:** the repository, and a decompile of KSP's `Assembly-CSharp.dll`.
**You get:** how KSP applies its UI scale, what it leaves at a fixed number of pixels,
how each piece is scaled here, and which mods' scaling of it is turned off.

The player's side is in [player/ui-scaling.md](../player/ui-scaling.md).

## How KSP applies its UI scale

`UIMasterController.SetScale` sets `Canvas.scaleFactor` to `GameSettings.UI_SCALE` on the
main, action, message, dialog, tooltip, tweening, drag and debug canvases, and the app
canvas to that times `UI_SCALE_APPS` **[src]**. The UI camera's orthographic size is half
the screen's height, so one world unit is one pixel. The flight's element scales
(`UI_SCALE_TIME`, `_ALTIMETER`, `_MAPOPTIONS`, `_STAGINGSTACK`, `_MODE`, `_NAVBALL`,
`_CREW`) multiply the transforms of those panels in `FlightUIModeController`, while
`UIELEMENTSCALINGENABLED` is on. `GameEvents.onUIScaleChange` fires only from KSP's
settings screen. KSP sets `GUI.matrix` nowhere, so no IMGUI window is ever scaled.

ReDefinition's settings window sets `UI_SCALE` as KSP's settings screen does: after
`SetScale` and `SetAppScale` it fires `onUIScaleChange` and resets the map's node
container (`KspBehaviour.ApplyEngine`), so that the labels, part action windows and other
mods follow a new scale at once.

## What stays at a fixed number of pixels, and how it is scaled

`UiScaling.World` is `GameSettings.UI_SCALE` times the player's share
(`OwnSettings.WorldUiScale`, 100 % by default).

| What | KSP's own **[src]** | Here |
|---|---|---|
| map icons and captions | `MapNode.Create` sizes the icon in pixels (14 to 32); every node sits in one container scaled by `1 / uiScale` (`MapViewCanvasUtil.ResetNodeCanvasContainerScale`), placed in pixels; captions pushed apart by their `textBounds` in their own units (`MoveAwayFromEachOthers`) | each node's own transform scaled about its centre, every frame (`MapNodeScale`); KSP only moves it. A postfix on `UpdateCaptionText` scales `textBounds` by the node's scale |
| vessel labels | `BaseLabel.ResetParent` keeps the label at a world scale of 1; placed by `anchoredPosition` in canvas units | each label's scale set to `World` over its parent's lossy scale (`FlightScaling`) |
| maneuver gizmo | `ManeuverGizmoBase.screenSize`, from the prefab, over `Screen.height`; set to 0 while it is minimised | each gizmo's own size as it appears, times `World`; one at 0 left alone (`ManeuverGizmos`) |
| editor move and rotate gizmos | `ScreenSpaceObjectScaling.screenSize`, from the prefab, over `Screen.height` | postfix on `GizmoOffset.Attach` and `GizmoRotate.Attach` multiplies it once (`ScalingHooks`) |
| orbit lines | `OrbitRendererBase.MakeLine`: 5 px; `OrbitTargeter.DottedLineUpdate`: 5 px; `PatchRendering.MakeVector`: its `lineWidth` | postfixes set the Vectrosity line's width to KSP's times `World` |
| CommNet lines | `CommNetUI.lineWidth2D` 4 and `lineWidth3D` 1, applied every frame | the fields set from KSP's own times `World` (`CommNetLines`) |
| R&D tree | in the R&D canvas under the main canvas, so at the UI scale; opens at `UIGridArea.zoom` 1 with `zoomMax` 1, the zoom-in button enabled by `zoom != zoomMax` | opened at 1.5 through `ZoomTo(1.5, true)`, `zoomMax` 2.5, both buttons enabled (`SpaceCentreScaling`) |
| R&D tree nodes | `RDNode.Setup` builds the node at the scene's root at `RDNode.scale` from the tech tree (0.6 in the stock tree) in pixels, moves it under `RDNodeRoot` keeping its size, and takes its width and height, where its arrows meet it, from the same scale: the node is as many pixels at any UI scale and zoom | prefix and postfix on `Setup`: the scale times `RDNodeRoot`'s lossy scale (UI scale times zoom) times `NodeShare`, 2 / 2.25, for a tree node's Setup only, then its own again, so that a saved tree keeps it (`RnDNodeHooks`) |
| R&D parts list | `RDPartList.partTransformMask`'s `GridLayoutGroup` lays the tiles out at the prefab's cell size | each tile scaled 1.2 about its centre, the grid's spacing grown by the cell times 0.2 and its padding by 0.1 of it on each side, its columns divided by 1.2 (`SpaceCentreScaling`) |
| crew portrait room | `GetMaxGalleryCapacity` divides the screen's pixels by the portrait's width in canvas units | postfix divides by the width at the UI and crew scales |
| other mods' IMGUI windows | never scaled | `ModWindowScale`, below |

## Other mods' windows

Unity keeps the `GUI.matrix` a window is registered with and draws the window with it,
mapping the pointer back through it. `ModWindowScale` patches every `GUI.Window`,
`GUI.ModalWindow` and `GUILayout.Window`, which all take the id, the rect and the window
function first. Its prefix sets a matrix that scales by `UI_SCALE` about the window's top
left corner, shifted back onto the screen where the grown window would leave it. Its
postfix notes where the window is drawn and puts the previous matrix back, and a finalizer
does so where the call throws. The prefix runs first (Harmony priority 800) and the postfix
last (0), so that ClickThroughBlocker's postfix and `ModWindowClose` on `GUI.DoWindow`
within see the window where it is drawn. ClickThroughBlocker's default shield tests the
rect the mod passed in, the unscaled one, so ReDefinition's own shield
(`UnityMouseEvents`) covers the scaled windows as it covers the bundled mods' windows.

It leaves alone:

* a window registered while `GUI.matrix` is not the identity;
* windows whose function comes from Unity or KSP;
* ReDefinition's own;
* mods that scale their windows with a matrix of their own, which is the identity at their
  own scale of 1: MechJeb, Kerbal Alarm Clock, RCSBuildAid, Janitor's Closet, CameraTools;
* mods that multiply their window size or text by `UI_SCALE` themselves: Kopernicus,
  KSPCommunityFixes, RemoteTech, KSP Craft Organizer.

`ScalingHookTests` resolves every method `ScalingHooks` patches, and every postfix
parameter Harmony binds by name, against KSP's `Assembly-CSharp.dll`.

A change of the share shows at once on the map, the labels, CommNet's lines, the target's
line and the maneuver gizmo. The orbit and patch lines take it when KSP makes them anew,
the editor's gizmos when they next appear.

## Other mods' scaling turned off

ReDefinition's pieces always run. `OtherScalers` patches, as the game starts, the part of
each mod below that scales the same thing, where that mod's type is loaded. The other
mods' `Start`s run in the main menu or later, so they are patched before they run. Each
mod is patched on its own; one that fails is logged under `other-scalers` and leaves the
others patched.

| Mod | Type | Patched |
|---|---|---|
| 4kSP, 4kSP Expanded | `FourkSP.FourkSP` | `UpdateNodes` and `AddToolbarButton` skipped |
| 4kSP Expanded, R&D | `_4kSP_RnD.RDScalerHarmonyBootstrap`, `_4kSP_RnD.RDSceneScaler` | `Start` skipped: no Harmony patch, no R&D hook; its larger parts-list tiles, zoom to 5 and space centre toolbar window go with them |
| 4kSP Expanded, windows | `_4kSP_ModWindows.ModWindowScalerHarmonyBootstrap` | `Start` skipped: no Harmony patch |
| Maneuver Node Evolved | `BetterManeuvering.ManeuverController` | a transpiler turns each store to `ManeuverGizmoBase.screenSize` in `LateUpdate` and `gizmoSpawn` into a call to `OtherScalers.Store`: an open gizmo keeps ReDefinition's size, one KSP collapsed to 0 reopens at it, which keeps *Force Maneuver Node Open*; `baseScale`, `maxScale` and `dynamicScaling` have no effect |

## Left as KSP draws them

* The crew portraits' render textures, 256 pixels (`Kerbal.Start`, `KerbalEVA.OnStart`):
  they blur only where the UI scale times the crew scale is above 2, and the texture is
  handed to the portrait and to up to four cameras that IVA mods such as RasterPropMonitor
  and FreeIva reach as well.
* The editor's staging stack takes `UI_SCALE` but not `UI_SCALE_STAGINGSTACK`, which KSP
  applies in flight only.
* KSP's own IMGUI windows: the legacy debug windows only.
* The fuel flow overlay's text, the main menu's buttons and the building labels at the
  space centre are 3D objects in their scene, not pixels on the screen.
* The hardware cursor.

WaypointManager and DistantObject scale their own markers and labels by `UI_SCALE`;
nothing here touches them. KSP.log says once per scene what is scaled, and once per game
which mod's scaling is turned off, in lines that start with `UI scaling:`. A method of
another mod not found, or no size store found in Maneuver Node Evolved, is a warning
under `other-scalers`.
