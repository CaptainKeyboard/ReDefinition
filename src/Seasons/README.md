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

## What it uses and what uses it

It uses `Core` in `ReDefinition.Common.dll` and nothing else of ReDefinition. ReDefinition does not name it. `ArchitectureTests` and `tools/checks/check_no_seasons_reference.ps1` hold this.

## Rules of the area

- The seasons apply to Kerbin only (`SeasonClock.Applies`).
- The design numbers of the snow line are constants in `SnowMask` and nowhere else; the snow colour is in `SnowPaint`.
- No type of `ReDefinition.dll` or of Harmony is named here.
- Parallax is reached by reflection only, and only through `ParallaxLink`.

## Open

- The orbit and map view (the scaled planet's colour map) is not tinted.
- The ground refresh is not tested in the game yet. The test plan is `.local/work/seasons-test/PLAN.md` (outside the repository).
