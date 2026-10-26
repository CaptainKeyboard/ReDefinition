# The features on screen

**For:** a developer who adds or changes a feature that draws into KSP's interface or scenes.
**You need:** the repository, and a flight, an editor or a main menu to look at.
**You get:** where each feature starts, what switches it, and the rules every control follows.

ReDefinition adds features that are not part of the upscaler: HUD additions, the editor's
engineer and planner, interface scaling, the main-menu scene and the space centre's look. Each
is a `KSPAddon` of its own for the scene it works in. Most read one switch from `OwnSettings`.
Read this page to find the add-on of a feature, and the conventions a new one has to follow.
The dashboard has [its own page](flow-dashboard.md).

## The features and their switches

The setting is the key in `settings.cfg` and in `OurModules`. [Startup and
scenes](flow-startup.md) lists every add-on, including the ones without a switch.

| Feature | Scene | Entry file | Setting |
|---|---|---|---|
| Flight panel with profiles | Flight | `src/Hud/Panel/FlightPanel.cs` | `flightPanel`, `flightPanelAuto` |
| Round gauge | Flight | `src/Hud/RoundGauge.cs` | `roundGauge` and `navballSpeeds` |
| Vertical and horizontal speed | Flight | `src/Hud/NavballSpeeds.cs` | `navballSpeeds` |
| Docking port marker | Flight | `src/Hud/NavballDockingMarker.cs` | `navballDockingMarker` |
| Navball dragged to a new place | Flight | `src/Hud/NavballDrag.cs` | `navballDrag` |
| Altimeter follows the ground | Flight | `src/Hud/AutoAltimeter.cs` | `autoAltimeter` |
| Flight starts in maneuver mode | Flight | `src/Hud/FlightStartMode.cs` | `flightStartsInManeuverMode` |
| Stage details in columns | Flight, editors | `src/Hud/StageInfoColumns.cs` | `stageDetailColumns`, `stageTwrRange` |
| Delta-v tools in the staging stack | Flight, editors | `src/Hud/DeltaVTools.cs` | `deltaVTools` |
| Tooltips and the staging reset question | Flight, editors | `src/Hud/HudTooltips.cs` | `hudTooltips`, `stagingResetAsks` |
| Crew slot buttons | Flight | `src/Hud/CrewSlotButtons.cs` | `crewSlotButtons` |
| Better Time Warp in the time panel | Flight, space centre | `src/Hud/TimeWarpBar.cs` | `timeWarpPanelButton`, `timeWarpToolbarButton` |
| Buttons over the buildings | Flight, space centre, editors | `src/Hud/WorldButton.cs` | `worldButtons` |
| Editor engineer and planner | Editors | `src/Engineer/EditorEngineer.cs` | none |
| Scaling of KSP's fixed-pixel drawing | Flight, space centre, tracking station | `src/Scaling/ScalingAddon.cs` | KSP's UI scale |
| Scaling of other mods' windows | every scene | `src/Scaling/ModWindowScale.cs` | `scaleModWindows` |
| Fireworks behind the menu | Main menu | `src/Menu/MenuFireworks.cs` | `menuFireworks` |
| Zoom from the menu to the space centre | Main menu, space centre | `src/Menu/KscArrival.cs` | `menuKscZoom` |
| Grass on the lawns | Flight, space centre | `src/Scenery/KscGrass.cs` | `kscGrass` |
| Lawns drawn as terrain | Flight, space centre | `src/Scenery/KscGround.cs` | `kscLawnTerrain` |
| Re-entry effects skipped while unseen | Flight | `src/Scenery/FireflyIdle.cs` | `fireflyIdle` |
| Runway lights dimmed | Flight, space centre | `src/Scenery/RunwayLights.cs` | none |

## How a feature gets its setting

1. `OwnSettings` holds the key as a field, flagged live in `OwnSettings.Fields`.
2. `ReDefinitionAddon.LoadSettings` copies the live fields into the object that
   `ReDefinitionAddon.Live` returns (`OwnSettings.CopyLive`). *Accept* copies the changed ones
   (`OwnSettings.ChangeLive`). [A setting's path](flow-setting.md) follows that route.
3. A HUD feature asks `FlightUiSwitch.On(s => s.RoundGauge)` in `src/Hud/FlightUiSwitch.cs`. It
   returns the field of `Live`, and true when `ReDefinitionAddon.Instance` does not exist.
4. The scenery and menu features have a private `Enabled` that reads `addon.Live` the same way,
   for example `MenuFireworks.Enabled` and `KscGrass.Enabled`.
5. `AutoAltimeter.Update` reads `addon.Live.AutoAltimeter` itself and treats a missing add-on
   as off.
6. The scaling add-ons read KSP's own `GameSettings.UI_SCALE` through `UiScaling.World`. Only
   `UiScaling.ModWindows` reads `Live.ScaleModWindows`.

A row with `TakesEffect = ApplyWindow.NextScene` in `OurModules.FlightUi` is read when the
scene starts, mostly in the add-on's `Start` or in a hook that runs once. The others are read
each time the feature acts.

## The toolkit in src/Ui

The HUD and editor features build uGUI objects of KSP's own. They use the helpers in `src/Ui`:

- `UiHygiene.Child` makes an object on KSP's UI layer under a parent.
- `UiHygiene.MouseOnly` takes a control off keyboard navigation (see the rules below).
- `UiHygiene.Tooltip` puts KSP's tooltip prefab on an object.
- `ConfirmDialog` asks a question with KSP's *Cancel*, as `AssemblyReturn` does before it
  leaves a flight.
- `ScreenFit`, `UnityMouseEvents.Shield` and `DialogOverWindow` keep a dialog on the screen and
  clicks out of the game.
- `TooltipText.WrapAll` breaks the tooltips of a dialog's elements at 83 characters.
- `IconSprites.Load` makes a sprite of a texture under `GameData/ReDefinition/Icons`, as the
  planner's button does.
- `WorldButton` in `src/Hud` stands a button in the world and takes its look from `KscButtons`.

`RoundGauge`, `WorldButton` and the planner's `PlannerWidgets` use `UiHygiene` most.

## The tooltip and escape rules

1. **Navigation.** KSP's event system presses a selected control again on Space or Return.
   Every control made here goes through `UiHygiene.MouseOnly`, so the staging key cannot press
   it twice.
2. **Layer.** An object made at run time is on layer 0. `UiHygiene.Child` gives it the parent's
   layer.
3. **Tooltips on the HUD and in the editors.** A name or a few words in title case, such as
   "Toggle SAS", through `UiHygiene.Tooltip`. Rows of a settings dialog explain in sentences.
4. **Tooltips in dialogs.** `DialogTooltips.Attach` gives each element with `tooltipText` KSP's
   controller, and `TooltipText` breaks long lines because KSP draws a tooltip on one line.
5. **Escape.** An Escape that ends a key capture or a text field must not also close the dialog
   or open the pause menu. `EscapeGuard.Install` patches `PopupDialog.Update` for this.
6. **Dialogs of ReDefinition.** Register them with `EscapeGuard.Own`, as `SettingsWindow.Open`
   does.
7. **Text fields.** `UiHygiene.Typing` tells the hotkeys to wait while a field has the
   keyboard. `UiHygiene.ReleaseEnded` lets go of a field whose edit has ended.
8. **Greyed controls.** `UiHygiene.DeferInteractable` wraps a dialog's interactable conditions
   so a control that is hidden at the moment of the change is still set when it shows.

## Where to change it

- The switch of a feature: its field and `Fields` entry in `src/Modules/OwnSettings.cs`, and
  its row in `OurModules.FlightUi` (see [add a setting](howto-add-a-setting.md)).
- The scene a feature runs in: the `[KSPAddon]` attribute on its class.
- A rule for every control: `src/Ui/UiHygiene.cs` or `src/Ui/EscapeGuard.cs`.

## What can go wrong

- A feature whose hook fails keeps KSP's own behaviour and logs the cost, for example "The
  flight panel keeps KSP's orbit tabs" or "Could not add the entry to the main menu".
- A control built without `UiHygiene.MouseOnly` is pressed again by the staging key.
- A tooltip not found logs "A control has no tooltip" (`CompatibilityLog` kind `tooltip`).
- HUD buttons that KSP builds late are searched for 30 seconds. `HudTooltipsFlight` then logs
  "Not found in 30 s, so without a tooltip".
