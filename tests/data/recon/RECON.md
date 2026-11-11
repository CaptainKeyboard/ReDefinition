# K-pre reconnaissance: state of the premises (package B09)

Status: the probe is built and reviewed, the first start (run2) did not reach the game's flight because the Atmospheres assembly does not
load in the game (below). No premise has a measurement yet. Every row of the table is UNKNOWN until K-pre has run; the facts under
"Read in the installed assemblies and configs" were read without a game start and are not measurements.

## Blocker found by the first start

`GameData/ReDefinition/Atmospheres/Plugins/ReDefinition.Atmospheres.dll` fails to load: KSP's `AssemblyLoader` loads the assemblies in the
order of their paths (`Atmospheres`, `Common`, `Dashboard`, `Main`, `Planner`) and reads the types of `ReDefinition.Atmospheres` before
`ReDefinition.Common` is known (`KSP.log`: "ADDON BINDER: Cannot resolve assembly: ReDefinition.Common", then
"Exception loading 'ReDefinition.Atmospheres': ReflectionTypeLoadException ... Could not load file or assembly
'ReDefinition.Common, Version=0.1.17.0'"). Before the rename (A00) the assembly was called Seasons, which sorts after `Common` [VERIFY: the old GameData folder name]. No module of the
Atmospheres assembly runs in the game, so no probe can. Cause [VERIFY]: the load order; the assembly carries no `KSPAssemblyDependency`
on Common and Common carries no `KSPAssembly`. A fix is a platform change (A00/A02 files): `[assembly: KSPAssembly("ReDefinition.Common", 0, 1)]`
in the Common project and `[assembly: KSPAssemblyDependency("ReDefinition.Common", 0, 1)]` in the Atmospheres project (the pattern of
`src/KspAssemblyInfo.cs` for Harmony), or a folder name for Atmospheres that sorts after `Common`. It was not applied here: the files are not
B09's. Start log: `.local/work/atmo-series-starts.jsonl` has one counted start (K-pre run2, 2026-11-11 08:37).

## Premise table

| id | premise | threshold (fixed before the run, rows in `tests/series/rows/B09.json`) | result | consequence taken |
|---|---|---|---|---|
| P1 | KSP temperature curves reachable | 4 curves present; all samples finite; KSC 233..333 K over 4 phases x 4 hours; KSC mean within 30 K of the altitude curve; sea level 15 K warmer than 10 km | UNKNOWN (names read, see below) | none yet |
| P2 | height and ocean grid obtainable | Kerbin 360 x 180 complete; ocean fraction 0.5..0.8 | UNKNOWN | none yet |
| P3 | PQS vertex spacing, Parallax displacement | numbers recorded | UNKNOWN (Parallax config values read) | none yet |
| P4 | wind injection | override registered; drift along the wind within 20 % of a drag model; no-wind control within 10 % of the predicted drift | UNKNOWN | none yet |
| P5 | audio path | pan index difference >= 0.6; output silent when paused | UNKNOWN (members read) | none yet |
| P6 | camera chain and depth | depth readable at BeforeForwardAlpha through the global texture, or a copy at an early event is valid on the main camera | UNKNOWN | none yet |
| P7 | TUFX fog | measured share within 20 % of Unity's Exp2 share; fog off gives <= 0.05 | UNKNOWN | none yet |
| P8 | foreign shader accepted | luma ratio 0.9..1.1 against the stock material; control at a tenth of the albedo <= 0.5 | UNKNOWN | none yet |
| P9 | EVE behaviour | layer list complete; re-bind latency measured | UNKNOWN | none yet |
| P10 | opaque disc over the stock ocean (run2) | share of disc pixels in the region >= 0.9, disc off <= 0.1, luma ratio >= 1.5, at 50 m, 500 m, 5 km | UNKNOWN | none yet |
| P11 | the same over Scatterer's ocean (run1) | as P10 | UNKNOWN | none yet |
| P12 | RT sky against ambient | frame luminance rises with the ambient factor by 0.002 per step (raster ambient only; the RT sky is not driven by the probe) | UNKNOWN | none yet |

The three plausibility reviews (independent Sonnet agents) ended with "DO NOT SIGN" for the code as of the commits before the last; the last
review's items (water for 2 km along the wind, the P8 control, restoring the pod's materials, the main-camera fallback for P6) were applied
afterwards and not re-reviewed. Open from the reviews: P3 is measured at the pod off the coast, not at KSC or 55 N; the terrain case of P8 is
not tried; P12 does not test the RT sky; `P6.depth_ok` is defined by the brief (readable at queue 3000 or the copy fallback works).

## Read in the installed assemblies and configs (KSP 1.12, no game start)

- `CelestialBody` has all four curves of the plan by their names (`latitudeTemperatureBiasCurve`, `axialTemperatureSunMultCurve`,
  `atmosphereTemperatureSunMultCurve`, `eccentricityTemperatureBiasCurve`) and also `atmosphereTemperatureCurve`,
  `latitudeTemperatureSunMultCurve`, `axialTemperatureSunBiasCurve`; `GetFullTemperature(Vector3d)`, `GetFullTemperature(double altitude,
  double atmoSunBasedTempOffset)` and `GetTemperature(double)` exist.
- Names that differ from the plans: the part's velocity relative to the air is `Part.dragVector` (with `dragVectorMag`, `dragVectorDir`,
  `dragVectorDirLocal`, `dragScalar`, `dynamicPressurekPa`); there is no `dragVelocity`. KSPCommunityFixes replaces
  `FlightIntegrator.UpdateAerodynamics` wholesale (`FlightIntegratorPerf.cs` in the reference clone), so a wind has to enter before the base
  call or through the rigidbody velocity.
- ModularFlightIntegrator 1.2.10 (MIT, `LICENSE.txt` Copyright 2014 sarbian): static `Register*` methods per slot, among them
  `RegisterUpdateAerodynamicsOverride(voidPartDelegate)`, `RegisterCalculateDragValueOverride(doublePartDelegate)`,
  `RegisterCalculateAerodynamicAreaOverride`, `RegisterUpdateThermodynamicsOverride/Pre/Post`, `RegisterIntegrateOverride`; instance methods
  `BaseFIUpdateAerodynamics(Part)` and so on call the base. A slot holds one function; the call returns false when taken.
- The Mk1 command pod (`mk1pod.v2`) has no `ModuleParachute`; the Mk16 (`parachuteSingle`) has, with its node positions scaled by 0.1.
- `GameSettings` volume fields: `MASTER_VOLUME`, `SHIP_VOLUME`, `AMBIENCE_VOLUME`, `MUSIC_VOLUME`, `UI_VOLUME`, `VOICE_VOLUME`.
- `KerbalEVA` has one audio field (`weldingLasersFX`, an `AudioSource`), no `AudioClip` field and no `OnGround`; its FSM states are
  fields named `st_*` (`st_walk_acd`, `st_run_acd`, `st_bound_gr_acd`, ...); `tgtBoundStep`, `boundStepSpeed`, `lastBoundStep`,
  `boundLeftFoot` and `footPivot` exist. No step sound hook is visible: the stride fallback of the audio plan is the likely path (to be
  confirmed by P5's list at run time).
- `PQS` has `ForceStart()`, `StartUpSphere()`, `isStarted`, `maxLevel`, `quads` (the six root quads); `PQ` has `subNodes`, `isSubdivided`,
  `subdivision`, `verts`, `mesh`, `quadTransform`. The drawn quads are the leaves under the roots.
- Parallax (`Parallax_StockTerrainTextures/Terrain.cfg`): `_DisplacementScale` 0.3 for Kerbin, 0.2 for the Mun, 0.25 for Duna and Gilly, 0.2
  for Eve, 0.4 for Bop and Pol; global settings `maxTessellation = 64`, `tessellationEdgeLength = 4`, `maxTessellationRange = 30`.
- EVE: `CloudsManager.onApply` is a static `EventVoid` fired after the configs are applied; `CloudsObject.Apply()` makes a new `CloudsPQS`.
- `FlightCamera` has public `maxDistance`, `minDistance`, `camPitch`, `camHdg`, `SetDistanceImmediate`.

## Dumps

None yet. A run writes `GameData/ReDefinition/Atmospheres/PluginData/recon/<start>-*.json`; the runner collects them to
`tests/data/recon/dumps/`: `run1-bodies.json`, `run1-heights-<Body>.json` (360 x 180, heights in metres and the ocean mask),
`run1-temperature-<Body>.json` (4 phases x 4 longitudes x 4 altitudes x 13 latitudes, kelvin), `run1-pqs-spacing.json`,
`<start>-cameras-*.json`, `<start>-depth-taps.json`, `run1-eve-layers.json`, `<start>-env.json`. `ReconDumpTests` checks their schema and
stays inconclusive until they exist.
