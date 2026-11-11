# Atmospheres

The atmospheres (the season clock, the snow and the storm cells) as a mod of its own, `ReDefinition.Atmospheres.dll` in `GameData/ReDefinition/Atmospheres`. It needs neither ReDefinition nor Harmony nor ZTheme. Parallax and EVE are optional ([docs/player/seasons.md](../../docs/player/seasons.md)). The numbers and the reasons for them are in the `//` header of each file.

## Start here

| File | Read it for |
|---|---|
| `src/Atmospheres/SeasonClock.cs` | The season phase from the universal time, the year length and the hemispheres. |
| `src/Atmospheres/SeasonState.cs` | The phase the ground uses: the forced one or the clock's; none where there is no clock. |
| `src/Atmospheres/SnowMask.cs` | The snow cover from latitude, height, phase and noise, and the design constants. |
| `src/Atmospheres/SnowPaint.cs`, `SnowNoise.cs` | The snow colour and the blend, and the deterministic noise. |
| `src/Atmospheres/AtmospheresSettings.cs` | The settings in `PluginData/settings.cfg`: keys, ranges, defaults, snapshot and reset. |
| `src/Atmospheres/AtmospheresStartup.cs` | The add-on that checks Common, reads the settings, starts the parts and logs one line. |
| `src/Atmospheres/SnowPqsMod.cs` | The PQS mod on Kerbin that paints snow into the vertex colours when a quad is built. |
| `src/Atmospheres/SnowRefresher.cs` | Adds the PQS mod and hands the ground refresh to the two classes below. |
| `src/Atmospheres/LiveRepainter.cs` | Live refresh: paints the built quads again, a few per frame; the pass and log decisions. |
| `src/Atmospheres/GroundRebuilder.cs` | Rebuild refresh: builds the terrain anew where no vessel needs it; the due and safe decisions. |
| `src/Atmospheres/QuadSnow.cs`, `SnowQuads.cs` | What is kept per quad (original colours, latitude, height, noise) so that a quad can be painted again. |
| `src/Atmospheres/ParallaxLink.cs` | What the atmospheres ask of Parallax, by reflection, guarded. |
| `src/Atmospheres/EveSnowLink.cs` | EVE's snow particle field and snow sound raised with a storm or a replaced rain. |
| `src/Atmospheres/EveRainLink.cs` | EVE's rain (particles, droplets, wet ground, lightning, sound) taken away over snow. |
| `src/Atmospheres/EveLayerStorm.cs`, `EveStormMath.cs` | The storm cell inside the cloud layers EVE already has (setting `stormClouds = Existing`): the reflection, the swap of the coverage and cloud-type maps of the weather layers for working copies, the cell composited into them only where it lies (a rectangle read from the original, written to the copy's data and copied on the GPU), the swirl painted and erased in the picture of EVE's 2D layer (shader `ReDefinition/AtmospheresSwirlPaint`), and the pure maths (lobed cell, screen blend, cloud-type channel, rectangle groups, spiral, fan vertices). |
| `src/Atmospheres/EveCellLayer.cs`, `EveCoverageMath.cs`, `EveSwirlMath.cs` | The storm cells as layers of the mod's own config (`stormClouds = Own`, and where EVE's own layers cannot carry the cell): the reflection and the painters, the coverage map's maths and the 2D swirl's. Two layers: the cloud (volume and 2D layer for orbit and map) and the snow (EVE's particle field and sound, which both modes use). Their config is `GameData/ReDefinition/Atmospheres/Clouds/AtmospheresStormLayer.cfg`. |
| `src/Atmospheres/ReflectLink.cs` | The member lookups of the links, with the names that were missing. |
| `src/Atmospheres/StormModel.cs` | The storm draw of a slot, the envelope and the band schedule from time, latitude, season and seed. |
| `src/Atmospheres/StormCells.cs` | The storm cells: place, radius, life and drift as pure functions of seed and time; intensity at a point. |
| `src/Atmospheres/StormLook.cs` | What an intensity looks like: visibility, fog, veil, flakes, ambient light. |
| `src/Atmospheres/StormEffect.cs` | The storm in flight: lifecycle, the target intensity, the order of the steps, the failure guard. |
| `src/Atmospheres/FogController.cs` | The fog bookkeeping of a storm: saved, written and the decision to put it back. |
| `src/Atmospheres/StormLog.cs` | The log lines of a storm, built from plain values. |
| `src/Atmospheres/StormVeil.cs`, `StormFlakes.cs`, `StormAmbient.cs`, `AmbientTracker.cs` | The whiteout veil, the falling snow, the lowered ambient light and its tracker. |
| `src/Atmospheres/StormCull.cs` | The terrain layer left out beyond what the storm's fog shows (`stormCullDistance`). |
| `src/Atmospheres/StormVolume.cs`, `StormVolumeMath.cs`, `StormNoise.cs`, `StormShaders.cs` | The snow veil of the cells in the world, raymarched: the pool, the numbers it is fed, the noise, the shaders. |
| `src/Atmospheres/OrbitMask.cs` | The snow mask as pixels of an equirectangular map, in the PQS texture convention; pure, runs on a worker thread. |
| `src/Atmospheres/OrbitSnow.cs` | Kerbin's scaled-space planet: the polling, the worker, the generation counter, the restore. |
| `src/Atmospheres/OrbitTextures.cs`, `OrbitHeights.cs` | The textures and the overlay of the orbit snow, and the height grid read from the PQS. |
| `src/Atmospheres/Mathx.cs`, `Geo.cs` | The numeric helpers and the angle and direction functions the files above share. |
| `src/Atmospheres/AtmospheresLog.cs` | The log helper: the prefix, the per-kind warnings. |
| `src/Atmospheres/RetryGate.cs`, `FailureStreak.cs` | The retry schedule of a lookup and the failures-in-a-row counter. |

## What it does

- The clock gives a phase from 0 to 1; phase 0 is the northern midwinter and the south runs half a year apart. `SnowMask.Coverage` is the one function from latitude, height, phase and noise to a cover; the ground, the orbit view and the storms read it, so they agree.
- `SnowPqsMod` is a child of Kerbin's PQS, ordered after the colour mods. It turns the vertex colour toward snow when a quad is built and keeps what it needs to paint the quad again.
- `SnowRefresher` adds the mod, then follows `groundRefreshMode`: `Live` paints built quads again (`LiveRepainter`), `Rebuild` builds the terrain anew where no vessel needs it (`GroundRebuilder`), `Off` leaves built ground alone. Without a phase (no Kerbin yet, nothing forced) nothing is painted.
- `ParallaxLink` tells Parallax's terrain and scatters that a quad's colours changed, the way Parallax does itself.
- Storms are cells on the planet (`StormCells`, from the `StormModel` rule). `StormEffect` reads the intensity at the flight camera and sets the fog, the veil, the flakes, the ambient light and EVE's snow field, and `StormVolume` draws the cells from afar.
- `EveRainLink` replaces EVE's rain by its snow where the ground is snow or ice, whether or not a storm runs.
- The atmospheres are made to run with EVE's volumetric clouds. By default (`stormClouds = Existing`) the cell is part of EVE's weather: `EveLayerStorm` composites it into the coverage and type maps of Kerbin's base layer (its cell type is Cumulus, denser than the Stratus deck) and weather layers (Polar-snow) and draws it into the picture of the base layer's 2D layer, so it is lit and textured as the clouds around it. The falling snow is EVE's particle field on the mod's own snow layer (`EveCellLayer`, which `Own` also uses for the cloud). `StormVolume` draws the snow veil under the cloud only. Without EVE's volumetric clouds there are no cells (`StormEffect` logs it once); the ground and orbit snow go on. The mod's own flakes (`StormFlakes`, setting `stormOwnFlakes`) fall where EVE has no snow field.
- `OrbitSnow` draws the same snow over the colour map of the scaled planet for the orbit and map view.
- Every step that can fail logs once and puts back what it changed: the storm effect, the orbit snow, the live refresh and the links switch themselves off for the session.
- The storms add no image effect and no command buffer, so the upscalers and raytracing of ReDefinition are not touched.

## What it uses and what uses it

It uses `Core` in `ReDefinition.Common.dll` and nothing else of ReDefinition. ReDefinition does not name it. `ArchitectureTests` and `tools/checks/check_no_reference.ps1` hold this. Parallax and EVE are reached by reflection; the shader bundle `Atmospheres/Shaders/atmospheres.shaders` is built from `unity/Assets/ReDefinitionAtmospheres` by `tools/build_atmospheres_bundle.ps1`.

## Rules of the area

- The seasons apply to Kerbin only (`SeasonClock.Applies`).
- The design numbers of the snow line are constants in `SnowMask` and nowhere else; the snow colour is in `SnowPaint`.
- No type of `ReDefinition.dll` or of Harmony is named here.
- Parallax is reached through `ParallaxLink` only; EVE only through `EveSnowLink`, `EveRainLink`, `EveCellLayer` and `EveLayerStorm`. Their lookups use `ReflectLink`.
- A setting has one key constant in `AtmospheresSettings.Keys` and one default in `ResetToDefaults`; the player guide lists the same keys, defaults and ranges (a test compares them).
- A log line goes through `AtmospheresLog`.
