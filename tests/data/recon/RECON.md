# K-pre reconnaissance: state of the premises (package B09)

Status (2026-11-11): the probe is built and has passed four plausibility reviews (the fourth ended with DO NOT SIGN on 8 defects, all settled;
the re-reviews ended with SIGN). The load-order blocker of the first start is fixed on main (commit 55fc088c, `KSPAssembly` for Common
and the dependency in the four mods). The measuring start (run1) has NOT run: `tools/atmo_series.py run K-pre --start run1` refused at the
memory gate ("only 9.2 GB of memory are free (12 needed)"; the free memory stayed between 9.5 and 10.7 GB for 20 minutes with other agents'
builds and simulations and the IDE running). No start was counted for it. Every row of the table below is therefore UNKNOWN (not run); the
facts under "Read in the installed assemblies and configs" were read without a game start and are not measurements. One earlier start
(run2, 2026-11-11 08:37) is counted and produced no data (the assembly did not load); run1 and run2 remain.

## Decision rules fixed before the run

- P4: PASS needs the override registered, a rigidbody found for the canopy (`P4.chute_rb_found`; `P4.chute_rb_own` says whether it is the
  canopy's own or its parent's), and the peak of the horizontal part of `dragVector` taken in the hook (`P4.dragvector_horizontal_ms`)
  at 15 m/s or more with a 20 m/s wind and at 1 m/s or less in the control. The drift rows (`P4.wind_error_rel` over the last 15 s,
  `P4.wind_error_full_rel` over 30 s) are a second row: the model assumes drag already aligned with the relative wind, so a miss of
  more than 20 % with the mechanism row passed is the canopy's swing, not the injection.
- P6: `P6.depth_ok` is `readable on the main camera` OR (`copy valid` AND `copy read intact at BeforeForwardAlpha`). The consumer is a
  blit from the copy's render texture at BeforeForwardAlpha; it shows that the event runs on the main camera and the copy survives to
  it, not that a material at queue 3000 can bind it. D32/D37/D38 read the separate rows `P6.depth_readable_main_camera` and
  `P6.depth_fallback_main_camera`.
- P8: the blocking case is an opaque lit shader of a loaded mod bundle (name in `P8.foreign_shader`, the candidates in
  `P8.foreign_candidates`) on balls, on the pod and on a flat lit mesh over the sea (the terrain stand-in); the chosen candidate is the
  responsive one furthest from 1. `P8.forward_luma_ratio` (a lit shader without a deferred pass) outside 0.9..1.1 is the trigger of D34
  variant (f). Unity's Legacy Diffuse is the informative `P8.builtin_luma_ratio`.
- P12 is UNKNOWN by design: the probe varies the raster `ambientIntensity` only and reads no ray-traced frame; the rows are not read as
  a verdict on the RT sky.
- P9: only the re-bind latency of the coverage map after a forced `Apply()` is measured; the light-volume latency is not.
- P2: grids exist for Kerbin (run1 first), Duna, Laythe and Eve (last, inside the step budget); B10 and C25 assume no other body.
- P3: the vertex spacing near the pod is measured over the sea; for land (KSC, 55 N) `P3.spacing_analytic_m` (top level
  `pqs.maxLevel`, cube-face centre) and `P3.spacing_analytic_edge_m` (1.41 times) are computed from the grid size read.
- P1 runs first in the full-mod-set block after P7 (its clock jumps return to the base time) and is exempt from the step budget.
- Spawn: `Env.spawn_ok` (pod within 500 m of the planned place and the ground below -12 m) gates P4 and the ocean discs.

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

Result column: "UNKNOWN" means not run (memory gate), not "measured and undecided". The thresholds above are the rows of
`tests/series/rows/B09.json` as signed by the last re-review (HEAD of `atmo/B09`, 2026-11-11).

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
