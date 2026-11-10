# Seasons

Seasons on Kerbin as a mod of its own, `ReDefinition.Seasons.dll` in `GameData/ReDefinition/Seasons`. It needs neither ReDefinition nor Harmony nor ZTheme. Parallax and EVE are optional ([docs/player/seasons.md](../../docs/player/seasons.md)). The numbers and the reasons for them are in the `//` header of each file.

## Start here

| File | Read it for |
|---|---|
| `src/Seasons/SeasonClock.cs` | The season phase from the universal time, the year length and the hemispheres. |
| `src/Seasons/SeasonState.cs` | The phase the ground uses: the forced one or the clock's; none where there is no clock. |
| `src/Seasons/SnowMask.cs` | The snow cover from latitude, height, phase and noise, and the design constants. |
| `src/Seasons/SnowPaint.cs`, `SnowNoise.cs` | The snow colour and the blend, and the deterministic noise. |
| `src/Seasons/SeasonsSettings.cs` | The settings in `PluginData/settings.cfg`: keys, ranges, defaults, snapshot and reset. |
| `src/Seasons/SeasonsStartup.cs` | The add-on that checks Common, reads the settings, starts the parts and logs one line. |
| `src/Seasons/SnowPqsMod.cs` | The PQS mod on Kerbin that paints snow into the vertex colours when a quad is built. |
| `src/Seasons/SnowRefresher.cs` | Adds the PQS mod and hands the ground refresh to the two classes below. |
| `src/Seasons/LiveRepainter.cs` | Live refresh: paints the built quads again, a few per frame; the pass and log decisions. |
| `src/Seasons/GroundRebuilder.cs` | Rebuild refresh: builds the terrain anew where no vessel needs it; the due and safe decisions. |
| `src/Seasons/QuadSnow.cs`, `SnowQuads.cs` | What is kept per quad (original colours, latitude, height, noise) so that a quad can be painted again. |
| `src/Seasons/ParallaxLink.cs` | What the seasons ask of Parallax, by reflection, guarded. |
| `src/Seasons/EveSnowLink.cs` | EVE's snow particle field and snow sound raised with a storm or a replaced rain. |
| `src/Seasons/EveRainLink.cs` | EVE's rain (particles, droplets, wet ground, lightning, sound) taken away over snow. |
| `src/Seasons/EveCellLayer.cs`, `EveCoverageMath.cs`, `EveSwirlMath.cs` | The storm cells as layers of EVE's volumetric clouds: the reflection and the painters, the coverage map's maths and the 2D swirl's. Two layers: the cloud (volume and 2D layer for orbit and map) and the snow (EVE's particle field and sound). Their config is `GameData/ReDefinition/Seasons/Clouds/SeasonsStormLayer.cfg`. |
| `src/Seasons/ReflectLink.cs` | The member lookups of the links, with the names that were missing. |
| `src/Seasons/StormModel.cs` | The storm draw of a slot, the envelope and the band schedule from time, latitude, season and seed. |
| `src/Seasons/StormCells.cs` | The storm cells: place, radius, life and drift as pure functions of seed and time; intensity at a point. |
| `src/Seasons/StormLook.cs` | What an intensity looks like: visibility, fog, veil, flakes, ambient light. |
| `src/Seasons/StormEffect.cs` | The storm in flight: lifecycle, the target intensity, the order of the steps, the failure guard. |
| `src/Seasons/FogController.cs` | The fog bookkeeping of a storm: saved, written and the decision to put it back. |
| `src/Seasons/StormLog.cs` | The log lines of a storm, built from plain values. |
| `src/Seasons/StormVeil.cs`, `StormFlakes.cs`, `StormAmbient.cs`, `AmbientTracker.cs` | The whiteout veil, the falling snow, the lowered ambient light and its tracker. |
| `src/Seasons/StormVolume.cs`, `StormVolumeMath.cs`, `StormNoise.cs`, `StormShaders.cs` | The cells in the world, raymarched or as meshes: the pool, the numbers it is fed, the textures, the shaders. |
| `src/Seasons/OrbitMask.cs` | The snow mask as pixels of an equirectangular map, in the PQS texture convention; pure, runs on a worker thread. |
| `src/Seasons/OrbitSnow.cs` | Kerbin's scaled-space planet: the polling, the worker, the generation counter, the restore. |
| `src/Seasons/OrbitTextures.cs`, `OrbitHeights.cs` | The textures and the overlay of the orbit snow, and the height grid read from the PQS. |
| `src/Seasons/Mathx.cs`, `Geo.cs` | The numeric helpers and the angle and direction functions the files above share. |
| `src/Seasons/SeasonsLog.cs` | The log helper: the prefix, the per-kind warnings. |
| `src/Seasons/RetryGate.cs`, `FailureStreak.cs` | The retry schedule of a lookup and the failures-in-a-row counter. |

## What it does

- The clock gives a phase from 0 to 1; phase 0 is the northern midwinter and the south runs half a year apart. `SnowMask.Coverage` is the one function from latitude, height, phase and noise to a cover; the ground, the orbit view and the storms read it, so they agree.
- `SnowPqsMod` is a child of Kerbin's PQS, ordered after the colour mods. It turns the vertex colour toward snow when a quad is built and keeps what it needs to paint the quad again.
- `SnowRefresher` adds the mod, then follows `groundRefreshMode`: `Live` paints built quads again (`LiveRepainter`), `Rebuild` builds the terrain anew where no vessel needs it (`GroundRebuilder`), `Off` leaves built ground alone. Without a phase (no Kerbin yet, nothing forced) nothing is painted.
- `ParallaxLink` tells Parallax's terrain and scatters that a quad's colours changed, the way Parallax does itself.
- Storms are cells on the planet (`StormCells`, from the `StormModel` rule). `StormEffect` reads the intensity at the flight camera and sets the fog, the veil, the flakes, the ambient light and EVE's snow field, and `StormVolume` draws the cells from afar.
- `EveRainLink` replaces EVE's rain by its snow where the ground is snow or ice, whether or not a storm runs.
- The Seasons are made to run with EVE's volumetric clouds. The cell's cloud, its swirl in the orbit and map view and its falling snow are EVE's layers (`EveCellLayer`). The mod's own cloud (`StormVolume` with `_CloudWeight` 1, the plain meshes) and flakes (`StormFlakes`, setting `stormOwnFlakes`) are fallbacks that get no further work.
- `OrbitSnow` draws the same snow over the colour map of the scaled planet for the orbit and map view.
- Every step that can fail logs once and puts back what it changed: the storm effect, the orbit snow, the live refresh and the links switch themselves off for the session.
- The storms add no image effect and no command buffer, so the upscalers and raytracing of ReDefinition are not touched.

## What it uses and what uses it

It uses `Core` in `ReDefinition.Common.dll` and nothing else of ReDefinition. ReDefinition does not name it. `ArchitectureTests` and `tools/checks/check_no_reference.ps1` hold this. Parallax and EVE are reached by reflection; the shader bundle `Seasons/Shaders/seasons.shaders` is built from `unity/Assets/ReDefinitionSeasons` by `tools/build_seasons_bundle.ps1`.

## Rules of the area

- The seasons apply to Kerbin only (`SeasonClock.Applies`).
- The design numbers of the snow line are constants in `SnowMask` and nowhere else; the snow colour is in `SnowPaint`.
- No type of `ReDefinition.dll` or of Harmony is named here.
- Parallax is reached through `ParallaxLink` only; EVE only through `EveSnowLink`, `EveRainLink` and `EveCellLayer`. Their lookups use `ReflectLink`.
- A setting has one key constant in `SeasonsSettings.Keys` and one default in `ResetToDefaults`; the player guide lists the same keys, defaults and ranges (a test compares them).
- A log line goes through `SeasonsLog`.
