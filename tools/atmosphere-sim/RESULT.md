# Atmosphere simulation proof (B10): RESULT

Status: PHASE 2 done, decision FALLBACK (section P4). The header (H0-H10) was written and signed (REV-B10h-1, conditions H-1..H-4) before the criterion runs; the independent phase-2 review is open.

---------------------------------------------------------------------------------------------------------------------------

## HEADER (fixed before the runs; sign-off line at the end)

### H0 What is built

`tools/atmosphere-sim/AtmoSim.Core` (netstandard2.0, no UnityEngine, no `Math.Exp/Pow/Sin/Cos/Log/Atan` in the step: `DMath` uses only
`+ - * /`, `Math.Sqrt` and `Math.Floor`, which IEEE 754 rounds exactly). `AtmoSim.Runner` (console, net10.0) and `Tests` (MSTest, net10.0,
`dotnet test tools/atmosphere-sim/Tests`; 16 tests green at header time: DMath accuracy, spectral round trip, solid-body wind, flux
divergence, same-seed hash, thread independence, checkpoint = straight run bit for bit, clone, finiteness/bounded wind over 30 days,
water ledger closure, switches, no-ocean body, epoch derivation and order independence, sub-grid rule).

Model (one paragraph per part, enough for C25):

1. Dynamical core: two-layer quasi-geostrophic (Phillips type) model on the sphere. Prognostic: potential vorticity of an upper layer
   (sigma 0.25) and a lower layer (sigma 0.75) as spherical-harmonic coefficients (triangular truncation `DynM`, default T21, Gaussian
   grid 64x32). Inversion is diagonal in spectral space. Advection of absolute PV (beta term included, f = 2 Omega sin(lat)) by
   the layer winds, spectral transform method, SSP-RK3 with 2-24 sub-steps chosen from the previous maximum wind (Courant number),
   hyper-diffusion (time 3 h at the truncation limit), Ekman drag on the lower layer (spin-down 2 Earth days at 4x Earth rotation, scaled with
   1/sqrt(rotation)), weak drag aloft. The baroclinic deformation radius comes from N, the tropopause height and f0 (N from the lapse rate),
   scaled by `EddyScale` (default 1, the physical value). The thickness Delta = psi1 - psi2 is the temperature of the column through the thermal
   wind: T_mid = T_eq,mid(lat,t) + c(lat) (Delta - Delta_eq(lat,t)), c = f0 h(lat) / (R ln 3), h a smooth sign of f that is zero at the equator
   (regularised inverse near the equator, QG is not valid there). Delta relaxes to Delta_eq with a radiative time tau = cp ps / (g 4 sigma T^3) x 0.7
   (clamped 1-300 days). Delta_eq follows from the meridional gradient of the equilibrium mid-layer temperature by thermal wind.
2. Equilibrium: surface temperature T_eq(lat, t) = Tmean x (w + (1-w) Q(lat,t)/Qglobal)^(1/4) x normalisation, Q the daily-mean insolation from
   declination (obliquity, season phase of the year, eccentricity), w = min(0.95, 0.5 sqrt(ps/101325 Pa)) the share spread by heat transport;
   lapse rate to the tropopause, isothermal above. This is the stand-in for the B09 temperature samples. Season phase 0 = northern midwinter
   (SeasonClock). Tilt is an input of insolation (KSP has none; [VERIFY] value 23.4 degrees for the Kerbin-like set until C12 fixes it).
3. 3D fields on the physics grid (lat-lon Gaussian rows, 128x64 default, K sigma levels, spectral fields synthesised onto it): temperature
   (equilibrium profile + baroclinic anomaly with a vertical shape + skin-temperature pull on the lowest levels), horizontal wind (interpolated
   between the layer winds, boundary-layer turning and slow-down below the lower layer so that low-level flow converges into lows, plus a
   thermal-low flow toward warm air that fades where rotation dominates: sea/land breezes, the day-night circulation of slow rotators),
   vertical velocity from the QG thermodynamic equation (omega = (c (dDelta/dt + J(psi_bt, Delta) - relaxation) - latent heating) / S),
   parabolic profile.
4. Moisture on the physics grid: vapour and cloud water, semi-Lagrangian transport (trilinear, departure point in lat-lon with a polar cos floor
   0.12, global mass fixer per field), evaporation from ocean (bulk formula, reduced by ice) and from soil water on land, boundary-layer mixing,
   saturation adjustment with latent heating, moist-static-energy convection (CAPE from a parcel of the lowest two levels, adjustment time 2 h,
   35 % detrains as cloud water, 65 % rains out, convective updraft sqrt(CAPE) capped at 30 m/s), autoconversion (threshold 0.2 g/kg,
   time 40 min), fall-out with re-evaporation, column latent heating (capped at 3e-4 K/s) fed back into Delta on the next step.
5. Surface layer (2D): skin temperature (land relaxation 6 h toward air temperature + insolation anomaly, sea relaxation from the mixed-layer
   heat capacity `SeaHeatCapacityPerM x SeaMixedDepthM` over 15 W/m2/K), sea ice from the heat deficit/excess of the mixed layer at the freezing
   point (`SeaFreezeK`, latent heat per volume `IceLatentDensity`) plus Stefan growth under cold air and top melt, snow pack in metres of water
   equivalent (accumulation when the snow fraction of the wet-bulb-like temperature > 0.5, melt 0.18 mm/K/h, sublimation, wind erosion above
   12 m/s back to vapour), soil water (capacity 150 mm, overflow to the sea only on a body with a sea), dust lifted over land above a wind threshold
   on dusty bodies and settling with 2 days.
6. No storm positions, no injected noise after the initial state. The seed enters only the small initial perturbation of the streamfunctions and the
   synthetic ocean/terrain maps.

Deviations from the wording of the brief (reviewer: say if one is not acceptable):
* Temperature is diagnosed from the prognostic thickness and the surface state, not integrated as a separate 3D prognostic field. Pressure is the
  geostrophic surface pressure anomaly rho0 f0 h psi2, not a prognostic field.
* The dynamical core is balanced (QG); gravity waves are not simulated. The tropics have no Hadley circulation; tropical rain comes from
  convection on warm moist surface air (criterion 3 will show whether that is enough).
* Latent heating enters through the column mean, not per level.
* The brief asks to prove the platform functions bit-equal under .NET and Mono, or to avoid them. They are avoided (DMath). The Mono run of the
  kernel is C25's player test; it is not done here ([VERIFY] on this machine: no Mono run in this package).

### H1 Time, grid, sets, seeds, runs

* Fixed step: 1 h of game time (primary) and 3 h (A/B pair). "Day" below means one rotation period of the body (6 h for the Kerbin-like set).
* Grids: 128x64x16 (reference), 96x48x16, 64x32x12. The chosen grid is the LARGEST of the three whose step cost (criterion 5) is <= 30 ms and whose
  memory is <= 30 MB. It is decided by the timing run, which comes first and does not look at any climate output. All other runs use the chosen grid.
* Parameter sets (`BodyParams`): Kerbin-like (the fast rotator with oceans of the brief: rotation 21600 s, radius 600 km, year 9 203 545 s, tilt 0.4084,
  ocean target 0.7), Duna-like (thin, cold, dry, dusty), Eve-like (thick, hot, 70 % ocean target), Laythe-like (95 % ocean), Slow-rotator (Kerbin-like with
  rotation period 864 000 s, tilt 0.05, ocean target 0.3), No-ocean (Kerbin-like with ocean 0), Cold-world (Kerbin-like with surface mean 258 K).
  Earth-like (real Earth radius and rotation) is run as a scale sanity check only (not part of the gate).
* Seeds: Kerbin-like A = 1, B = 2, C = 3; every other set seed 1. Maps: synthetic continents of the seed (`MapGen`); B09 grids replace them when `tests/data/recon/`
  exists (it does not at header time; every run states which maps it used).
* Runs (simulated Kerbin years): Kerbin-like seed A >= 10, seeds B and C >= 5, every other set >= 5; first year is spin-up and excluded from statistics
  unless a criterion says otherwise. One detailed month (30 days) with daily outputs and 6-hourly frames for the sheets. A/B pairs: Coriolis off, ocean off,
  seed A vs B, step 1 h vs 3 h (each >= 5 years).
* Tuning cap: ONE structural change after the first complete set of runs (more levels, a baroclinic gradient, a smaller step, or `EddyScale`), then report.
  No other tuning of parameters after seeing results. Physical constants are fixed in this header's code (commit hash recorded in the sign-off).

### H2 Detectors (frozen thresholds)

Frames: every 6 h of game time. Belts: 35-70 degrees of each hemisphere.

* D1 pressure minima. Field: p' = rho0 f0 h(lat) psi2 (surface pressure anomaly) minus its zonal mean per latitude row, smoothed with a 3x3 box. A minimum is a cell
  lower than its 8 neighbours whose value is lower than the mean on a ring of radius R = max(3 cells, 0.75 x deformation radius) by at least 0.4 % of the
  surface pressure (4 hPa on 1013 hPa). Tracking: greedy nearest neighbour between consecutive frames within 25 m/s x 6 h; one missing frame allowed.
  A system is persistent if its track lasts >= max(2 days, 12 h). Metric: mean number of simultaneously active persistent tracks per hemisphere in the belt.
  Criterion 2 requires >= 3 per hemisphere on average (Kerbin-like seed A, years 2-10).
* D2 front bands. A cell is a band cell if |grad T(sigma 0.8)| >= 3 K per 100 km OR |grad CWP| >= 0.25 x (95th percentile of CWP of that frame) per 100 km
  (CWP: column cloud water path). Band = 8-connected component of band cells, extent (largest great-circle distance between its cells) >= 20 cells x 29.5 km = 590 km
  and aspect ratio (sqrt of the ratio of the second-moment eigenvalues) >= 3. A front event is a band tracked (centroid within 400 km per 6 h) for >= 12 h. Criterion 2:
  >= 1 front event per 10 days per hemisphere in the belt (Kerbin-like seed A, years 2-10).
* D3 negative controls, run on the same frames: (a) every frame spatially shuffled (cell permutation, seeded), (b) white noise with the frame's mean and standard deviation.
  Required: persistent tracks per hemisphere <= 0.1 and front events <= 5 % of the real count and <= 0.1 per 10 days, both controls.
* Tracker numbers for the reviewer (not pass/fail): count, lifetime, drift speed (east component), straightness (net displacement / path length) of the persistent tracks.

### H3 Precipitation bands (water-equivalent rate of a grid cell, mm/h; `PrecipMmH`)

| Level | snow (type snow fraction >= 0.5) | rain / sleet |
|---|---|---|
| none | < 0.02 | < 0.02 |
| Flurries (drizzle) | 0.02 - 0.10 | 0.02 - 0.5 |
| Light | 0.10 - 0.30 | 0.5 - 2.5 |
| Moderate | 0.30 - 0.80 | 2.5 - 7.6 |
| Heavy | 0.80 - 2.0 | 7.6 - 25 |
| Blizzard class | >= 2.0 AND 10 m wind >= 15.6 m/s (else Heavy) | >= 25 (downpour; no wind condition) |

Sources: snow numbers are LWE equivalents of METAR-style light/moderate/heavy snow ([VERIFY]); rain numbers are the AMS/WMO classes (2.5 / 7.6 mm/h
for light/moderate/heavy, [VERIFY]); the blizzard wind condition is the NWS 35 mph (plan 09 3.2). `PrecipRateToLevel` of C20 is piecewise linear in
log10(rate) through (rate -> level01): snow (0.02 -> 0.04), (0.10 -> 0.20), (0.30 -> 0.40), (0.80 -> 0.60), (2.0 -> 0.80), (5.0 -> 1.0);
rain (0.02 -> 0.04), (0.5 -> 0.20), (2.5 -> 0.40), (7.6 -> 0.60), (25 -> 0.80), (60 -> 1.0); the Blizzard name needs the wind condition. The per-body
tables for dust, sand, ash, methane are body configuration (C12) and not asserted here.

### H4 Criteria with their targets

| # | Criterion | PASS when (measured on the chosen grid, step 1 h unless stated) |
|---|---|---|
| 1 | Stability | every set >= 5 y, Kerbin-like >= 10 y: no NaN; max wind < 150 m/s; yearly mean global surface temperature within 3 K of `SurfaceMeanK` (years 2+); global atmospheric water in year n+1 within 1 % of year n (years 2+) and the water ledger residual (stores - initial - ocean exchange) <= 1e-6 kg/m2 |
| 2 | Emergence | D1: >= 3 persistent minima per hemisphere on average; D2: >= 1 front event per 10 days per hemisphere; D3 controls pass; no hand-placed systems (structural: no code path adds storms after the initial state, a unit test and the review of the diff) |
| 3 | Climate zones | Kerbin-like: annual zonal-mean precipitation |lat| < 10 degrees over |lat| 20-35 degrees >= 3, and |lat| > 75 degrees <= 50 % of the 40-60 degree belt. No-ocean global mean precipitation <= 5 % of Kerbin-like seed A (years 2-5). Slow rotator: amplitude of the wavenumber-1 low-level (sigma 0.9) wind vector in the Sun-fixed longitude frame, 30-day mean, 0-45 degrees, >= 2 x the amplitude of the zonal-mean wind; the Kerbin-like control must show a ratio < 1. Dust: on Duna-like, dust is lifted only in cells whose 10 m wind exceeds 10 m/s (zero lift events below it) and at least 0.5 % of land cell-hours lift. Eve-like: no sea ice, mean surface temperature within 3 K of target, global mean precipitation > 0. Laythe-like: ocean-cell mean precipitation >= 70 % of the Kerbin-like value. Duna-like: global mean precipitation <= 5 % of Kerbin-like. Polar and zonal-mean plots per set are reported |
| 4 | Absolute precipitation statistics | sites 45-70 degrees, each hemisphere in its cold season (the half of the year in which its equilibrium temperature at 57 degrees is below its annual mean), site-hours of >= 3 years, bands of H3: precipitating share (rate >= 0.02) 20-40 %; Heavy-or-above share of all site-hours in [0.5 %, 5 %]; Blizzard-class share in [0.05 %, 1.5 %]; shares of the precipitating hours ordered Light > Moderate > Heavy > Blizzard |
| 5 | Cost | measured on this CPU, one thread, Release x64 .NET, exclusive lock, median of 200 steps after warm-up: step <= 30 ms (= 0.5 ms per frame over 60 frames) and resident state + checkpoint <= 30 MB for the chosen grid; 128x64x16 and 64x32x12 reported; GPU only if the CPU variant fails at 64x32x12 |
| 6 | Determinism and rewind | identical state hash in two runs, on two threads, and for different chunkings of the query loop; checkpoint at day 10 + advance to day 20 = straight run bit for bit; restore at 10 arbitrary earlier times (including Parsek-style jumps back and forth) = straight run; catch-up of 1 y from scratch and from a checkpoint (ms) and checkpoint size reported |
| 7 | Smoothness | report the 99th percentile change per game second of the interpolated coverage and precipitation maps between steps; stand-in limit 1e-3 per game second [UNKNOWN until C23 measures the real limit] |
| 8 | Forecast | a clone run 24 h forward equals the continuation bit for bit, also across an epoch boundary and its crossfade; clone + 24 steps <= 1.5 s on a worker, and the rolling continuation <= 1.5 s per 24 h |
| 9 | Surface layer | snow depth rises during cold precipitation (correlation of dSnow with cold precipitation > 0.9 in the snow-line band) and decays above freezing (no snow cells with air > 278 K for 3 days); bounded over 5 y (year-5 maximum of the global-mean depth <= 1.2 x year-3, no cell above 10 m LWE); no water deposit at ocean fraction 0 (No-ocean snow+rain total <= 1 % of Kerbin-like); dust only above the wind threshold. Snow line table (Kerbin-like seed A, years 3-5, land cells of a 5-degree band around the latitude, both hemispheres each in its own season, cover = depth >= 2 cm snow, four season windows of +-0.05 year around phases 0 / 0.25 / 0.5 / 0.75): 0 degrees <= 0.02 all seasons; 30 <= 0.10 winter, <= 0.02 other seasons; 45 >= 0.30 winter, <= 0.05 summer; 55 >= 0.6 winter, <= 0.05 summer; 70 >= 0.85 winter, <= 0.30 summer; 85 >= 0.95 winter (polar cap), <= 0.60 summer; spring and autumn between summer-0.05 and winter+0.05. A band with fewer than 20 land cells is reported "no land" and counts as FAIL (the synthetic map is then replaced by another seed once, stated) |
| 10 | Sea ice | Kerbin-like >= 3 y: winter ice edge (lowest latitude where the zonal-mean ice fraction of the ocean cells is >= 0.15, +-0.05 year around midwinter) between 50 and 60 degrees in each hemisphere; mirrored phases (0.25 vs 0.75 of the year): see note A; lag of the maximum ice extent behind midwinter between 0.02 and 0.25 year (freeze-up and break-up durations and their ratio are reported); no ice on No-ocean; ice persists all year on Cold-world (minimum annual ice area >= 20 % of the maximum); no cell has ice where the sea temperature is more than 0.5 K above `SeaFreezeK`; no ice on Eve-like |
| 11 | Surface wind (10 m = 0.8 x lowest-level wind) | Kerbin-like all cells: P50 in 3-10 m/s, P95 in 8-20 m/s; storm-track ocean cells (45-65 degrees) P99 in 14-28 m/s; Blizzard condition (rate >= 2.0 mm/h and wind >= 15.6 m/s) share of cold-season site-hours in [0.05 %, 1.5 %]; Duna-like P50 < 10 m/s and P99 >= 10 m/s (dust threshold reachable and rare); P50/P95/P99 reported per set |
| 12 | Convective scale | cells with resolved convective updraft >= 2 m/s exist in the Kerbin-like tropics (>= 0.5 % of cell-steps), median lifetime of the 4-connected updraft clusters (overlap tracking per step) between 1 and 12 h, median cluster area >= 1 cell; the sub-grid rule (`SubGrid.Cores`) is deterministic, gives radii 5-25 km, covers <= 35 % of a cell (unit test, green); the 95th/99th percentile of the resolved updraft and the cell size in km are reported as "cell scale available at GO" |
| 13 | Bounded catch-up | worst case over 50 arbitrary UTs (steps taken x measured step cost) <= 20 s; the epoch length is DERIVED: epochSteps = floor(20 s / stepCost) - 2 spin-up - 2 crossfade; crossfade: 99th percentile per-step change of the blended coverage/precipitation maps inside the crossfade window <= 3 x the same number outside it; memory of the second trajectory counted |
| 14 | Pre-signing | this header signed by the reviewer before the runs; D3 passes |

Note A (confirmed by REV-B10h-1). The brief says "autumn ice > spring ice at mirrored phases". With ocean heat storage the physical direction at equal
insolation is the opposite (spring ice > autumn ice: the sea keeps the summer heat into autumn, the Arctic extent at the same insolation is larger in spring than in autumn).
The test is the PHYSICAL direction: ice area at phase 0.25 over ice area at phase 0.75 for the northern hemisphere, mirrored for the southern; the hemisphere mean of the
ratio must be >= 1.2 (amendment H-7, a sharpening written before the runs). A ratio below 1.2 or a reversal means the sea heat capacity is too small and is reported as such.
The brief text "autumn ice > spring ice" is superseded by this note.

Epoch/surface continuity (part of 9, 10 and 13): the snow cover fraction of every table band and the ice-edge latitude, evaluated across epoch boundaries, jump by <= 0.10 (cover)
and <= 3 degrees (ice edge) within the crossfade window; otherwise the epoch is lengthened within the 20 s bound or the slow state gets the analytic design of C26.

### H5 A/B pairs (expected effect first)

| Pair | Expected | Metric and PASS |
|---|---|---|
| Coriolis off | the vortices vanish | D1 persistent minima per hemisphere <= 25 % of the reference (>= 5 y each) |
| Ocean off | rainfall collapses | global mean precipitation <= 5 % of the reference |
| Seed A vs seed B | same climate, different weather | relative L1 difference of annual zonal-mean precipitation <= 10 %; area-weighted spatial correlation of the daily precipitation ANOMALY maps (minus their own zonal mean) after day 10 < 0.3 over the 35-70 degree belts (the raw-map correlation is reported too; it carries the common climate) |
| Step 1 h vs 3 h | statistically similar | relative L1 difference of annual zonal-mean precipitation <= 15 % |

### H6 Deliverables for the reviewer (paths in `MAINREPO\.local\work\atmosphere-sim\`)

Cloud-map contact sheets (daily over one month, equirectangular and orthographic globe at three longitudes), frame-strip sheets (6 h, 8 frames per sheet), tracker table,
cross-sections through a developing cell and a front, zonal-mean plots per set, precipitation time series at fixed sites (mid-latitude 50 N, tropics 5 N, polar 80 N), `metrics.json`
(PASS/FAIL with the detector settings), the A/B difference plots, determinism and rewind outputs, the epoch/catch-up arithmetic, the time-lapse (extra for the owner).

### H7 Premise checks at header time

* A02 platform present (`tools/gpu_lock.py`, lock free). The CPU-only runs do not need the GPU lock except the timing run (criterion 5), which takes `--exclusive`.
* B09 data (`tests/data/recon/`) absent: synthetic maps and the stand-in temperature profile are used; no premise of this brief is made false by that.
* `IWeatherField` (A01) read: sampling by (body, lat, lon, alt, ut), maps by `WeatherMapKind`, window by radius, `Advance(budgetMs)`, `StepSeconds`, `Readiness`, clone and checkpoint interfaces.
  The mapping in this file's phase 2 section follows it.
* Height/ocean grid source: `src/Atmospheres/OrbitHeights.cs` reads a grid of `OrbitMask.HeightWidth x HeightHeight` PQS heights (B13 may move it); the production port of `OceanFraction`/`TerrainM`
  is C25's work and is not needed for the proof.

### H8 Amendments required by the sign-off (written before any criterion run; they close open definitions and change no threshold)

* H-1 Epoch parameters. Spin-up window = 20 body days (rotation periods of the body, 120 steps on the Kerbin-like set at 1 h), crossfade length = 4 body days (24 steps).
  `epochSteps = floor(20 s / stepCost) - 2 x spinUpSteps - 2 x crossfadeSteps` with `spinUpSteps = 20 x RotationPeriodS / StepSeconds` and
  `crossfadeSteps = 4 x RotationPeriodS / StepSeconds`. They are not changed after results; the one structural retry may change them and the change is reported.
  The slow surface state (snow, sea ice, soil water) is re-initialised from climatology at each epoch start. At a step cost around 30 ms the whole worker budget is ~660 steps
  (~28 game days on the Kerbin-like set), so an epoch is short against the seasonal memory of snow and sea ice; the header expects the across-epoch continuity part of
  criteria 9 and 10 to FAIL in that case. A FAIL there is reported as FAIL and triggers the analytic slow state of C26 (fallback hybrid description), not tuning.
* H-2 Criterion 2 and `EddyScale`. `EddyScale` is a gameplay scale, not physics. A pass of criterion 2 reached only with `EddyScale` < 1 is reported as "PASS with EddyScale = x (gameplay scale)",
  not as an emergent result, and it counts as the one structural change. The tracker numbers of the unmodified (`EddyScale` = 1) run are reported in any case. No threshold changes.
  Physical expectation on the Kerbin-like set: deformation radius about 280 km, fastest-growing wavelength about 1100 km, about 2 waves on the 50 N circle (2424 km), so 2-4 simultaneous lows per hemisphere.
* H-3 Criterion 3 and the subtropical dry belt. The model has no Hadley circulation and no subsidence. A FAIL of the zonal ratio part of criterion 3 (tropics over subtropics >= 3) is reported
  as FAIL and enters the FALLBACK evaluation. The single structural retry may be spent on a stated Hadley/subsidence term (a zonal-mean tropical overturning derived from the zonal-mean heating, which dries
  the 15-35 degree belt); this term is named here so that it is not an after-the-fact choice. If the retry is spent on it, it is not spent on criterion 2.
* H-4 Persistence of D1 is 12 h of game time (2 body days on the Kerbin-like set is 12 h; the wording "max(2 days, 12 h)" is replaced by "12 h"). Track swaps (a track whose successor is closer to another
  track's last position than to its own) are an additional reported tracker number; the tracking radius 25 m/s x 6 h = 540 km against an expected spacing of ~1100 km makes swaps possible.
* H-5 The tracker reports front events split by arm (thermal / cloud-water-path / both). The real field with the frame order shuffled in time (spatially intact) is reported for D1 as an extra (tracks should collapse); it is not a pass/fail control.
* H-7 see Note A (ratio >= 1.2).

### H9 Implementation definitions of the analysis tools (written before the criterion runs; they close wording of H2-H5 and change no threshold)

* Frame = 6 h of game time. "Day" = one rotation period of the body (H1). Front events are reported per 10 body days (the pass rule) and per 10 Earth days.
* D1 minima that lie within the ring radius R of a deeper minimum of the same frame count as one system (non-maximum suppression); the count without it is reported too.
* Statistics start with year 2 (year 1 is spin-up); detector frames of years 2 to the end; the snow-line table, ice tables and the snow/ice annual cycles use years 3-5.
* Snow cover: `SnowW` (m of water equivalent) >= 0.02 on land cells (ocean fraction < 0.5); "2 cm" is read as 2 cm of water equivalent, "10 m LWE" as `SnowW` <= 10 m.
* Cold season of a hemisphere: the phases at which the daily-mean insolation at 57 degrees (with the eccentricity factor) is below its annual mean. Sites = grid cells with |lat| 45-70 in the hemisphere's cold season.
* Criterion 4 classes follow H3 (blizzard class = snow with rate >= 2.0 mm/h and 10 m wind >= 15.6 m/s, or rain >= 25 mm/h). Criterion 11 blizzard condition is the literal rate >= 2.0 mm/h and wind >= 15.6 m/s for any type (snow-only is reported).
* Wind percentiles of "all cells" are area-weighted (the unweighted ones are reported). Storm-track cells: ocean fraction >= 0.5, |lat| 45-65.
* Sea ice fraction of a cell = 1 - exp(-IceH / 0.15 m) (as in the model); zonal-mean ice fraction is the mean over the ocean cells of a row; ice area is the area-weighted global fraction. Midwinter = year phase 0 (north) and 0.5 (south).
* Wave-1 test (criterion 3): last 30 body days of the run, level nearest sigma 0.9, rows |lat| <= 45, longitude relative to the sub-solar longitude of the model's geometry; amplitude = sqrt(cos and sin coefficients of u and v squared), averaged over rows, over the amplitude of the 30-day mean zonal-mean u.
* Criterion 12: clusters of resolved updraft >= 2 m/s over the whole grid (4-connected), overlap tracking per step in years 2 and 3; shares measured in the tropics (|lat| < 20 degrees).
* Criterion 7: coverage = min(1, cloud water path / 0.3 kg m-2), precipitation level = piecewise log-linear `PrecipRateToLevel` of H3; the 99th percentile of |change| per game second over all cells and steps of year 2.
* Epoch tests (criteria 9, 10, 13): two boundaries of the pipeline closest to year 3 phase 0.12 and 0.55; start and end of the crossfade window are compared (snow cover of the six table bands, ice-edge latitude); the crossfade smoothness compares the blended maps inside the window with the same maps in the 24 steps before and after it.
* Seed comparison maps: 24 h means of the precipitation rate, days 10-70 of year 2. Zonal means of the A/B pairs are the means over years 2 to the end of each run.
* A map-generator defect (the ocean quantile search divided by an extra factor of the longitude count, which gave all-ocean maps) was fixed before any criterion run; the synthetic land share is then about 28-32 % with the seeds used.
* Mono: no Mono run in this package (C25's player test).

### H10 The one structural retry (decided after the first complete set of runs, before the retry runs)

First set (EddyScale = 1): no persistent pressure minimum in either hemisphere (criterion 2), surface wind P50 about 1.4 m/s. The thermal-wind shear of the model's winter equilibrium
gradient (about 46 K over 60 degrees of latitude) is about 10 m/s, while the two-layer critical shear is about beta x Ld^2 = 6.2e-10 /(m s) x (282 km)^2 = about 48 m/s on this planet, so the physical
baroclinic core is stable at the physical deformation radius. The retry is `EddyScale` = 0.5 (Ld^2 four times smaller, critical shear about 12 m/s), one value, all sets rerun with it, nothing else changed.
Per H-2 a pass of criterion 2 reached this way is reported as "PASS with EddyScale = 0.5 (gameplay scale)". No second retry.

### Sign-off

Reviewer: REV-B10h-1  date: 2026-11-11  verdict: SIGNED under conditions H-1 to H-4 (written above in H8)
Core commit at header time: efc650c4

---------------------------------------------------------------------------------------------------------------------------

## PHASE 2 (after the sign-off)

Evidence for the independent reviewer is in `C:\Users\franz\IdeaProjects\ReDefinition\.local\work\atmosphere-sim\` (first set, EddyScale 1) and `...\retry_eddy0p5\` (the one structural retry):
`metrics.json` (all items with value, threshold, PASS/FAIL), `criteria.md`, `sheets\` (contact sheets, strips, globe views, cross-sections, zonal plots, snow/ice plots, site series, A/B plots, `tracker_table.md`, `timelapse_cloud.gif`),
`<run>\stats.json` per run, `cost.json`, `determinism.json`, `forecast.json`, `epoch.json` (clean repeat; `epoch_first_run.json` is the first one), `subgrid_test.txt`. Reproduce: `AtmoSim.Runner run|cost|determinism|forecast|epoch|evaluate`, then `plots/make_sheets.py <root>`.
Data used by every run: synthetic continents of the seed (`MapGen`, land share 28-32 %), stand-in equilibrium temperature (no B09 data yet), Kerbin-like tilt 23.4 degrees [VERIFY], step 1 h (3 h for the A/B pair), grid 128x64x16 (T21 core, 64x32 core grid).

### P1 Chosen grid, step, epoch, golden hash

* Grid 128x64x16 (largest of the three whose median step is <= 30 ms: 28.94 ms; 96x48x16 22.05 ms; 64x32x12 16.73 ms; p95 37.2 / 27.3 / 21.6 ms). Resident state 11.3 MB, checkpoint 2.56 MB, two trajectories during a crossfade 22.6 MB + checkpoint (all <= 30 MB).
* Step 1 h. Epoch: spin-up 120 steps (20 body days), crossfade 24 steps (4 body days), epochSteps = floor(20 s / 28.94 ms) - 2 x 120 - 2 x 24 = 403 steps = 67 body days = 16.8 Earth days = 0.158 year.
* Golden hash (Kerbin-like, seed 1, 128x64x16, step 1 h, 30 days = 720 steps from `Init`): `238C8B2E4DDD9B53`; after 20 body days (120 steps): `587B0AF257F5E605`. Same hash across two runs, two threads and three chunkings.

### P2 Criteria (first set, EddyScale 1; the retry set gives the same pass/fail per criterion, differences below)

| # | Result | Numbers (value, threshold) |
|---|---|---|
| 1 Stability | FAIL | NaN at step 1 for Duna-like and Slow-rotator (and the Coriolis-off pair); Kerbin-like seed A max wind 152 m/s in year 1 (years 2+ <= 105), Laythe-like 243, Cold-world 178, No-ocean 152 (< 150); yearly atmospheric water drifts 4.3 %/year on Kerbin-like seed A (31.7 -> 26.8 kg/m2 over 10 years; <= 1 %), also Eve 8.8 %, Laythe 10.8 %, No-ocean 9.6 %; mean T2m off target by 3.4 K (seed B), 4.3 (Laythe), 7.8 (Cold-world) (<= 3 K); water ledger <= 1e-6 on all finite runs |
| 2 Emergence | FAIL | D1 persistent minima per hemisphere 0 / 0 (>= 3), ring radius 212 km; D2 front events 0.15 (N) and 0.09 (S) per 10 body days (>= 1; 0.58 and 0.34 per 10 Earth days), arms thermal 51, cloud-water 37, both 1; D3 shuffled-space control 0.05 (pass), white-noise control 0.41 persistent minima (> 0.1, FAIL), front-event controls pass; no-hand-placed-systems structural check passes |
| 3 Climate zones | FAIL | tropical/subtropical ratio 5.89 (pass); polar over mid-latitude belt 0.97 (<= 0.5); No-ocean precipitation 14.3 % of Kerbin-like (<= 5 %); Slow-rotator wave-1 test not available (NaN); Kerbin-like control ratio 0.07 (< 1, pass); Duna-like dust test not available (NaN); Eve-like no ice, precipitation 0.12 mm/h; Laythe-like ocean precipitation 2.27 x Kerbin-like |
| 4 Absolute statistics | FAIL | precipitating share 24.4 % (pass); Heavy-or-above 0.25 % (0.5-5 %); Blizzard class 0 % (0.05-1.5 %); ordering light > moderate > heavy holds, blizzard is empty |
| 5 Cost | PASS | 28.94 ms per step, 13.2 MB at 128x64x16 (and 22.0 ms / 7.6 MB, 16.7 ms / 2.9 MB for the smaller grids) |
| 6 Determinism and rewind | PASS | identical hashes (runs, threads, chunkings), checkpoint at day 10 + 10 days = straight run, checkpoint at 0.9 year + remaining = straight run, 10 of 10 arbitrary earlier UTs equal a fresh pipeline; one year from scratch 75.7 s, from the checkpoint 7.2 s; checkpoint 2.56 MB |
| 7 Smoothness | PASS | 99th percentile change per game second: coverage 2.5e-6, precipitation level 6.3e-5 (stand-in limit 1e-3, UNKNOWN until C23 measures) |
| 8 Forecast | PASS | clone + 24 steps 0.80 s, rolling 24 steps 0.71 s (<= 1.5 s), equal bit for bit, also across the epoch boundary and crossfade |
| 9 Surface layer | FAIL | dSnow vs cold precipitation correlation 0.99995 (pass); 41 352 land cell-steps with snow >= 2 cm after 3 body days above 278 K (= 0); bounded (pass); table: N 70 and 85 degrees have no land on seed A (counts FAIL), N 45 has 13 land cells, winter cover 0.21 (N 45), 0.15 (N 55), 0.17 (S 45), 0.05 (S 55), 0.06 (S 70), 0.44 (S 85) against 0.3 / 0.6 / 0.3 / 0.6 / 0.85 / 0.95; summers 0; across the epoch crossfade the snow cover of the 55 N band jumps by 0.90 (<= 0.10) |
| 10 Sea ice | FAIL | winter ice edge N 51.6 (pass), S 43.3 degrees (50-60); spring/autumn ice ratio 1.4 (N) and 30.6 (S) (>= 1.2, pass); lag of the maximum 0.145 y (N) and 0.075 y (S) (pass); no ice on No-ocean and Eve-like; Cold-world minimum/maximum ice area 1.0; ice edge jumps by 11.2 and 5.6 degrees across the epoch crossfade (<= 3) |
| 11 Surface wind | FAIL | Kerbin-like P50/P95/P99 1.45 / 8.65 / 22.65 m/s (P50 3-10: FAIL; P95 pass); storm-track ocean P99 2.75 m/s (14-28); blizzard condition share 0 (0.05-1.5 %); Duna-like winds undefined (NaN) |
| 12 Convective scale | PASS (numbers), see caution | 15.7 % of tropical cell-steps have a resolved updraft >= 2 m/s; the updraft P95 and P99 sit at the 30 m/s cap of the scheme; cluster lifetime median 1 h (one step), area median 1 cell; cell 29.4 x 29.2 km; sub-grid unit test green |
| 13 Bounded catch-up | FAIL (by 0.9 %) | 50 arbitrary UTs 19.65 s at the median step cost (pass); measured wall time of the worst catch-up 20.18 s on the clean repeat (20.47 s on the first run) (<= 20 s); crossfade p99 change inside / outside ratio 0.8-1.5 (<= 3, pass); real mean step in the pipeline is about 30 ms, the median 28.9 ms was used by the derivation |
| 14 Pre-signing | FAIL | header signed (REV-B10h-1, H-1..H-4 written before the first run); D3 negative controls: white-noise control finds 0.41 persistent minima per hemisphere (> 0.1) |
| A/B | FAIL | Coriolis off: the run is NaN (no usable pair); ocean off: precipitation 14.6 % of the reference (<= 5 %); seed A vs B: zonal-mean L1 43 % (<= 10 %; the seed also changes the synthetic continents), anomaly-map correlation 0.49 (< 0.3); step 1 h vs 3 h: L1 222 % (<= 15 %), the 3 h run reaches 445 m/s |

Retry set (`EddyScale` = 0.5, all sets, H10): criteria 5, 6, 7, 8, 12 pass and 1, 2, 3, 4, 9, 10, 11, 13, 14 fail as before. Differences: D1 persistent minima 0.003 (N) / 0 (S) per hemisphere, D2 events 0.09 / 0.24 per 10 body days; both D3 controls fail (9.7 persistent minima per hemisphere on the shuffled and the noise field, ring radius 106 km); Kerbin-like wind P50/P95/P99 2.65 / 12.35 / 24.25 m/s, storm-track P99 10.5 m/s; tropical/subtropical ratio 1.70 (fail); Heavy-or-above 3.9 % (pass) and blizzard class 0.003 % (fail);
winter snow cover on the Kerbin-like seed B (the replacement seed with land in the high-latitude bands) N 45 0.85, N 55 0.99, N 70 1.0, N 85 0.86 with summers 0; seed A bands without land stay FAIL; S winter ice edge 85 degrees; precipitation of Laythe-like 5.0 x Kerbin-like with 61 m/s P99 wind; max wind 195-309 m/s in year 1; the tracker numbers are in `retry_eddy0p5\sheets\tracker_table.md`.
Tracker (first set, seed A): no persistent pressure minimum; the D2 bands live 108 h median (250 h mean) with an east drift of 0.01 m/s median: they are quasi-stationary gradient bands, not moving systems. A "PASS with EddyScale = x" does not occur.

### P3 Findings of the runs (defects of the engine, not of the proof harness)

* The two-layer QG core is baroclinically stable on this planet at the physical deformation radius (shear about 10 m/s against about 48 m/s); with `EddyScale` 0.5 the winds grow (P50 1.45 -> 2.65 m/s) but no travelling cyclones appear. The cloud sheets show comma-shaped cloud patterns in the month frames (strip_2.png); the detector finds no pressure minima of 4 hPa depth, the surface-pressure anomaly has a standard deviation of about 0.5-1 hPa.
* NaN at the first step for the Duna-like body and the slow rotator and with Coriolis off (`AtmoSim.Runner nanscan`, `nanscan2`): rotation 864 000 s alone triggers it on the Kerbin-like body; for Duna-like the radius, the rotation or the tropopause height each removes it when set to the Kerbin value. The cause is not located within the time box.
* A checkpoint taken late in a year-long run did not continue bit for bit (the winds of the last step choose the sub-step count); fixed in `Atmosphere.cs` (the checkpoint carries `MaxWind` and the ledger), test `CheckpointLateInALongRunEqualsTheStraightRun`.
* The synthetic map generator produced all-ocean maps (the ocean quantile search divided by an extra factor); fixed before the first run.
* Slow state (snow, sea ice) re-initialised from climatology at each epoch start jumps by 0.9 (cover) and 5.6-11.2 degrees (ice edge) across the crossfade, as the header (H-1) predicted: the continuous snow line and ice edge need the analytic slow state.
* Water: atmospheric water decreases by 4 %/year on the Kerbin-like set over 10 years without settling (the model dries out slowly).
* The seed pair compares different synthetic continents; "same climate" cannot hold by construction (maps are seed-dependent). The retry set gives 27 % L1 and 0.23 anomaly correlation.

### P4 Decision: FALLBACK

Trigger evaluation: GO requires all 14 criteria PASS. Criteria 1, 2, 3, 4, 9, 10, 11, 13 and 14 fail on both sets, including the central one (2: emergence) with and without the structural retry, the cold-season absolute statistics (4), the surface wind (11) and the Duna-like/slow-rotator/Coriolis-off runs (NaN). The decision is FALLBACK (C26). The independent reviewer's verdict "coherent moving systems" is not given by the numbers (no persistent minima, stationary bands).

### P5 What the hybrid (C26) takes from this package

* Kept as engine parts (all green here): `DMath` (deterministic functions), `Rng`, `SubGrid.Cores` (sub-grid cores 5-25 km, deterministic), epoch re-anchoring with crossfade and its cost arithmetic (epoch 403 steps at 28.9 ms), checkpoint/clone/forecast, the state hash, the surface-layer bookkeeping (snow/ice/ledger), the 128x64 maps and their cost (28.9 ms per step is the budget for the whole hybrid), the `PrecipRateToLevel` bands of H3.
* To be replaced: the emergent dynamics. A deterministic seeded kinematic system generator along storm-track belts (cyclone/front objects as pure functions of seed and UT that persist for days and drift with the zonal flow, written into the same 128x64 fields: pressure, cloud water, precipitation, wind) plus an analytic seasonal climatology for the slow state (zonal-mean temperature, snow line and sea-ice edge as functions of the season phase with a stated lag), which also removes the epoch discontinuity of snow and ice. The tracker numbers (count, lifetime >= 12 h, east drift, straightness) and the D1/D2 detectors of this package are the acceptance harness of that hybrid.

### P6 Mapping to `IWeatherField` (for whichever field becomes the engine)

Level01 and the type from `PrecipMmH` and `SnowFall` through the H3 tables; precipitation visibility and `OpticalDepthVertical` from `CloudPath` and `PrecipMmH`; wind and gusts from `U3`/`V3` at the lowest levels and `Wind10`; coverage and type maps from `CloudPath` and the cloud-water profile; the snow map from `SnowW`, ice from `IceH`; the 3D window from `Qc`/`T3` on the 16 sigma levels with the sub-grid cores of `SubGrid.Cores` inside cells with a resolved updraft.
