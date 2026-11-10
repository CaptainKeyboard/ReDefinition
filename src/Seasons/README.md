# Seasons

Seasons on Kerbin as a mod of its own, `ReDefinition.Seasons.dll` in `GameData/ReDefinition/Seasons`. It needs neither ReDefinition nor Harmony nor ZTheme. Parallax and EVE are optional ([docs/player/seasons.md](../../docs/player/seasons.md)).

## Start here

| File | Read it for |
|---|---|
| `src/Seasons/SeasonClock.cs` | The season phase from the universal time, the year length and the hemispheres. |
| `src/Seasons/SnowMask.cs` | The snow cover from latitude, height, phase and noise, and the design constants. |
| `src/Seasons/SeasonsSettings.cs` | The settings in `PluginData/settings.cfg` of the mod's own folder. |
| `src/Seasons/SeasonsStartup.cs` | The add-on that checks Common, reads the settings, starts the refresher and logs one line. |
| `src/Seasons/SnowPqsMod.cs` | The PQS mod on Kerbin that paints snow into the vertex colours when a quad is built. |
| `src/Seasons/SnowRefresher.cs` | Adds the PQS mod, repaints built quads (Live) or rebuilds the ground (Rebuild). |
| `src/Seasons/QuadSnow.cs`, `SnowQuads.cs` | What is kept per quad (original colours, latitude, height, noise) so that a quad can be painted again. |
| `src/Seasons/SnowPaint.cs`, `SnowNoise.cs` | The snow colour and the blend, and the deterministic noise. |
| `src/Seasons/ParallaxLink.cs` | What the seasons ask of Parallax, by reflection, guarded. |
| `src/Seasons/StormModel.cs` | The storm schedule: intensity 0 to 1 from time, latitude, season and seed; the diagnostics override. |
| `src/Seasons/StormLook.cs` | What an intensity looks like in numbers: visibility, fog density, veil alpha, sky grey, ambient factor, flake layers, rate, size and streaks, height fade. |
| `src/Seasons/StormEffect.cs` | The flight add-on that applies a storm: fog, veil, flakes, ambient light, EVE snow; restores what it changed. |
| `src/Seasons/StormVeil.cs`, `StormFlakes.cs`, `StormShaders.cs` | The veil grid with the sky gradient, the two particle layers, and the shaders and textures they use. |
| `src/Seasons/StormAmbient.cs`, `AmbientTracker.cs` | The ambient light scaled in `Camera.onPreCull`, and the tracker that tells our write from someone else's. |
| `src/Seasons/EveSnowLink.cs` | EVE's snow particle field raised with the storm, by reflection, guarded. |
| `src/Seasons/OrbitMask.cs` | The snow mask as pixels of an equirectangular map, in the PQS texture convention; pure, runs on a worker thread. |
| `src/Seasons/OrbitSnow.cs` | Kerbin's scaled-space planet: base copy of its colour map, mask drawn over it, set as the material's map. |

## What it does

- `SeasonClock.Phase` turns a time and a year length into a phase in [0, 1). `YearSeconds` scales Kerbin's orbit period by the year length factor. `HemispherePhase` gives the southern hemisphere half a year later. Phase 0 is the northern midwinter.
- `SnowMask.Coverage` is a pure function from latitude, height above sea level, phase and a noise value to a cover from 0 to 1. The ground, the orbit view and the storms are to read this one function, so that they agree.
- `SeasonState.Phase` is the phase the ground uses: the `forcePhase` setting if it is from 0 to 1 (a diagnostics setting for screenshots), otherwise the clock.
- `SnowPqsMod` is a child of Kerbin's PQS with the sort order `int.MaxValue - 10`: after the colour mods; the installed Parallax orders its own mod at 999999 and reads the colour from the finished mesh. In `OnVertexBuild` it moves the vertex colour toward the snow colour by cover times `snowStrength`; the alpha stays. It keeps the colour the vertex had before, with its latitude, height and noise, in `SnowQuads` (about 3.6 KB per quad). It logs the sphere's mods in their order once.
- `SnowRefresher` adds the mod when Kerbin's PQS exists and then follows the ground refresh mode (`groundRefreshMode`):
  - `Live`: every `refreshSeconds` (5), or sooner when the phase has moved by 0.002 of a year, it paints the built quads again from the stored originals (2 ms of a frame at most) and writes `mesh.colors32`. Quads painted for nearly the same phase are skipped.
  - `Rebuild`: when the phase has moved by 0.02 of a year and 120 s have passed, it calls `PQS.RebuildSphere`, only where no vessel stands on the ground (the sphere is not started, or in flight or the tracking station with the sphere inactive; never in the space centre).
  - `Off`: only quads built later get the phase.
- With Parallax (`ParallaxLink`), a repainted quad is announced the way Parallax announces a changed quad mesh itself: `PQSMod_Parallax.terrainQuadData[quad].OnQuadNormalUpdate()` (the subdivided terrain mesh reads the quad's colours again). The scatters of the quad (`ScatterComponent.scatterQuadData`) get the new colours in `colors` and `sourceColorsBuffer`, and when the mean snow of the quad moved by 0.1, each `ScatterData` is cleaned up and started again, as Parallax's `ReinitializeScatters` does. A symbol that is missing is logged once and that part is left out.

## Snow storms

- `StormModel.Intensity(universalTime, latitude, yearSeconds, forcedPhase, winterLine, seed, frequency)` is a pure function. Time is cut into slots of 24 Kerbin hours (86 400 s) per hemisphere and per 10-degree band of latitude. A slot holds at most one storm, with a chance of `BaseChance` (0.3) times the frequency setting times the gate. The gate (`Gate`) is the snow cover of `SnowMask` at the middle of the band times how wintry the hemisphere is (`Winterness`), so there are no storms near the equator or in the summer, also not on the polar caps. A hash of seed, hemisphere, band and slot gives the start inside the slot, the ramp up (1.5 to 3 h), the peak (2 to 8 h), the decay (2.5 to 6 h) and the peak height (0.45 to 1). At the real year length that is about six storms a year at 60 degrees.
- `StormModel.Resolve` applies the `forceStorm` setting: from 0 to 1 it is the intensity everywhere, whatever the place, season and `storms` switch; negative means the schedule.
- `StormEffect` runs in flight. The target is the intensity at the active vessel, on Kerbin, in the atmosphere, times `StormLook.HeightFactor` (full below 4 km, none from 10 km). The applied intensity follows the target in about two seconds. It sets:
  - `RenderSettings` fog: `ExponentialSquared`, density `sqrt(3) / visibility`, so that 5 % of the contrast is left at the visibility. The visibility goes from 20 km down to `stormVisibility` (60 m) at intensity 1, about 245 m at 0.5. The fog colour is grey-white scaled by the sun's elevation. The fog settings from before are saved at the start and put back at the end and in `OnDestroy`.
  - `StormVeil`: a 9 x 9 grid of the alpha-blended shader just in front of the flight camera (`FlightCamera.fetch.mainCamera`). Its vertices carry colour and alpha, set per frame from the direction each looks along against the vessel's up. Toward the horizon and the ground: the fog colour and alpha `stormVeil * intensity^1.3`. Toward the zenith: a dark storm grey `(0.34, 0.36, 0.40)` scaled by the sun, with alpha `1.1 * stormVeil * intensity^1.1` (at most 0.97). The grey share rises from the horizon to 24 degrees elevation and reaches its full value at intensity 0.125, before the veil is opaque, so the sky is never lighter on the way (`StormLook.SkyGreyShare`). The setting `stormSkyDarkening` scales it (full grey from 0.67; 0 leaves the sky alone).
  - `StormFlakes`: two particle systems made once, a near layer (box 9 m wide, 45 % of the flakes, 0.05 to 0.11 m) and a far layer (32 m, 55 %, 0.07 to 0.16 m), in the camera's local space and turned to the vessel's up. They are stretched billboards: the length of a flake is its speed times a scale that grows from 0.015 to 0.09, so a full storm has streaks of about a metre and a light fall has dots. The rate is `3000 * intensity^0.8` flakes a second in total, sizes go from 70 % to 100 % with the intensity, the speed is 3.5 to 6 m/s down and up to 9 m/s of wind. The boxes lean upwind by half the drift so that the flakes are spread around the camera. The screen size of a flake is kept between 0.18 % and 2 % of the viewport height. Render queue 3100, after the veil at 3000, so that the whiteout does not wash the flakes out. At most 5000 flakes per layer, no allocation per frame.
  - Ambient light (`StormAmbient`, `AmbientTracker`): scaled by `1 - 0.6 * stormSkyDarkening * intensity^0.8` in `Camera.onPreCull`, once per frame. Read from the sources: Scatterer's `DisableAmbientLight` (on the scaled-space camera) sets `RenderSettings.ambientLight` to black in `OnPreRender` and puts back the value it read in `OnPostRender`; Deferred's `RefreshLegacyAmbient` copies `ambientLight` into the shader global `legacyAmbientColor` in `OnPreRender`; Deferred sets the ambient mode only in the editor. Writing in `onPreCull` is after every `Update` and `LateUpdate` (so KSP's own write of the frame comes first) and before the cameras render (so both of them see the scaled value). A value that differs from the one written is taken as the owner's new baseline; at the end the baseline is put back only while the value is still ours. Mode `Flat` scales `ambientLight`, `Trilight` the three colours, `Skybox` the `ambientIntensity`. Who writes the ambient light in KSP itself was not found in the reference sources, so the log counts how often the baseline changed under us.
  - EVE's snow particle field (`EveSnowLink`): the installed configuration has `snow-Kerbin` (400 000 particles in a 100 m field) in the layers `Kerbin-Snow-Particles-1/2`. EVE's `ParticleField` draws it while its private `currentCoverage` (set in `ParticleField.Update` from the layer's coverage map, scaled by the cloud type's `particleFieldDensity`) is above 0, and has no setting or API for it. The storm lists `Atmosphere.CloudsManager.GetObjectList()`, takes the layers of the body whose `LayerRaymarchedVolume.particleField` has a config name containing `snow`, and in `onPreCull` writes `max(EVE's value, intensity)` and calls `SetRendererEnabled(true)`; at the end it writes EVE's value back where it is still ours. Every symbol was checked against the clone of EVE and as a name in the installed `Atmosphere.dll`; the startup line and the `storm begins` line say `EVE snow field: found / not found / not controllable (missing ...)`. The setting `stormEveSnow` switches it off. The own flakes stay: they are what shows without EVE.
- No image effect and no command buffer is added. The main mod uses `OnRenderImage` and command buffers on the flight camera for the upscalers, raytracing and the late trace; the seasons stay out of that chain (`grep` of `src/` for `OnRenderImage` and `AddCommandBuffer`: `Raytracing/RtCamera.cs`, `Raytracing/RtCamera.LateTrace.cs`, `Proxy/NativeUpscalerLink.cs`, `Raytracing/Ksp/EveCloudRelight.cs`, `Raytracing/Ksp/MoonGlow.cs`, `Api/Hooks.cs`).
- Whether the shaders of the game, of Parallax and of Scatterer apply `RenderSettings` fog was read from the sources in the reference clones: Scatterer sets none and its shaders switch fog off or use it only in old EVE shaders; Parallax applies fog (`UNITY_APPLY_FOG`, `multi_compile_fog`) in the forward pass of `Terrain/Parallax.shader` and `Terrain/KSCTerrain.shader` and in its scaled-space shaders, but not in the scatter (grass, rock) shaders; the Unity post-processing stack of TUFX has a deferred fog pass that its profile switches on. Whether the stock terrain and part shaders, the deferred path and Scatterer's sky show the fog in the game is not established; the veil is what is certain to reduce the contrast. `KSP.log` tells which path ran (`storm N begins: path ...`) and the harness measures the result.

## Orbit and map view

- Kerbin seen from orbit and in the map is the scaled-space body (`CelestialBody.scaledBody`), drawn with one equirectangular colour map. With Parallax the material is `Custom/ParallaxScaled` and the map is `_ColorMap` (4096 x 2048); the stock scaled planet shaders and Kopernicus read `_MainTex`. `OrbitSnow` takes the property from the material at run time (`_ColorMap`, then `_MainTex`, the first with a texture) and logs the shader, the property and the size.
- It copies the map into a render texture (base copy) with `Graphics.Blit`, which works for compressed and unreadable textures. Per composition it copies the base into a second render texture (target, with mips) and draws one quad with the snow mask over it (`GL` immediate mode, `UI/Default` with `_ColorMask` 14 so that the alpha channel stays; if the shader is missing, the particle shaders of `StormShaders` without that guarantee). The target is set as the material's map. The planet's own texture is never written to; the original is put back at the end.
- The mask (`OrbitMask.Fill`, 1024 x 512) calls `SnowMask.Coverage` for every texel with the texel's latitude, the noise of the texel's direction (`SnowNoise`) and a height. So it is the ground's function, evaluated on a grid. The convention is read from `PQS.BuildVertexMapCoords` (u = longitude / 2 pi from +z toward +x, v = latitude / pi + 0.5), and a test compares it with that formula. Heights come from the PQS (`GetSurfaceHeight` on a 256 x 128 grid, 2 ms of a frame at most, once, when the sphere is started); until they are read the mask has no height bonus. The mask and the noise table (0.5 MB) are made on a worker thread.
- A composition runs at most every 30 s of real time and when the phase moved by 0.01 of a year, or after 2 s when it moved by 0.05 or the settings or heights changed (`OrbitMask.Due`). The material's map is polled once a second: if Parallax loaded a new texture, it becomes the base copy and the target is composed again.
- Memory: both render textures with mips, 4 bytes a texel: 29 MB at 2048 wide, 117 MB at 4096 (`orbitSnowWidth`, default 4096, never larger than the original). The mask texture is 2 MB.
- Settings: `orbitSnow` (default on), `orbitSnowWidth`.
- The ground can differ from the map by the height grid (coarse) and by the 8 bit noise; the transition has no seam in the formula, but the exact line can move by a texel.

## What it uses and what uses it

It uses `Core` in `ReDefinition.Common.dll` and nothing else of ReDefinition. ReDefinition does not name it. `ArchitectureTests` and `tools/checks/check_no_seasons_reference.ps1` hold this.

## Rules of the area

- The seasons apply to Kerbin only (`SeasonClock.Applies`).
- The design numbers of the snow line are constants in `SnowMask` and nowhere else; the snow colour is in `SnowPaint`.
- No type of `ReDefinition.dll` or of Harmony is named here.
- Parallax is reached by reflection only, and only through `ParallaxLink`; EVE only through `EveSnowLink`.

## Open

- None of the storm look (sky, ambient light, streaked flakes, EVE snow) and none of the orbit snow has run in the game. The tests are `.local/work/seasons-test/check_seasons.py --mode storm` and `--mode orbit` (outside the repository; PLAN.md there).
- The thresholds of the flake metric in the harness are estimates from a synthetic picture; the first run calibrates them.
- The orbit map's orientation (south at the bottom of the texture, the draw into the render texture not flipped) is read from the sources, not measured; the orbit check compares winter with summer and fails if the hemispheres are swapped.
- The flakes carry no motion vectors, so an upscaler may smear them. The sun's light is not dimmed in a storm, only the ambient light and the sky.
- The ground refresh is not tested in the game yet. The test plan is `.local/work/seasons-test/PLAN.md` (outside the repository).
