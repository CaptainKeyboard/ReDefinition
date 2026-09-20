# How each mod keeps its settings

**For:** mod authors looking for a pattern close to their own mod, and anyone working on
ReDefinition's registrations.
**You need:** nothing.
**You get:** for every bundled mod, where its settings live while the game runs, how a
value reaches it, how it is saved, and when it takes effect.

Each entry was read from that mod's source and checked against the installed build,
decompiled where the version matters. KSP was decompiled from 1.12.5.

The registrations that reach these places are in `GameData/ReDefinition/Mods`
([modders/registering-a-mod.md](../modders/registering-a-mod.md)). A setting no member
path reaches needs a behaviour, one per mod.

## Saved through

| Mod | Saved through | Kept | `saving` |
|---|---|---|---|
| KSP | `GameSettings.SaveSettings`, into `settings.cfg` | in its file | `InModFiles` |
| Scatterer | its `Scatterer_config` node, saved with `SaveConfigs` | in its file | `InModFiles` |
| EVE, Volumetric Clouds build | the first object node of `EVE_RAYMARCHED_CLOUDS_QUALITY`, its `SaveConfig` | in its visual pack's file | `InModFiles` |
| Parallax Continued | `ParallaxSettings.SaveSettings` | in its file | `InModFiles` |
| Firefly | `SettingsManager.SaveModSettings` | in its file | `InModFiles` |
| TUFX | the main menu's profile in its `defaultConfiguration`, `SaveConfigs`; every other scene's in `TUFXGameSettings` of the loaded save | per save. ReDefinition keeps the choice and sets it into every save as it loads | `PerSave` |
| Distant Object | `Settings.Save` | per save, as TUFX | `PerSave` |
| Deferred, Waterfall | no save routine of their own | ReDefinition keeps the value and sets it at every start | `AtEveryStart` |

Values are saved once after a batch of changes, not one by one.

## KSP

The settings are static fields of `GameSettings`. KSP hands the quality ones to Unity
in `GameSettings.ApplySettings`: `QualitySettings.SetQualityLevel` with the render
quality, then the texture mipmap limit, the pixel lights and the shadow cascades.

A change from ReDefinition is followed up the way KSP's own screens finish. It hands
Unity the same quality values, sets the frame limit as `Application.targetFrameRate`,
and fires `GameEvents.OnGameSettingsApplied` once at the end of the frame. That event
is what KSP's reflection probe, terrain scatter, aerodynamic effects and FX camera
listen to. It also puts the upscaler's quality overrides back, because
`SetQualityLevel` resets MSAA, the LOD bias and anisotropic filtering.

The terrain preset is set by its index, `PQSCache.PresetList.SetPreset(int)`. The
overload taking a name never moves `presetIndex`, which KSP's screens read and write
back.

Terrain detail, shader quality and scatter are read as a planet's surface is built.
Surface effects are read as a part's module starts. Whether celestial bodies cast
shadows is partly read at `PSystemSetup` only, so that part needs a restart.

## Scatterer

The settings live in `Scatterer.Instance.mainSettings`, loaded in every scene's `Awake`
from the `Scatterer_config` node. A value goes into the running object and into the
node in memory at once, and then the node's file is saved.

Only a field Scatterer marks `[Persistent]` can be set this way. Its own save writes
the node anew from those fields alone, so any other value would be dropped at its next
change. `quarterResScattering` is such a field in the installed 0.908, and it is left
out.

The effects read the settings as they are built, in `SkyNode`, `OceanNode` and
`ProlandManager`, so a change arrives from the next scene on. The public release, 0.878,
and Volumetric Clouds' build, 0.908, differ in their fields and are told apart by the
members that exist.

Its window's keys are not among those settings. They are on
`Scatterer.Instance.pluginData`, each as a `KeyCode` field it reads and a string field
it saves, and its window opens on either modifier with either key. Both pairs are rows
in the *Keys* tab. A change goes into all four fields and is saved with Scatterer's own
`savePluginData`.

## EVE, Volumetric Clouds build

`Atmosphere.RaymarchedCloudsQualityManager` is fed from
`EVE_RAYMARCHED_CLOUDS_QUALITY`. It copies its object's values into private statics and
rebuilds the clouds.

A value goes into the static and into the same key in the first object node of its
first config, which is what EVE's own window edits. The clouds are then rebuilt once at
the end of the frame, through `ReinitAll`, and its configs are saved as its window
saves them.

EVE applies the quality config late in the main menu, five physics frames after its
global manager starts there. Physics frames are not seconds, so ReDefinition hands the
value over again every two seconds while the main menu is up.

The manager's statics sit on a generic base class, and reflection finds them only with
`BindingFlags.FlattenHierarchy`. The public EVE build has no volumetric clouds, and
nothing of it is bundled.

## Parallax Continued

The settings are the static `Parallax.ConfigLoader.parallaxGlobalSettings`, read once
while loading and saved by its window's *Save Changes*. Its groups are structs, so a
change reads the group, sets the field and writes the group back.

Tessellation and the planet shadow settings take effect at once, through the calls its
own window makes, `ToolbarMenu.UpdateTerrainMaterials` and
`UpdateScaledShadowMaterialSettings`, run once at the end of the frame. Part-light
shadows take effect when a part's light starts.

Scatter density and distance are baked into every scatter while its configs load. Its
window undoes and redoes that with `ReverseNormalisationConversions` and
`PerformNormalisationConversions`, both public. ReDefinition makes the same two calls as
the next scene is asked for, because a scatter system already running keeps buffers
sized for what it was built with.

While the game loads, Parallax checks KSP's terrain detail and reflection resolution in
`settings.cfg`. See [requirements.md](requirements.md).

## Firefly

`Firefly.ModSettings.I` is a dictionary of `ConfigField`s, filled from `ATMOFX_SETTINGS`
after ModuleManager's pass. A value goes through its indexer, and then through its
`SaveModSettings`.

Its re-entry strength and trail length are read every frame, its bow shock with the
material properties, and its particles when a vessel's effects are built. Its
`disable_` switches are shown the right way round, with `invert`.

## Deferred

The settings are the `settings` instance of its main-menu add-on, never written to
disk. ReDefinition therefore keeps a value and sets it at every start and scene change.

Its window opens on both modifiers and the key together, so the *Keys* tab writes one
modifier into both of its modifier fields.

At every scene load, Deferred sets up its screen-space reflections and hands its ambient
values to its shaders as globals.

Its two reflection-probe caps are left to Deferred's config. With a cap on, Deferred
writes KSP's own reflection settings at every scene load and KSP saves them, so a cap
switched for one run would leave that lowering in place for good.

## TUFX

TUFX applies a post-processing profile per scene: main menu, space centre, editor,
flight, map, IVA and tracking station. In the main menu it uses the profile in its
configuration, elsewhere the one in the save's game parameters.

Its window's choice goes through `ChangeProfileForScene`. ReDefinition writes the same
places that method writes, applies a profile at once where its scene is shown, through
`ApplyProfile`, and follows a choice made in TUFX's own window through a Harmony
postfix on `ChangeProfileForScene`.

KSP's `GameEvents` call handlers from the last subscribed to the first, and ReDefinition
subscribes before TUFX, so its handlers run after TUFX's in the same frame.

The profiles are known only from the running mod, so the list is read as the window
opens. TUFX's toolbar button is made through ToolbarControl and registered as `TUFX`.

## Waterfall

The settings are the static fields of `Waterfall.Settings`, filled once from
`WATERFALL_SETTINGS` after ModuleManager's pass and never written back. Lights and
distortion are decided as an effect is built, so a change arrives with the next vessel
or scene.

## Distant Object

`Settings.Instance` is read by its flares, its vessel drawing and its sky darkening, and
kept per save. `Load` reads the loaded save's file, or the one in its `PluginData` with
no save loaded, and `Save` writes that same file. It reads its file whenever a save
loads, whenever its drawing components start and whenever its window reads its values,
and it saves unasked when a save closes.

A value goes into the fields, then through `Commit`, which enables or disables its
drawing components, then through `Save`.

A Harmony postfix on `Load` hands the choice kept in ReDefinition back over whatever
file was just read, so every save gets it as it loads. A postfix on its window's
`ApplySettings` takes a value changed there into ReDefinition's. Without the hooks the
rows still work, and a loaded save shows its own values until the next scene change.

## The mods' toolbar buttons

ReDefinition hides a bundled mod's toolbar button while its settings are in the window,
and only where the registration also names the mod's `window`, which the *Advanced*
button then opens.

* A button on KSP's launcher is hidden with `VisibleInScenes = NEVER`, and the value it
  had brings it back.
* Whose a button is: the assembly of its click handler, such as `scatterer`,
  `EVEManager`, `Firefly`, `ParallaxContinued` or `DistantObject`. A registration names
  it with `button`.
* A button made through ToolbarControl is found by the namespace a registration names
  with `toolbarControl`.
* Blizzy's toolbar is left alone. It writes its layout to its own file whenever a
  button's visibility changes.
