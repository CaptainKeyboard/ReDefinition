# K-pre reconnaissance: measured state of the premises (package B09)

Starts of K-pre counted: three (2026-11-11 08:37 run2, no data, the assembly did not load; 11:38 run1; 12:13 run2). That is the plan's two starts
plus the one reserve; a further start needs the owner's consent (master plan 9.3). The measured values below come from run1 (full mod set, 378 s,
`Probe: PASSED`) and run2 (Scatterer absent, 12:13, `Probe: PASSED`, table: 27 rows PASS, 14 FAIL, all explained below).
Dumps: `tests/data/recon/dumps/run1-*.json` and `run2-*.json`; runner reports and KSP.log copies are in `build/atmo-series/` of the worktree.
Verdicts follow the thresholds of `tests/series/rows/B09.json`.

## Which failures were defects of the probe, which are findings

| id | what run1 / run2 read | probe defect or finding |
|---|---|---|
| P1 | run1: KSC -9.1e6 .. +9.1e6 K, poles -50, altitude without effect | Probe defect, fixed and re-measured in run2. `CelestialBody.GetFullTemperature(Vector3d)` returns only the sun-based offset (decompiled `CelestialBody.cs`: it ends in `GetAtmoThermalStats`' `atmosphereTemperatureOffset`) and hands that method an unnormalised sun vector, whose dot products reach `latitudeTemperatureSunMultCurve` as numbers of the order of 1e7. The same call at the pad reads 4.5e6 in run2 (`P1.stock_position_overload_ksc_value`). A stock-API finding: the position overload is unusable. |
| P5 | run1: path B silent, pause not silent, pan 0.571 | Partly a defect: the sources were placed by the transform of a listener that was not the active one, and the game's own sounds (left 0.11, right 0.60) were louder than the tones. Run2 mutes all other sources (782) and uses the active listener (`AudioListener` object; `Camera ScaledSpace` is an inactive one). The results are findings, below. |
| P6 | run1: copies invalid, `depth_ok` 0 | Defect: validity demanded sky and surface in the frame, and the frame had no sky pixels. Run2 tests that the depth holds two calibration balls in order. |
| P7 | run1: 0.340 against 0.500 | Finding for the full stack (TUFX, Deferred, Scatterer); not repeatable in run2, see P10. |
| P8 | run1 and run2: no candidate | Defect: `ShaderTagId.name` read empty, so no shader of any mod passed the lit-pass test (the survey of run2 lists `Custom/Parallax`, `ReDefinition/Scenery/Diffuse Ground KSC` and the Deferred shaders as "no lit pass"). Fixed (tags compared by id), not yet run. |
| P9 | run1 and run2: no render texture held by the layer's material | Undecided: the property `CloudCoverage` exists on all eight Kerbin materials (shader `EVE/RaymarchCloud`), and none read back the render texture just assigned. Diagnostics added (what is read back); not yet run. |
| P10, P7 in run2 | every frame read in run2 had the same luma 0.42321312 and the same peak 1.49, disc on and off, balls, pod, fog | Defect of the frame source: the flight camera's target did not follow the scene in run2 (in run1 it did). A liveness step (a flat wall in front of the camera, read from the target and from the screen, the live source is used) is added; not yet run. |
| P11 | run1: 5 km 0.662 | Unexplained; the quarter-region diagnostics exist only in run2's stock case, which had the frozen frame. |
| P12 | run1: not monotone | By design a raster-only measurement; stands as UNKNOWN for the RT sky. |

## Premise table

| id | measured | verdict | consequence taken |
|---|---|---|---|
| P1 | run2, 3328 samples, all finite; 4 of 4 curves present by the plan's names; Kerbin at KSC 305.0 .. 313.1 K over 4 phases x 4 local hours (mean 309.1, altitude curve alone 287.6, difference 21.5 K), sea level minus 10 km 89.6 K; Kerbin all samples 205.7 .. 314.1 K, poles 240.6 K; Duna 177.2 .. 271.6, Laythe 204.9 .. 288.8, Eve 300.8 .. 429.0 K; the clock jumps moved the true anomaly by 90 degrees per quarter (Kerbin -122.7, -32.7, 57.3, 147.3) and the pod survived; seasonal swing at KSC 7.8 K | PASS | The field target reads the temperature as `GetFullTemperature(altitude, offset)` with the offset from `GetAtmoThermalStats(false, sun, unitSunVector, dot, up, altitude, ...)`, never from `GetFullTemperature(Vector3d)`. The valid dumps are `run2-temperature-*.json` (the run1 files were not temperatures and are removed). B10 final run and C25 need no other source |
| P2 | Kerbin 360 x 180 complete, height -1334.7 .. 6140.0 m, ocean fraction 0.521; Duna complete (176 .. 7826 m, ocean 0); Laythe complete (-2786 .. 6166 m, ocean 0.932); Eve complete (-1775 .. 6572 m, ocean 0.344); 0 NaN | PASS (Kerbin 0.5 .. 0.8, at the edge of the range) | B10 and C25 may use real grids for these four bodies |
| P3 | top level `maxLevel` 10; 972 leaf quads read; vertex spacing near the pod over the sea 80.4 m (quad read 77.9 m, grid 15 x 15); analytic 65.7 m (cube-face centre), 92.7 m (edge); Parallax `_DisplacementScale` 0.3 (Kerbin), offset -0.5, max tessellation 64, edge length 4, range 30 m | PASS (numbers recorded) | D34: spacing alone does not force ribbons; land spacing is the analytic row, not measured |
| P4 | `UpdateAerodynamics` override registered through ModularFlightIntegrator (hook called 5370 times, 3000 shifted); `Part.dragVector` exists, `dragVelocity` does not; canopy rigidbody found and its own; 20 m/s wind: horizontal part of `dragVector` 19.998 m/s, drift 594.1 m against 594.35 m predicted (error 5.6e-5 over the last 15 s); control without wind 0.03 m/s and 0.048 m; terminal velocity 5.05 m/s | PASS | D31 uses H1 (the `dragVector` injection in the ModularFlightIntegrator slot); H3 stays unneeded. ModularFlightIntegrator is MIT (sarbian) |
| P5 | six volume fields; 787 sources (781 without a mixer group, 6 EXTERIOR); `KerbalEVA` has one audio member (`weldingLasersFX`), 43 FSM states, movement members `footPivot`, `tgtBoundStep`, `boundStepSpeed`, `lastBoundStep`, `boundLeftFoot`. Run2, others muted (baseline 0.0): path A (`OnAudioFilterRead`): source left L 0.353 / R 0.353, source right L 0.353 / R 0.447, so no 3D pan (pan 0.117); path B (`AudioClip.Create` stream): source right L 0.000 / R 0.201 (pans), source left 0.000 / 0.000 (stream lag: 5 of 26 callbacks ran with the tone on), pan 1.0 from one side. Pause: `FlightDriver.SetPause` sets `Time.timeScale` 0 but not `AudioListener.pause` (flag 0), output unchanged (0.555 against 0.555); `AudioListener.pause = true` lowers the output from 0.555 to 0.353, which equals path A's un-panned level alone, so path B was silenced and path A was not | FAILED against the thresholds (pan 0.6, silence 0.001); informative | D33 uses path B, not A: A is not spatialised and ignores the listener's pause. Footsteps use the stride fallback (no step hook on `KerbalEVA`). D33 sets or follows `AudioListener.pause` itself, since the game's pause does not. The left side of B and path B alone under pause were not measured separately [VERIFY in the first D33 series] |
| P6 | run2 (Scatterer absent): 7 cameras, main `Camera 00` 1707 x 960, DeferredShading, near 0.21; calibration balls at 3 m and 9 m, empty view: depth 0.3692 / 0.3650 / 0.3354 at AfterForwardOpaque, BeforeForwardAlpha and the consumer; the copy at AfterForwardOpaque is valid, is read intact at BeforeForwardAlpha, and the copy made at BeforeForwardAlpha is read intact at AfterForwardAlpha; the global depth at AfterForwardAlpha is overwritten (mean 0.9997, all far) and at BeforeImageEffects again (1.197); AfterDepthTexture and AfterGBuffer never execute. Run1 (full set, before the ball test): AfterForwardOpaque holds 0 (Scatterer present), BeforeForwardAlpha and AfterForwardAlpha equal (mean 0.810) | `P6.depth_ok` 1 in run2 (through the copy; `depth_readable_3000` 0) | D32, D37 and D38 take the CommandBuffer copy taken at BeforeForwardAlpha (valid in both mod sets by the taps; with Scatterer the copy at AfterForwardOpaque is empty), and read it after the transparent queue; they do not rely on the global `_CameraDepthTexture` surviving the transparent queue without Scatterer. Whether a material at queue 3000+ sees the global texture before it is overwritten was not measured (the taps are command-buffer reads). Frost draws over the flight camera only |
| P7 | run1: predicted share 0.4996 (Exp2, eye depth 2498.7 m), measured 0.3405 (error 31.8 %); fog off 0.0176. Run2: no usable frame | FAILED (informative) in the full stack; control PASS | C22 does not rely on Unity's fog formula alone: the veil carries the look |
| P8 | no opaque lit shader of a loaded mod bundle was identified (run1 and run2, probe defect fixed afterwards); survey of run2: 135 shaders that are not Unity's, 64 left out by name, 71 without a readable lit pass, `survey` in `run2-shaders.json`. Substitute from run1 (Unity Legacy Diffuse against KSP/Diffuse): balls 3.33, pod 0.655, terrain stand-in 4.95; forward-only (Legacy VertexLit): 1.47, 0.242, 3.71 | FAILED / not measured for the blocking case | D34 shells fall back to variant (f) (the trigger `forward_luma_ratio` outside 0.9 .. 1.1 holds at 3.71 in run1) until the foreign-shader case is measured |
| P9 | EVE present; 8 layers on Kerbin (2 own `ReDefinition-Atmospheres-Storm` / `-Snow` 4096 x 2048; `Kerbin-base-layer`, `Kerbin-cumulonimbus-layer` 8192 x 4096; `Kerbin-Weather-1`, `-weather-2`, `Kerbin-Snow-Particles-1`, `-2` 2048 x 1024), 8 volume, 3 flat; `CloudsManager.onApply` found; run2: all eight materials are `EVE/RaymarchCloud` with a `CloudCoverage` property, none read back the assigned render texture | PASS for the list, UNKNOWN for the re-bind latency (informative) | C23 sets its watchdog without a measured number: a conservative default and the real latency logged in the C23 series. C23's read-back (`EveBinding.Intact`) assumes a render texture assigned with `SetTexture` is read back by `GetTexture`; the probe did not see that on any layer: C23 must check this first [VERIFY] |
| P10 | not measured: the frames of run2 were frozen | UNKNOWN (blocking) | D35 stays on the orbit-only fallback until P10 is measured |
| P11 | run1: disc fraction 1.0 at 50 m (control 0.001, luma ratio 24.4), 1.0 at 500 m (control 0.004, ratio 10.3), 0.662 at 5 km (control 0.057, ratio 4.58); lift 0.3 m: 0.794 / 0.525 / 0.672 | PASS at 50 m and 500 m; FAILED at 5 km and in the low-lift variant (threshold 0.9) | D35: opaque disc proven near the surface only; the 5 km failure is not explained -> D35 orbit-only fallback applies |
| P12 | frame luma 0.2094 at ambient factor 1.0, 0.2143 at 0.7, 0.2139 at 0.4; spread 0.0215 | FAILED the monotone test (informative; raster ambient only, no RT frame read) | C24 notes: no result for the RT sky; measure with an RT frame in the C24 series |

## Briefs that change (for the orchestrator)

- B10 (final inputs) and C25: temperature from `GetFullTemperature(altitude, offset)` with `GetAtmoThermalStats` (unit sun vector); dumps `run2-temperature-*.json`.
- D31: H1 (`dragVector` injection), as planned.
- D33: path B (`AudioClip.Create` stream), `AudioListener.pause` handled by D33 itself, stride fallback for footsteps; path A is out.
- D32, D37, D38: depth copy at BeforeForwardAlpha, consumed after the transparent queue; no reliance on the global texture without Scatterer.
- C22: the veil carries the look (fog fraction 0.34 against 0.50).
- D34: variant (f) until a foreign shader is measured.
- C23: watchdog default; first check that a render texture assigned to `CloudCoverage` is read back (P9).
- D35: orbit-only fallback (P10 unknown, P11 fails at 5 km).
- C24: no RT-sky result.

## Names that differ from the plans

- `Part.dragVector` (also `dragVectorMag`, `dragVectorDir`, `dragScalar`, `dynamicPressurekPa`); there is no `dragVelocity`.
- ModularFlightIntegrator 1.2.10 (MIT) with `RegisterUpdateAerodynamicsOverride`; KSPCommunityFixes is present and replaces `UpdateAerodynamics`.
- `CelestialBody.GetFullTemperature(Vector3d)` is the sun-based offset, not a temperature; `GetFullTemperature(double altitude, double offset)` is the temperature. The assemblies of EVE and Parallax are `EVEManager` and `ParallaxContinued`.
- `KerbalEVA` has no step sound hook and no `OnGround`.

## Not measured, and why

- P10, and the P7 and P8 cases in run2: the frame source did not follow the scene; a liveness step and a screen fallback are in the probe, unrun and unsigned.
- P8's blocking case (foreign opaque lit shader): the lit-pass test was broken; fixed, unrun.
- P9 re-bind latency and light-volume latency; P12 RT sky; land vertex spacing (KSC, 55 N) is analytic only.

## Runs

K-pre starts counted: three, the plan's allowance. A fourth start (P10, the P8 foreign case, P9 latency, P7 without Scatterer) needs the owner's
consent and a new plausibility review of the changes after the last SIGN (frame liveness, lit-pass test, P9 read-back).
