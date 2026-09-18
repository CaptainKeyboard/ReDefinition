# How each mod keeps its settings

For each mod ReDefinition bundles: where its settings live while the game runs, how a
value set in ReDefinition's window reaches it and is saved, and when it takes effect.
Read from each mod's source and checked against the installed build by decompiling
where the version matters; KSP 1.12.5 decompiled. How a registration reaches these
places: the registrations in `GameData/ReDefinition/Mods`, and for what a member path
cannot say, the behaviours in `src/Settings/Behaviours`.

## Saved through

| Mod | Saved through | Kept |
|---|---|---|
| KSP | `GameSettings.SaveSettings` → `settings.cfg` | in its file |
| Scatterer | its `Scatterer_config` node, saved with `SaveConfigs` | in its file |
| EVE (Volumetric Clouds build) | the first object node of `EVE_RAYMARCHED_CLOUDS_QUALITY`, its `SaveConfig` | in its visual pack's file |
| Parallax Continued | `ParallaxSettings.SaveSettings` | in its file |
| Firefly | `SettingsManager.SaveModSettings` | in its file |
| TUFX | the main menu's profile in its `defaultConfiguration`, `SaveConfigs`; every other scene's in `TUFXGameSettings` of the loaded save | per save; ReDefinition keeps the choice and sets it into every save as it loads |
| Distant Object | `Settings.Save` | per save; as TUFX |
| Deferred, Waterfall | no save routine of their own | ReDefinition keeps the value and sets it at every start |

Values are saved once after a batch of changes, not per setting.

## Mod by mod

**KSP.** The static fields of `GameSettings`. KSP hands the quality ones to Unity in
`GameSettings.ApplySettings`: `QualitySettings.SetQualityLevel` with the render
quality, then the texture mipmap limit, pixel lights and shadow cascades. A change
from ReDefinition is followed up the way KSP's own screens finish: what
`ApplySettings` hands Unity for quality, the frame limit as `Application.targetFrameRate`,
then `GameEvents.OnGameSettingsApplied` once at the end of the frame -- the event KSP's
reflection probe, terrain scatter, aerodynamic effects and FX camera listen to, and
the one that puts the upscaler's quality overrides back (`SetQualityLevel` resets
MSAA, the LOD bias and anisotropic filtering). The terrain preset is set by its index
(`PQSCache.PresetList.SetPreset(int)`): `SetPreset(string)` never moves `presetIndex`,
which KSP's screens read and write back. Terrain detail, shader quality and scatter
are read as a planet's surface is built; surface effects as a part's module starts;
whether celestial bodies cast shadows partly only at `PSystemSetup`, so after a
restart.

**Scatterer.** `Scatterer.Instance.mainSettings`, loaded in every scene's `Awake` from
the `Scatterer_config` node. A value goes into the running object and the node in
memory at once, then the node's file is saved. Only a field Scatterer marks
`[Persistent]` can be set this way: its own save writes the node anew from those
fields only, so any other value would be dropped at the next change in its window.
`quarterResScattering` is such a field in the installed 0.908 and is left out. The
effects read the settings as they are built (`SkyNode`, `OceanNode`,
`ProlandManager`): from the next scene on. Its public release (0.878) and Volumetric
Clouds' build (0.908) differ in their fields and are told apart by the members that
exist.

Its window's keys are not among those settings: they stand on
`Scatterer.Instance.pluginData`, each as a `KeyCode` field it reads and a string field
it saves, and its window opens on either modifier with either key. Both pairs are rows
in the *Keys* tab; a change goes into all four fields and is saved with Scatterer's own
`savePluginData`.

**EVE, Volumetric Clouds build.** `Atmosphere.RaymarchedCloudsQualityManager`, fed from
`EVE_RAYMARCHED_CLOUDS_QUALITY`, copies its object's values into private statics and
rebuilds the clouds. A value goes into the static and the same key in the first object
node of its first config -- what its own window edits -- then the clouds are rebuilt
once at the end of the frame (`ReinitAll`) and its configs saved as its window saves
them. EVE applies the quality config late in the main menu, five physics frames after
its global manager starts there; ReDefinition hands the value over again every two
seconds while the main menu is up, since physics frames are not seconds. The
manager's statics sit on a generic base class, and reflection finds them only with
`BindingFlags.FlattenHierarchy`. The public EVE build has no volumetric clouds and
nothing bundled.

**Parallax Continued.** The static `Parallax.ConfigLoader.parallaxGlobalSettings`, read
once while loading and saved by its window's *Save Changes*. Its groups are structs:
a change reads the group, sets the field and writes the group back. Tessellation and
the planet shadow settings take effect at once through the calls its own window makes
(`ToolbarMenu.UpdateTerrainMaterials`, `UpdateScaledShadowMaterialSettings`), run once
at the end of the frame; part-light shadows when a part's light starts. Scatter
density and distance are baked into every scatter while its configs load; its window
undoes and redoes that (`ReverseNormalisationConversions`,
`PerformNormalisationConversions`, both public) -- ReDefinition makes the same two calls
as the next scene is asked for, since a scatter system already running keeps buffers
sized for what it was built with. While the game loads Parallax checks KSP's terrain
detail and reflection resolution in `settings.cfg` (see
[requirements.md](requirements.md)).

**Firefly.** `Firefly.ModSettings.I`, a dictionary of `ConfigField`s filled from
`ATMOFX_SETTINGS` after ModuleManager's pass. A value goes through its indexer, then
its `SaveModSettings`. Its re-entry strength and trail length are read every frame, the
bow shock with the material properties, the particles when a vessel's effects are
built. Its `disable_` switches are shown the right way round (`invert`).

**Deferred.** The `settings` instance of its main-menu add-on, never written to disk,
so ReDefinition keeps a value and sets it at every start and scene change. Its window
opens on both modifiers and the key together, so the *Keys* tab writes one modifier into
both of its modifier fields. At every
scene load Deferred sets up its screen-space reflections and hands its ambient values
to its shaders as globals. Its two reflection-probe caps are left to Deferred's config:
with a cap on, Deferred writes KSP's own reflection settings at every scene load, and
KSP saves them -- a cap switched for one run would leave that lowering for good.

**TUFX.** A post-processing profile per scene -- main menu, space centre, editor,
flight, map, IVA, tracking station. In the main menu the one in its configuration,
elsewhere the one in the save's game parameters. Its window's choice goes through
`ChangeProfileForScene`; ReDefinition writes the same places that method writes, applies
a profile at once where its scene is shown (`ApplyProfile`), and follows a choice made
in TUFX's own window through a Harmony postfix on `ChangeProfileForScene`. KSP's
`GameEvents` call handlers from the last subscribed to the first, and ReDefinition
subscribes before TUFX, so its handlers run after TUFX's in the same frame. The
profiles are known only from the running mod, so the list is read as the window opens.
Its toolbar button is made through ToolbarControl, registered as `TUFX`.

**Waterfall.** The static fields of `Waterfall.Settings`, filled once from
`WATERFALL_SETTINGS` after ModuleManager's pass and never written back. Lights and
distortion are decided as an effect is built: with the next vessel or scene.

**Distant Object.** `Settings.Instance`, read by its flares, vessel drawing and sky
darkening, kept per save: `Load` reads the loaded save's file, with none loaded the
one in its `PluginData`, and `Save` writes that same file. It reads its file whenever
a save loads, whenever its drawing components start and whenever its window reads its
values, and saves unasked when a save closes. A value goes into the fields, then
`Commit` -- which enables or disables its drawing components -- then `Save`. A Harmony
postfix on `Load` hands the choice kept in ReDefinition back over whatever file was just
read, so every save gets it as it loads; a postfix on its window's `ApplySettings`
takes a value changed there into ReDefinition's. Without the hooks the rows still work,
and a loaded save shows its own values until the next scene change.

## Toolbar buttons

* **KSP's launcher.** `ApplicationLauncherButton.VisibleInScenes` is public, and its
  setter moves the button between the visible and hidden lists. `NEVER` hides a
  button; the value it had brings it back.
* **Whose a button is:** the assembly of its click handler -- `scatterer`,
  `EVEManager`, `Firefly`, `ParallaxContinued`, `DistantObject`; a registration names
  it with `button`. A button is hidden only where the registration also names the
  mod's `window`, which ReDefinition's *Advanced* button then opens.
* **ToolbarControl** keeps its instances in `tcList`, each with a `nameSpace` and a
  `stockButton`; a button made through it is found by its namespace (`toolbarControl`
  in a registration). Blizzy's toolbar is left alone: it writes its layout to its own
  file whenever a button's visibility changes.

## The window's model

KSP's in-game settings dialog, `MiniSettings`: a `MultiOptionDialog` in the
`MiniSettingsSkin`, a scroll list with a content sizer, section buttons in the skin's
first custom style, and *Apply*, *Accept* and *Cancel* with KSP's own strings
(`#autoLOC_149512` to `#autoLOC_149514`). `DialogGUIToggle` and `DialogGUISlider` take
their state from their getters every frame, so one scroll list shows one category at
a time.
