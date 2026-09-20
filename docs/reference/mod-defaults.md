# Every setting, its default and its kind

**For:** anyone checking what *Reset to defaults* will set, and pack authors looking
for the value a profile starts from.
**You need:** nothing.
**You get:** one row per setting of each bundled mod and of KSP's graphics settings,
with its default, its kind and where the value was read.

This is the inventory *Reset to defaults* and the profiles are built on. The values
are in each mod's registration in `GameData/ReDefinition/Mods`, as `default` and in
`DEFAULTS` blocks.

A key in the tables below is the mod's own. ReDefinition's key for it is the mod's id, a
dot and that key, such as `scatterer.oceanFoam`. That is the key a `PROFILE` block or a
ModuleManager patch names
([modders/registration-reference.md](../modders/registration-reference.md)).

A value here is the one a config file holds. KSP's settings are numbers there, and the
window shows their names: `TEXTURE_QUALITY = 1` is *Half*,
`REFLECTION_PROBE_TEXTURE_RESOLUTION = 1` is *256*, `AERO_FX_QUALITY = 3` is *Normal*.
[player/graphics-profiles.md](../player/graphics-profiles.md) uses the names.

## What a default is here

A mod's default is the value its release ships for the installed version: its settings
file as released, and its code's own value where that file has none. It is never the
installed file, because ReDefinition, the player and visual packs write that one.

With Volumetric Clouds installed, what its author ships is the default. That package
holds its own builds of EVE and Scatterer with their configs, and its Readme asks for
its TUFX profile in every scene. It changes no other mod's defaults.

KSP's default is what the *Reset* of KSP's own settings screen sets, through
`GameSettings.ResetSettings`, which calls `SetDefaultValues` and
`PQSCache.CreateDefaultPresetList`. Only the graphics settings count here, the ones
KSP's graphics screen lists in `sharedassets3.assets`, and without resolution and full
screen.

## The three kinds

| Kind | Means | Profiles |
|---|---|---|
| quality | how good and how costly the picture is | a `PROFILE` block sets these |
| taste | the look an author or a visual pack chose | never set by a `PROFILE` block |
| other | interface, input, debug, physics and compatibility switches, and the antialiasing the upscaler owns | never set by a `PROFILE` block |

An `ALL_PROFILES` block may set a setting of any kind, for what a mod needs changed to
run with the upscaler. *Reset to defaults* sets every kind.

## Sources

| Mod | Default from | Checked against the build installed for development |
|---|---|---|
| KSP 1.12.5 | `Assembly-CSharp` decompiled: `GameSettings.SetDefaultValues`, `PQSCache.CreateDefaultPresetList`; graphics screen fields in `sharedassets3.assets` | -- |
| Scatterer 0.878 (public) | `Scatterer-release`: `config/config.cfg`; `MainSettingsReadWrite` decompiled for fields the file leaves out | not installed |
| Scatterer 0.908 (Volumetric Clouds) | `RaymarchedVolumetrics`: `config/config.cfg`; `MainSettingsReadWrite` decompiled | `Scatterer.dll` identical (MD5) |
| EVE 3.2.2 (Volumetric Clouds) | `RaymarchedVolumetrics`: `StockVolumetricClouds/raymarchedClouds.cfg`; `RaymarchedCloudsQuality`, `LightVolumeSettings` decompiled | `Atmosphere.dll` identical |
| EVE 1.11.7 (public) | it has no volumetric clouds, and nothing of it is bundled here | not installed |
| Parallax Continued 1.0.4 | `Parallax-release` = CKAN zip: `Config/ParallaxGlobalSettings.cfg`; `Common.cs` defaults | `ParallaxContinued.dll` identical |
| Firefly 1.0.6 | CKAN zip: `ModSettings.cfg`; `SettingsManager.cs` defaults | `Firefly.dll` identical; installed file unchanged |
| Deferred 1.3.5.0 | `Deferred-release` = CKAN zip: `Deferred.cfg`; `Settings.cs` defaults | `Deferred.dll` identical; installed file unchanged |
| TUFX 1.1.1 | `TUFX-release` = CKAN zip: `TUFX.cfg`; `TexturesUnlimitedFXLoader.Configuration` decompiled | `TUFX.dll` identical |
| Waterfall 0.11.0 | CKAN zip: `WaterfallSettings.cfg`; `Waterfall.Settings` decompiled | `Waterfall.dll` identical; installed file unchanged |
| Distant Object 2.2.1.7 | CKAN zip: `Settings.cfg` (defaults node), `PluginData/Settings.cfg` (the file it starts from, and what a save without its own loads); class defaults decompiled | `DistantObject.dll` identical; installed file unchanged |

## KSP (graphics)

| Key | Kind | Default | Note |
|---|---|---|---|
| `QUALITY_PRESET` | quality | 5 | |
| `TEXTURE_QUALITY` | quality | 1 | |
| `LIGHT_QUALITY` | quality | 8 | Deferred raises Unity's count to at least 64 |
| `SHADOWS_QUALITY` | quality | 4 | |
| `CELESTIAL_BODIES_CAST_SHADOWS` | quality | True | |
| `terrainDetail` | quality | Default | the PQS preset |
| `TERRAIN_SHADER_QUALITY` | quality | *none* | KSP's reset does not set it, and no code of KSP assigns it |
| `PLANET_SCATTER` | quality | False | |
| `PLANET_SCATTER_FACTOR` | quality | 0.5 | |
| `AERO_FX_QUALITY` | quality | 3 | Firefly sets it to its lowest itself |
| `SURFACE_FX` | quality | True | |
| `REFLECTION_PROBE_REFRESH_MODE` | quality | 0 | Deferred raises Off to Low |
| `REFLECTION_PROBE_TEXTURE_RESOLUTION` | quality | 1 | |
| `SYNC_VBL` | other | 1 | the monitor's |
| `FRAMERATE_LIMIT` | other | 120 | the monitor's |
| `ANTI_ALIASING` | other | 2 | the upscaler switches MSAA off while it runs |
| `AMBIENTLIGHT_BOOSTFACTOR` | taste | 0 | |
| `AMBIENTLIGHT_BOOSTFACTOR_MAPONLY` | taste | 0 | |
| `AMBIENTLIGHT_BOOSTFACTOR_EDITONLY` | taste | 0 | |
| `FALLBACK_UNDERWATER_MODE` | other | 1 | |
| `HIGHLIGHT_FX` | other | True | |
| `INFLIGHT_HIGHLIGHT` | other | True | |
| `PART_HIGHLIGHTER_BRIGHTNESSFACTOR` | other | 1 | |
| `CONIC_PATCH_DRAW_MODE` | other | 3 | |
| `CONIC_PATCH_LIMIT` | other | 3 | |
| `ORBIT_FADE_STRENGTH` | other | 1 | |
| `ORBIT_FADE_DIRECTION_INV` | other | False | |
| `ALWAYS_SHOW_TARGET_APPROACH_MARKERS` | other | False | |
| `COMMNET_LOWCOLOR_BRIGHTNESSFACTOR` | other | 0.5 | |

## Scatterer

"--" means the build has no such field. "not kept" means the mod does not save the setting, so
ReDefinition drops the row and names it in the log.

| Key | Kind | Public 0.878 | Volumetric Clouds 0.908 |
|---|---|---|---|
| `autosavePlanetSettingsOnSceneChange` | other | False | False |
| `disableAmbientLight` | taste | True | True |
| `integrateWithEVEClouds` | taste | True | True |
| `overrideNearClipPlane` | other | False | False |
| `nearClipPlane` | other | 0.21 | 0.21 |
| `useOceanShaders` | quality | True | True |
| `oceanFoam` | quality | True | True |
| `oceanTransparencyAndRefractions` | quality | True | True |
| `shadowsOnOcean` | quality | True | True |
| `oceanSkyReflections` | quality | True | True |
| `oceanScreenSpaceReflections` | quality | -- | True |
| `oceanCaustics` | quality | True | True |
| `oceanLightRays` | quality | True | True |
| `oceanCraftWaveInteractions` | other | True | True |
| `oceanCraftWaveInteractionsOverrideWaterCrashTolerance` | other | True | -- |
| `buoyancyCrashToleranceMultOverride` | other | 3.6 | -- |
| `oceanCraftWaveInteractionsOverrideDrag` | other | True | -- |
| `oceanCraftWaveInteractionsOverrideRecoveryVelocity` | other | True | True |
| `waterMaxRecoveryVelocity` | other | 5 | 5 |
| `oceanPixelLights` | quality | False | **True** -- its ocean is drawn deferred, lights cost nothing there (its changelog, 3.0.0) |
| `fullLensFlareReplacement` | taste | True | True |
| `sunlightExtinction` | taste | True | True |
| `underwaterLightDimming` | taste | True | True |
| `showMenuOnStart` | other | False | False |
| `useEclipses` | quality | True | True |
| `useRingShadows` | quality | True | True |
| `d3d11ShadowFix` | other | True | True |
| `useRaymarchedCloudGodrays` | quality | -- | True |
| `useRaymarchedTerrainGodrays` | quality | -- | False |
| `raymarchedGodraysStepCount` | quality | -- | 50 |
| `raymarchedGodraysScreenshotDenoisingIterations` | other | -- | 10 |
| `useLegacyTerrainGodrays` | other | False | False |
| `useSubpixelMorphologicalAntialiasing` | other | True | True |
| `smaaQuality` | other | 2 | 2 |
| `useTemporalAntiAliasing` | other | True | True |
| `taaStationaryBlending` | other | 0.9 | **0.75** |
| `taaMotionBlending` | other | 0.65 | 0.65 |
| `taaJitterSpread` | other | 0.8 | 0.8 |
| `taaSharpness` | other | 0.28 | 0.28 |
| `disableTaaBelowFrameRateThreshold` | other | 26 | 26 |
| `terrainShadows` | quality | False | False |
| `scatteringTonemapper` | taste | 2 | 2 |
| `unifiedCamShadowsDistance` | quality | 50000 | 50000 |
| `unifiedCamShadowNormalBiasOverride` | other | 0 | 0 |
| `unifiedCamShadowBiasOverride` | other | 0 | 0 |
| `unifiedCamShadowResolutionOverride` | quality | 8192 | 8192 |
| `unifiedCamShadowCascadeSplitsOverride` | other | 0.0015, 0.015, 0.15 | 0.0015, 0.015, 0.15 |
| `dualCamShadowsDistance` | quality | 50000 | 50000 |
| `dualCamShadowNormalBiasOverride` | other | 0.72 | 0.72 |
| `dualCamShadowBiasOverride` | other | 0.5 | 0.5 |
| `dualCamShadowResolutionOverride` | quality | 0 | 0 |
| `dualCamShadowCascadeSplitsOverride` | other | 0.005, 0.025, 0.125 | 0.005, 0.025, 0.125 |
| `quarterResScattering` | quality | not kept | not kept |
| `useDithering` | taste | True | **False** |
| `m_fourierGridSize` | quality | 128 | **256** |
| `oceanMeshResolution` | quality | 6 | 6 |
| `useLowResolutionAtmosphere` | other | False | False |

The SMAA and TAA rows are Scatterer's antialiasing: every graphics profile sets both
off (`ALL_PROFILES`), and while a profile is chosen ReDefinition also switches their
components off at run time, whatever the file says.

## EVE, Volumetric Clouds build only

| Key | Kind | Default | Note |
|---|---|---|---|
| `temporalUpscaling` | quality | x9 | the pack's file sets none; the class's |
| `nonTiling3DNoise` | quality | True | |
| `renderCloudsInReflectionProbes` | quality | True | |
| `mapViewCloudFade` | taste | True | |
| `ambientVolume` | other | 1 | sound |
| `lightningVolume` | other | 1 | sound |
| `screenshotModeDenoisingIterations` | other | 8 | screenshots |
| `lightVolumeSettings.horizontalResolution` | quality | 224 | the pack's; the class has 256 |
| `lightVolumeSettings.verticalResolution` | quality | 32 | |
| `lightVolumeSettings.stepCount` | quality | 50 | |
| `lightVolumeSettings.directLightTimeSlicing` | quality | 12 | the pack's; the class has 8 |
| `lightVolumeSettings.ambientLightTimeSlicing` | quality | 32 | |
| `lightVolumeSettings.timewarpRateMultiplier` | quality | 3 | the pack's `maxTimewarpUpdateRateIncrease` is no field of this build |

## Parallax Continued

| Key | Kind | Default | Its window's cost note |
|---|---|---|---|
| `maxTessellation` | quality | 64 | moderate |
| `tessellationEdgeLength` | quality | 4 | moderate |
| `maxTessellationRange` | quality | 30 | low |
| `advancedTextureBlending` | quality | True | very low |
| `ambientOcclusion` | quality | True | very low |
| `densityMultiplier` | quality | 1 | high |
| `rangeMultiplier` | quality | 1 | high |
| `fadeOutStartRange` | quality | 0.8 | low |
| `collisionLevel` | other | 2 | moderate CPU; restart |
| `colliderLookaheadTime` | other | 0 | moderate CPU |
| `lightShadows` | quality | True | moderate when lights are on |
| `lightShadowsQuality` | quality | Medium | moderate |
| `scaledSpaceShadows` | quality | True | low |
| `smoothScaledSpaceShadows` | quality | True | none |
| `scaledRaymarchedShadowStepCount` | quality | 48 | moderate |
| `loadTexturesImmediately` | other | False | none |
| `wireframeTerrain` | other | False | debug |
| `suppressCriticalMessages` | other | False | debug |
| `cachedColliderCount` | other | 1000 | |

## Firefly

| Key | Kind | Default |
|---|---|---|
| `particles` (`disable_particles`) | quality | True (False) |
| `bowshock` (`disable_bowshock`) | taste | True (False) |
| `strength_base` | taste | 2800 |
| `length_mult` | taste | 1 |
| `hdr_override` | other | True |

## Deferred

| Key | Kind | Default |
|---|---|---|
| `useScreenSpaceReflections` | quality | True |
| `useHalfResolutionScreenSpaceReflections` | quality | True |
| `ambientBrightness` | taste | 0.9 |
| `ambientTint` | taste | 0.7 |
| `useDitheredTransparency` | taste | False |
| `useSmaaInEditors` | other | True |
| `guiModifierKey1` | other | LeftControl |
| `guiModifierKey2` | other | LeftAlt |
| `guiKey` | other | D |
| `capReflectionProbeRefreshRate` | other | True |
| `capReflectionProbeResolution` | other | True |

The two caps are not bundled, and the reset leaves them to Deferred's config:
with a cap on, Deferred lowers KSP's own reflection settings at every scene
load, and KSP saves them. Deferred keeps no file of its own, so a cap set for
one run would leave that lowering for good.

## TUFX

| Key | Kind | Default | With Volumetric Clouds |
|---|---|---|---|
| `profileMainMenu` | taste | Default-MainMenu | **Blackrack_TUFX** |
| `profileSpaceCenter` | taste | Default-KSC | **Blackrack_TUFX** |
| `profileEditor` | taste | Default-Editor | **Blackrack_TUFX** |
| `profileFlight` | taste | Default-Flight | **Blackrack_TUFX** |
| `profileMap` | taste | Default-Tracking | **Blackrack_TUFX** |
| `profileInternal` | taste | Default-Internal | **Blackrack_TUFX** |
| `profileTrackingStation` | taste | Default-Tracking | **Blackrack_TUFX** |
| `ShowToolbarButton` | other | True | |

## Waterfall

| Key | Kind | Default |
|---|---|---|
| `EnableLights` | quality | True |
| `EnableDistortion` | quality | True |
| `AtmosphereDensityExponent` | taste | 0.512 |
| `MinimumEffectIntensity` | other | 0.005 |
| `MinimumLightIntensity` | other | 0.05 |
| `EnableLegacyBlendModes` | other | False |
| `ForceAllControllersAwake` | other | False |
| `RandomControllersAwake` | other | True |
| `ShowEffectEditor` | other | False |
| `DebugModules`, `DebugLoading`, `DebugSettings`, `DebugEffects`, `DebugModifiers`, `DebugParticles`, `DebugMode`, `DebugUIMode` | other | False |
| `TransparentQueueBase`, `DistortQueue`, `QueueDepth`, `SortedDepth` | other | 3000, 3002, 750, 1000 (code; not in the file) |

## Distant Object

| Key | Kind | Default | Note |
|---|---|---|---|
| `flaresEnabled` | taste | True | |
| `flareBrightness` | taste | 1 | |
| `flareSize` | taste | 1 | |
| `flareSaturation` | taste | 1 | |
| `debrisBrightness` | taste | 0.15 | |
| `ignoreDebrisFlare` | taste | False | |
| `showNames` | other | True | from its `PluginData/Settings.cfg`; the class has False |
| `textSize` | other | 14 | the shipped file's `fontSize` is a key it does not read |
| `fontName` | other | Arial | |
| `renderVessels` | quality | True | from its `PluginData/Settings.cfg`; the class has False |
| `renderMode` | quality | RenderTargetOnly | |
| `maxDistance` | quality | 750000 | |
| `ignoreDebris` | quality | False | |
| `changeSkybox` | taste | True | |
| `maxBrightness` | taste | 0.25 | |
| `minimumSignificantBodySize` | taste | 1 | |
| `referenceBodySize` | taste | 60 | |
| `minimumTargetRelativeAngle` | taste | 100 | |
| `debugMode` | other | False | |
| `useToolbar` | other | True | from its `PluginData/Settings.cfg`; a button on Blizzy's toolbar, which stays there while bundled |
| `useAppLauncher` | other | True | |
| `onlyInSpaceCenter` | other | False | |

Its file also holds `situations`, which its `Load` never reads back
(`DistantFlareClass.Load`, decompiled): not a setting anyone can change, and
not bundled.
