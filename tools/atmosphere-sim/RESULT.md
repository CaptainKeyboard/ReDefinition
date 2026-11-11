# Weather generator proof (B10, rewritten after the owner ruling): RESULT

Status: PHASE 1 (build and header). The header below is to be signed by an independent reviewer BEFORE any criterion run. No criterion run has been made; exploratory runs made while building are listed in H9.
Phase 2 (criterion runs, sheets, decision GO or REWORK) follows only after the sign-off.

The first prototype of this package (a two-layer spectral model that failed 9 of 14 criteria) is kept as history at the end of this file. Its core (`Atmosphere`, `Dynamics`, `Spectral`, `Physics`, `EpochField`) and the tests of those parts are deleted.

---------------------------------------------------------------------------------------------------------------------------

## HEADER (fixed before the runs)

### H0 What is built

`tools/atmosphere-sim/AtmoSim.Core` (netstandard2.0, no UnityEngine, no `Math.Exp/Pow/Sin/Cos/Log/Atan/Tanh` in the generator: `DMath` uses only `+ - * /`, `Math.Sqrt`, `Math.Floor` and the exact bit pattern of 2^n, all exactly rounded or exact in IEEE 754)
holds the generator. `AtmoSim.Runner` (console, net10.0) holds the detectors, trackers, study and timing commands. `Tests` (MSTest, net10.0) runs with
`python tools/dotnet_test.py --project tools/atmosphere-sim/Tests/AtmoSim.Tests.csproj`.

| file | content |
|---|---|
| `Core/DMath.cs` (kept, extended) | deterministic Exp, Log, Pow, Sin, Cos, Atan, Atan2, Asin, Acos, Tanh, a fast tanh, SmoothStep, WrapPi |
| `Core/Hashing.cs` | integer hash (SplitMix style) with `Of(seed, a, b, c)`, `Draw`, the xorshift `Rng` for the synthetic maps |
| `Core/BodyParams.cs` | the inputs of the generator per body and the eight parameter sets (H2) |
| `Core/Circulation.cs` | `BodyDerived`: everything derived once from the body (Rossby and Rhines rules, cell count, jet, wave gate, temperature climatology, lattice size, precipitation bands) |
| `Core/Generator.cs` | `WeatherGenerator.Sample(lat, lon, alt, ut, out Weather)`: climatology, circulation, wave systems and fronts, uplift, cloud water, rain, convective cells, wind, type, dust flag, vertical profile |
| `Core/SurfaceLayer.cs` | `IceModel` (heat store, Stefan growth, melt, snow insulation) and `SurfaceSite` (snow, wet/dry, dust, ice of one site as a function of the UT) |
| `Core/MapGen.cs` (adapted) | `HeightGrid` (ocean, terrain, smoothed moisture source, bilinear lookup) and the synthetic continents of a seed |
| `Core/SubGrid.cs` (adapted) | maps and the 3D window by point evaluation |
| `Runner/Detectors.cs` (kept, adapted to grids) | metric grid, D1 and D2, tracker, controls |
| `Runner/Study.cs`, `Timing.cs`, `Evaluate.cs`, `Json.cs`, `Program.cs` | tracker run, frame export, zonal and front debug prints, cost and determinism runs, PASS/FAIL table |
| `plots/make_sheets.py` (adapted) | contact sheets, orbit view, frame strips, time-lapse, tracker table from the frame export |

Deleted: `Atmosphere.cs`, `Dynamics.cs`, `Spectral.cs`, `Physics.cs`, `EpochField.cs`, `EpochStudy.cs`, the old `Study.cs`, and the tests of water ledger, checkpoint, clone, epoch, spin-down, spectral transform.

31 tests green at header time (DMath accuracy, determinism across runs, threads, evaluation order, instances and chunkings, UT rewind and jump, finiteness on all sets, zero allocations, planted systems and a planted front found by the detectors, white noise and shuffled field find nothing,
waves off, rotation off, hand-over amplitude change, level scale, dust, window agreement, cell count by rotation rate, jet season, Stefan law, snow insulation and melt, freeze-up delay by the heat store, salinity, no ice without ocean, snow bounded, ice continuity at the anchor, dust bounded).

### H1 The generator model (one paragraph per part, enough for C25)

The generator is a pure function of (body, seed, latitude, longitude, altitude, UT). No state, no checkpoints, no epochs; the only caches are per thread and hold values that are themselves pure functions of the UT (the wave systems of the current UT). Time is the UT in seconds; "day" in this header is 86400 s; the Kerbin-like year is 9 203 545 s = 106.5 days, its rotation period is 21 600 s.

1. Climatological background. Temperature `T = T_ann(x) + (T(x, delta) - T_ann(x)) (1 + 0.15 land)` with `x = sin(lat)`, `T(x, d) = T_norm (w + (1 - w) I(x, sin d))^(1/4)`, `I = 1 - 0.477 P2(x) + 2 x sin d` (at least 0.03; the leading terms of the daily-mean insolation),
   `w = clamp(0.3 sqrt(p_s / 101325 Pa), 0.2, 0.95)` the share spread by heat transport, `T_norm` fixed so that the area mean of the declination-zero profile is the body's `SurfaceMeanK` (stand-in for the B09 temperature samples; they replace it when they exist).
   The declination is `d(t) = -obliquity cos(2 pi t / year)` (season phase 0 = northern midwinter, as `SeasonClock`); the sea seasons lag the land by 8 % of the year. The land share is the moisture source smoothed over 30 degrees of arc (a coast line is not a front).
   Diurnal term `A_d (1 - 0.7 ocean share) (1 - 0.5 cover) cos(lat) cos(d) cos(h - 0.5)` from the sun hour angle `h` (sub-solar longitude `-(Omega - 2 pi / year) t`). Altitude: lapse rate to the tropopause, the drop limited to a quarter of the surface temperature.
   Humidity proxy `H in 0..1 = h_T (0.04 + 0.96 moist) (1 + 1.2 belt) (1 - 0.55 subtropic)` with `h_T = 1 / (1 + exp(-0.07 (T - T_mean)))`, `belt = exp(-((lat - lat_itcz) / 0.2 rad)^2)` the moist belt (ITCZ) at `lat_itcz = 0.55 d_lag` (the declination 5 % of a year earlier), `subtropic` the dry belt at the Hadley edge. The ocean fraction enters only through `moist`: ocean fraction 0 gives H of at most 0.04 h_T.
1b. General circulation (closed form, `BodyDerived`). Rhines rule: the number of jets from equator to pole `N_J = 0.5 sqrt(2 Omega a / U_ref)` with `U_ref = 25 m/s` (the Earth gives 3.05). Cells per hemisphere: three for `N_J >= 1.5` (Hadley edge 30, Ferrel edge 60 degrees, jet 48), two for `N_J >= 0.9` (Hadley edge 40, jet 52),
   one below (broad cell, jet 55). Sub-solar regime weight `1 - SmoothStep(0.25, 0.7, N_J)` (1 for a tidally locked body): the surface flow then points to the sub-solar point (`6 m/s` times the weight) and the moist belt is centred there. Wave gate `SmoothStep(0.35, 1.2, N_J) (1 - sub-solar weight)`.
   Jet speed (the steering speed of the systems, `c = U_jet`): thermal wind `U_T = g H (dT / T) / (f45 0.4 a)` across the jet zone, saturated by the Rossby bound `U_jet = 0.1 Omega a tanh(0.4 U_T / (0.1 Omega a))` (zero at zero rotation). Deformation radius `Ld = N H / f45`
   with `N^2 = (g / T)(g / cp - lapse)` (at least 0.3 of the dry value); system radius `R = clamp(0.75 Ld, 0.12 a, 0.45 a)`. Surface winds: the zonal-mean trade, westerly and polar-easterly humps (amplitude `0.35 U_jet`, at least 0.5 m/s) with a meridional component of 0.35 of it, and the zonal-mean pressure structure
   (equatorial trough, subtropical highs, subpolar lows) as `ZonalPressureAnomaly`. Seasonal shifts: ITCZ `0.55 d_lag`, jet latitude and cell edges `0.22 d_lag` poleward in the summer hemisphere, jet speed `1 - 0.3 hemisphere d_lag / 0.4084` (stronger in winter), lag 5 % of the year.
2. Moving systems. Per hemisphere `n = clamp(round(2 pi a cos(48 deg) / (1.7 R)), 3, 8)` slots (seven on the Kerbin-like set). Slot `j` has a period `T_j` (hash, 3 to 6 days) and a phase offset; generation `n` of the slot is `hash(seed, hemisphere, j, n)` and lives on `[off + n T_j, off + (n + 1) T_j)` with the amplitude
   window `sin^2(pi u)`. The generation draws the longitude at mid-life, a latitude offset in the track, the wavelength `3 R (0.85..1.25)`, the cross-track width `1.25 R (0.8..1.2)`, the amplitude `0.025 p_s (0.6..1.2)` and the front amplitude `4.5 K (0.7..1.2)`
   (scaled by `T_mean / 288`). Its centre moves east with the local jet speed at mid-life: `lon_c(t) = lon_0 + c (t - t_mid) / (a cos lat_c)`. Pressure anomaly `p' = -A e cos(q)` with `q = k (x + 0.45 hemisphere y)` (`x`, `y` metres east and north of the centre, envelope `e` Gaussian in both),
   temperature anomaly `T' = B e_T tanh(1.6 sin q)` (a narrower envelope along the track, `0.45 lambda`, and a longer one across, `1.28` of the pressure envelope): warm ahead (east), cold behind, a sharp transition line (the FRONT) through the system whose width is set by the tanh; the front strength
   `F = |grad T'| = B e_T 1.6 k (1 - tanh^2) |cos q| sqrt(1 + 0.45^2)` in K per 100 km. The count per hemisphere stays constant (each slot always has a generation), amplitude changes at hand-over are zero by the window.
3. Uplift, clouds, rain. `U = clamp(0.85 max(0, -p' / (0.8 A_0)) + 0.5 F / F_ref, 0, 1.4)`. Cloud water `CW = clamp(0.55 H - 0.14, 0, 1) + H (1.1 U + 0.7 F / F_ref) + 0.7 cell`, at most 1.5; coverage `1 - exp(-3 CW)`.
   Rain `P = body factor (4 max(0, X - 0.12) + 12 max(0, cell - 0.15) / 0.85)` mm/h water equivalent with `X = H (1.1 U + 0.7 F / F_ref)`; the convective share and the type: wet-bulb temperature `T_w = T - freeze - 1.2 K`, snow fraction `1 / (1 + exp((T_w - 1) / 0.9))` (plan 09 section 3.5), rain fraction the rest, sleet fraction `4 f_s f_r`, the type is the largest.
4. Convective cells. Lattice rows of height `Lc = clamp(50 km sqrt(a / 6371 km), 12, 32 km)` (15.4 km on the Kerbin-like set), in each row `floor(2 pi cos(lat) / (Lc / a))` cells; the lattice drifts with the steering wind of its row (a table by latitude: `1.5` times the surface wind of the circulation plus `0.5` of the jet).
   Per 3-hour bucket every cell draws presence (`u < p_cell`), a life of 1 to 3 hours with a start inside the bucket (so it lives and ends inside the bucket: no popping at the bucket boundary), a jitter of `0.3 Lc` and a radius `0.3 Lc (0.75..1.25)`
   (diameter 6.9 to 11.5 km on the Kerbin-like set). Strength `sin^2(pi age / life) exp(-r^2 / radius^2) SmoothStep(0, 0.1, p_cell - u)`; the point evaluation checks the 3 x 3 neighbour cells. `p_cell = clamp(0.8 F/F_ref + 0.9 max(0, I - 0.35) + 0.3 U, 0, 0.95)` with
   instability `I = clamp(1.3 H + 0.3 warm sector + 0.25 sun heating - 0.3, 0, 1)` taken at the query point (a cell near a threshold of `p_cell` shows as a truncated blob, continuous by the SmoothStep). A present cell adds cloud water, rain, uplift, gusts and a towering profile (top `0.35 .. 1.0` of the tropopause height).
5. Wind. Geostrophic from the wave pressure field `u_g = -dp/dy / rho f / (f^2 + f_c^2)`, `v_g = dp/dx / rho f / (f^2 + f_c^2)` with `f_c = f(12 degrees)` (a smooth cap near the equator), soft saturation `v_sat tanh(v / v_sat)` with `v_sat = max(15 m/s, 2 U_jet)`, Ekman friction (factor 0.65, turning 25 degrees toward low pressure),
   plus the circulation wind above; with altitude the wind grows to 2.2 times at half the tropopause height and the jet is added above 0.4 of it. Gust factor `1 + 0.5 cell + 0.3 F / F_ref`.
6. Surface layer (`SurfaceLayer.cs`). Per site a window of 6-hour samples (temperature at the site height without the diurnal term, rain rate, wind, humidity) is cached; all of the following are functions of the UT only.
   Snow water equivalent: the recurrence over the last 40 days (160 steps) from a climatological base (up to 120 mm where the climatological temperature is far below freezing), `+ P f_snow` per step, melt `3 mm/(K day)` above freezing, sublimation `0.15 mm/day (1 + wind/8)`, blow-off above 12 m/s; the value at a time is the linear interpolation between the window results of the two surrounding steps (no jump when the window slides); depth = 8 times the water equivalent, at most 2000 mm w.e.
   Wet/dry state: the decaying sum of the last 16 steps of rain. Dust (dusty bodies on land): deposit 0.05 mm per dry calm step, lifting above the body's wind threshold on dry points (`H < 0.25`, no rain), bounded to 5 mm.
   Ice (sea texels, ocean fraction at least 0.5), daily steps: the mixed layer (depth `MixedLayerDepthM` 12 m, 4.186e6 J/m3/K) relaxes toward the air temperature with `tau = rho c D / 15 W/m2/K` (39 days); ice forms (10 mm) when `T_ml` reaches the freezing point of the body's liquid `T_f = 273.15 K - 0.054 K/psu x salinity` (271.26 K at 35 psu);
   Stefan growth `h^2 = h0^2 + 2 k_i / (rho_i L_f) integral (T_f - T_air) dt` (k_i 2.2 W/m/K, rho_i 917, L_f 3.34e5 J/kg: 1.241e-3 m2 per K day), reduced by a snow layer `1 / (1 + k_i h_s / (k_s h))`; melt `6 mm/(K day)` of positive degree-days plus a solar term `(1 - albedo) S dt / (rho_i L_f)` (albedo 0.5, snow covered 0.8, active within 3 K of freezing);
   under ice `T_ml = T_f`. History: integrated from the CLIMATOLOGICAL annual cycle (the same ice model run over five years on the climatological temperature and insolation of the site, giving `T_ml` and `h` at the four quarter-year boundaries) at the last quarter-year boundary, and again from the previous boundary;
   the two results are cross-faded with `SmoothStep(0, 0.5 quarter, t - boundary)`: at a boundary the old integration (from the previous boundary) is identical to the new second one, so the result is continuous. Spring ice thicker than autumn ice, the freeze-up and break-up lags and the ice edge emerge from the heat store and the Stefan law.
   Cost design: the cached window means one new sample per site and 6-hour step; the snow window is evaluated for the two surrounding steps and the last two are cached.

### H2 Parameter sets, seeds, data

| set | radius km | rotation s | year s | obliquity | p_s Pa | g | mean K | ocean target | notes |
|---|---|---|---|---|---|---|---|---|---|
| Kerbin-like (A seed 1, B seed 2, C seed 3) | 600 | 21 600 | 9 203 545 | 0.4084 | 101 325 | 9.81 | 288 | 0.7 | fast rotator with oceans |
| Duna-like | 320 | 65 517 | 17 315 400 | 0.44 | 6 000 | 2.94 | 215 | 0 | thin, cold, dry, dusty (threshold 10 m/s), body precipitation factor 0.15 |
| Eve-like | 700 | 80 500 | 5 657 995 | 0.05 | 506 625 | 16.7 | 340 | 0.7 | thick, hot, factor 2.0 |
| Laythe-like | 500 | 52 980 | 9 203 545 | 0.1 | 60 795 | 7.85 | 290 | 0.95 | ocean world, factor 0.8 |
| Slow-rotator | 600 | 864 000 | 9 203 545 | 0.05 | 101 325 | 9.81 | 288 | 0.3 | day-night circulation expected, diurnal swing 20 K |
| Tidally-locked | 600 | = year | 9 203 545 | 0 | 101 325 | 9.81 | 288 | 0.5 | sub-solar point fixed |
| No-ocean | as Kerbin-like | | | | | | | 0 | the ocean-off control |
| Cold-world | as Kerbin-like | | | | | | 258 | 0.7 | perennial ice expected |

Derived by `BodyDerived` (printed by `AtmoSim.Runner smoke <set>` at header time): Kerbin-like 3 cells, jet count 1.87, jet 17.2 m/s at 48 degrees, deformation radius 282 km, system radius 212 km, 7 slots, surface wind amplitude 6.0 m/s, equator-pole contrast 27.8 K;
Duna-like 1 cell, 0.78, 3.1 m/s, wave gate 0.51, 5 slots; Eve-like 2 cells, 1.05, 5.5 m/s, wave gate 0.91; Laythe-like 2 cells, 1.09, 5.9 m/s, wave gate 0.95; Slow-rotator 1 cell, 0.30, sub-solar weight 0.97, wave gate 0; Tidally-locked 0.09, sub-solar weight 1; No-ocean and Cold-world as Kerbin-like.
Earth-like (real Earth radius and rotation) exists as a scale sanity set and is not part of the gate.

Data used by every run of phase 2: synthetic continents of the seed (`MapGen.Continents`, 180 x 90 grid, ocean fraction target as above, smoothing 30 degrees), the stand-in climatology of H1 (no B09 samples), obliquity of the Kerbin-like set 23.4 degrees as a stand-in for KSP's missing tilt [VERIFY].
Every run states in its output that it used synthetic maps. `tests/data/recon/RECON.md` records the K-pre run as not performed (premises P1 temperature curves and P2 height and ocean grid UNKNOWN); the B09 dumps replace the stand-ins in a later re-run, and a run with them is a different run, reported separately.

### H3 Conventions of the runs

* Evaluation grid for the detectors, maps and cover: 256 x 128 (equirectangular, row 0 north). Map-update timing at 512 x 256. Sheets 256 x 128.
* Frames: 3 h of game time for the tracker runs (60 days = 480 frames), 6 h for the sheets (30 days). Seeds A, B, C on the Kerbin-like set, seed 1 on every other set. Start UT of a run 0 (northern midwinter) unless a criterion names its phases.
* Seasons of the Kerbin-like set: year phase 0 northern midwinter, 0.25 spring, 0.5 summer, 0.75 autumn; a seasonal value is the mean over 10 days centred on the phase. Cold season of a hemisphere: the quarter of the year centred on its midwinter (north phase 0, south phase 0.5).
* Absolute precipitation bands (water equivalent mm/h; Level01 anchors in brackets), times the body factor of H2: flurries from 0.05 [0.04], light from 0.25 [0.20], moderate from 1.0 [0.40], heavy from 3.0 [0.60], the top of the scale 8.0 [0.80] and 16.0 [1.00].
  Levels: none below 0.04 on the Level01 scale; Flurries `0.04 <= L < 0.2`; Light `0.2 <= L < 0.4`; Moderate `0.4 <= L < 0.6`; Heavy `L >= 0.6`; Blizzard: type snow, `L >= 0.8` (8 mm/h water equivalent times the body factor) and 10 m wind at least 15.6 m/s (the 35 mph of the plan 09 table).
  Per body (factor): Kerbin-like, Slow-rotator, Tidally-locked, No-ocean, Cold-world 1.0 (0.05 / 0.25 / 1.0 / 3.0 / 8.0 mm/h); Duna-like 0.15 (0.0075 / 0.0375 / 0.15 / 0.45 / 1.2); Eve-like 2.0 (0.1 / 0.5 / 2.0 / 6.0 / 16.0); Laythe-like 0.8 (0.04 / 0.2 / 0.8 / 2.4 / 6.4).
* The cloud/wind/level numbers of the criteria are area-weighted means over the grid unless a criterion says otherwise.

### H4 Detectors and tracker (frozen thresholds)

* Metric grid: cell sizes in metres, great-circle distances. Zonal means are removed row by row where a field is compared against its surroundings.
* D1, pressure minima: the wave pressure anomaly (`PressureAnomPa`) minus its zonal mean; box smoothing of radius half the ring radius (at least one cell); a minimum is a smoothed local minimum in the belt 35 to 70 degrees of either hemisphere whose ring mean (16 points at the ring radius `R_ring`) exceeds the minimum by at least `0.004 p_s`.
  `R_ring = max(3 cells, 0.5 R_system)` (106 km on the Kerbin-like set). Minima within `R_ring` of a deeper one count as one system.
* D2, front bands: in the belt 35 to 70 degrees a cell is flagged when the gradient of the zonal anomaly of the surface air temperature is at least 3 K per 100 km (thermal arm) or the gradient of the zonal anomaly of the cloud water is at least `0.6 p95(cloud water)` per 100 km (cloud arm).
  Connected flagged cells (8-neighbourhood) form a band when they number at least 6, their largest great-circle extent is at least `20 cells x equatorial cell size` (294 km at 256 x 128 on the Kerbin-like set), the mean width (area over extent) is at most 200 km, and the aspect ratio of the second moments is at least 3.
* Tracker: greedy nearest neighbour per hemisphere, one pass per frame, radius `1.5 U_jet x frame` (at least 3 m/s), at most one missing frame in a track, a track counts as persistent when it spans at least 1 day; swaps (a detection closer to another track's last position) are counted and reported.
  Lifetime = span first to last frame; east drift = the sum of the per-step east displacements over the span (a track may circle the body); straightness = unwrapped net displacement over path length. Tracks that touch the first or last 3 days of a run are excluded from lifetime statistics.
* D3 negative controls: the real field spatially shuffled with one permutation, and white noise with the field's mean and standard deviation, through the same detectors and trackers.

### H5 Criteria and thresholds (phase 2 measures all of them; GATE = counts for GO; REPORT = shown with its expectation, judged by the reviewer, does not block GO)

1. COHERENT SYSTEMS (GATE), Kerbin-like seed A, 60 days, 3 h frames, frames of days 3 to 57: mean number of simultaneously alive persistent D1 tracks per hemisphere at least 3.0 in each hemisphere; median lifetime of persistent tracks at least 2.0 days;
   median east drift of the tracks within 30 % of the mean jet speed of their hemisphere over the run (`JetSpeed(hemisphere, ut)` averaged every 6 h); track straightness median reported; popping: the largest change of one slot's amplitude within one game hour, within 2 h of any hand-over in 60 days, at most 0.5 % of the largest possible amplitude
   (`SlotAmplitude`; the unit test measures it). Seeds B and C are run and reported.
2. FRONTS (GATE), same run: at least one persistent (at least 1 day) D2 event per 10 days in each hemisphere (events counted per 10 days of 86400 s; the per 10 body days number is reported); moving with its system: at least 80 % of the persistent front tracks have a D1 track of the same hemisphere within `1.5 R_system` for at least half of their frames,
   and their median east drift lies within 50 % of the jet speed.
3. CLIMATE ZONES (REPORT), per set, zonal means over one year (24 samples), expectations: Kerbin-like: the latitude of the zonal-mean precipitation maximum within 15 degrees of the equator, the 20-35 degree mean at most 0.6 of the 0-10 degree mean, the polar (above 75 degrees) mean at most 0.5 of the 40-60 degree mean; No-ocean: global mean cloud cover at most 0.05 and mean precipitation at most 5 % of the Kerbin-like set;
   Slow-rotator and Tidally-locked: precipitation correlated with the cosine of the angle to the sub-solar point (at least 0.3) and the share of precipitation variance explained by latitude below that of the Kerbin-like set; dust: no dust flag where the wind is below 0.8 of the threshold, on any set (also a unit test).
4. LEVEL SHARES (GATE), Kerbin-like seeds A, B, C, sites = grid cells with 45 to 70 degrees of latitude in the hemisphere's cold season, one sample every 6 h: precipitating share (rate at least 0.05 mm/h) between 20 % and 40 %; share(Light) > share(Moderate) > share(Heavy) > share(Blizzard); Heavy (level Heavy, blizzard excluded) below 5 %; Blizzard below 1.5 %.
   Heavy-or-above and Flurries are reported. A seed fails the criterion if any of these fails; the criterion passes if seed A passes and seeds B and C pass the share bounds (20-40 %, heavy, blizzard).
5. CIRCULATION AND WIND (REPORT with stated ranges). Cells: Kerbin-like 3, Duna-like 1, Eve-like 2, Laythe-like 2, Slow-rotator and Tidally-locked in the sub-solar regime (weight at least 0.9); a rotation sweep (periods 5400, 21 600, 43 200, 86 164, 200 000, 864 000 s on the Kerbin-like body) has non-increasing cell counts.
   Kerbin-like zonal-mean 10 m zonal wind over one year: 5-25 degrees between -10 and -1.5 m/s (trades), 40-55 degrees between +1.5 and +10 (westerlies), 70-85 degrees between -6 and -0.3 (polar easterlies); jet latitude (maximum of the zonal-mean wind at 0.8 of the tropopause height) between 40 and 56 degrees in each hemisphere, steering jet speed between 10 and 30 m/s;
   seasonal shifts over a year: ITCZ latitude amplitude between 4 and 15 degrees, jet latitude amplitude between 2 and 8 degrees, lag behind the sub-solar latitude between 0.02 and 0.12 of the year; jet speed larger in the winter hemisphere than in the summer hemisphere.
   10 m wind percentiles (area weighted, four seasons, 6-hourly): Kerbin-like P50 3 to 9, P95 8 to 20, P99 11 to 26 m/s; storm-track ocean cells (45-65 degrees) P99 14 to 30 m/s; Duna-like P99 8 to 30; Eve-like P99 2 to 15; Laythe-like P99 3 to 20; Slow-rotator and Tidally-locked P99 3 to 15; the blizzard condition (snow, level 0.8 or more, wind 15.6 m/s) reachable (at least one sample in the criterion 4 sites on the Kerbin-like seeds) and rare (below 1.5 %).
6. COST (GATE), measured under the exclusive lock on this machine, median of at least 5 repeats: `Sample` median at most 3 microseconds with 0 allocations (pattern: random positions, the UT constant over each batch of 256 calls, as maps and windows evaluate; the pattern with a new UT on every call is reported);
   512 x 256 map update at most 300 ms on one thread; 32 x 32 x 24 window at most 5 ms (one point evaluation per column, the vertical profile of the column for the levels); 24 hourly forecast steps of one site at most 0.1 ms.
7. DETERMINISM (GATE): identical result hash of 20 000 random (position, altitude, UT) queries across runs, four threads, a shuffled evaluation order and a second generator instance; identical map hashes under different row chunkings; another seed differs. The same under Mono is a C25 row in the WeatherPlayer (not run here).
8. UT REWIND AND JUMP (GATE): evaluation at UT - 2 days equals the direct evaluation bit for bit after a jump forward by 10 days and back, for 20 000 queries.
9. SMOOTHNESS (REPORT): 99th percentile of the change per game second of coverage and of Level01 over all texels of a 512 x 256 map, from maps 60 s apart at 24 times of a year; provisional limit 2e-3 per game second (C23's number replaces it when it is smaller); continuous in UT, so no time interpolation.
10. SURFACE LAYER (GATE): (a) Stefan law unit test within 2 %; (b) sea-ice edge: in each hemisphere's midwinter 10-day mean, the lowest latitude at which at least half of the sea texels of a 2.5-degree row have an ice fraction of at least 0.15 lies between 50 and 60 degrees; (c) the same site series: ice thickness in spring (phase 0.25 north) at least 1.2 times that in autumn (phase 0.75) at a seasonally icy sea site,
   the delay of ice formation after the daily climatological air temperature falls through `T_f` and the delay of ice loss after it rises through `T_f` both at least 1 day and their ratio (break-up over freeze-up) between 0.3 and 3.0; (d) no ice with ocean fraction 0; none at sea texels within 15 degrees of the equator; perennial ice (thickness above 0.1 m all year) at sea texels above 75 degrees on the Cold-world set; ice at 35 psu never thicker than at 0 psu at the same site and time, and the freezing point 1.89 K lower;
   (e) melt rate equals the melt factor times the excess temperature within 5 %, the solar term adds melt, a snow cover reduces growth; (f) continuity: the largest thickness change per 10 minutes across the quarter-year boundaries, over three years at five sites, at most 0.03 m; (g) cost: one sea site at most 20 microseconds with the 6-hour window cached (a new window step is reported), one 256 x 128 cover map update at most 150 ms with the window cached (the first update and the update including new window samples are reported).
   Snow line, Kerbin-like seeds A, B, C: land texels in 2.5-degree rows around the bands 0, 30, 45, 55, 70, 85 degrees north, cover = share with at least 2.5 mm w.e. (2 cm of depth): 0 degrees at most 0.01 in all four seasons; 30 degrees at most 0.10 in all seasons; 45 degrees at least 0.30 in winter and at most 0.05 in summer; 55 degrees above 0.6 in winter and below 0.05 in summer;
   70 degrees at least 0.9 in winter; 85 degrees at least 0.95 in winter (the polar cap); a band is evaluated on every seed with at least 20 land texels in it, passes when all of them pass, and a band with no evaluated seed is reported NOT EVALUATED and counts as FAIL. Dust deposit appears only on dry points of dusty bodies and lifts only above the wind threshold (Duna-like: deposit zero after a rainy step, none on the Kerbin-like set);
   wet state: after 3 hours of at least 0.5 mm/h the wet value is at least 0.5 within 6 hours, and at most 0.05 after 5 dry days; snow stays between 0 and 2000 mm w.e. over 5 simulated years at 12 sites (both hemispheres, land and sea texels), no NaN.
11. CONVECTIVE CELLS (GATE), Kerbin-like seeds A, B, C, belt 35-70 degrees, 60 days: footprint diameter (twice the 1/e radius, a connected component of cell strength at least 0.37 on a 1 km raster of a 150 km patch) between 5 and 25 km; lifetime between 1 and 3 hours (cells followed at 10-minute steps in the lattice frame);
   density between 0.2 and 8 cells per 10 000 km2 of area where the instability is at least 0.5, and between 0.3 and 10 cells per 100 km of front length (cells within 50 km of a D2 band; length = band extents); share of cell centres within 100 km of a flagged front cell at least 60 % (the enrichment over uniformly placed points is reported).
12. PRE-SIGNING AND CAUSALITY (GATE): this header signed before the runs; negative control: the D3 controls find at most 0.1 persistent minima per hemisphere and D2 events at most 5 % of the real count and at most 0.1 per 10 days; A/B: seed A vs B give different results (maps differ, correlation of the coverage anomaly with the zonal mean removed below 0.3) and the same seed twice gives the identical hash;
    waves off: D2 events at most 0.1 per 10 days, no persistent D1 track, and the mean cell strength in the belt 35-70 degrees at most 20 % of that with waves; rotation period multiplied by 1000: wave gate 0, no active system, no persistent D1 track; ocean fraction 0 versus 0.7 (humidity proxy only): coverage at most 0.05.

Decision rule: GO when every GATE criterion passes; otherwise REWORK. If a GATE criterion fails, only the generator parameters of H6 may be tuned (at most three iterations, a full re-run of the failed criteria after each), then the best numbers and the open defect are reported. The thresholds, detector definitions and the header text do not change after the sign-off.

### H6 Parameters that may be tuned in phase 2 (and nothing else)

`BodyDerived`: system amplitude (0.025 p_s), front amplitude (4.5 K), slot spacing (1.7 R), the Rhines reference speed, the jet saturation constants; `WeatherGenerator` constants: the cloud and rain coefficients (0.55, 0.14, 1.1, 0.7, 0.12, 4.0 and 12.0 mm/h, cell rain threshold 0.15), the cell presence terms and radii,
the humidity terms, the lifetime range of generations (inside 2 to 6 days), the front envelope and sharpness, the wind factors; `SurfaceLayer` constants (melt factors, exchange coefficient, mixed-layer depth, anchors). Every change is listed in phase 2 with the iteration number.

### H7 Deliverables for the independent reviewer (paths in `MAINREPO\.local\work\atmosphere-sim\`, phase 2)

Cloud-map contact sheets (daily over one month, equirectangular and orthographic orbit views at three longitudes, Kerbin-like seed A), a time-lapse (6-hour steps), frame-strip sheets (6 h steps, 8 frames per sheet with D1 and D2 marks), a ground-level time series of level and wind at 45 N, 55 N, 70 N and the equator, a cross-section through a front and through a cell,
zonal-mean plots per set, the tracker table (count, lifetime, drift speed, straightness, swaps per hemisphere), `metrics.json` with the PASS/FAIL table and the detector settings, the A/B pairs of criterion 12 (seed A vs B, waves on/off, rotation on/off, ocean on/off), one map sheet each for the Slow-rotator, Duna-like and Eve-like sets, and the interaction rows (sets x seasons x seeds, evaluation order shuffled).
The reviewer receives the reference expectations (real world: mid-latitude weather is a procession of low-pressure systems moving east every few days with warm and cold fronts, cloud bands along the fronts, steady rain or snow behind the warm front, showers and cells along the cold front; the tropics show a moist belt with convective cells; the subtropics are dry; weather repeats seasonally but never identically), the view table, the sheets and the metrics, not this builder's interpretation.

### H8 Premise checks at header time

* A02 platform present (`tools/gpu_lock.py`, `tools/dotnet_test.py`). CPU work only; the lock is needed for the exclusive timing rows of criterion 6 only.
* `src/Common/Weather/IWeatherField.cs` and `WeatherSample.cs` (A01) read: the generator outputs map to `Level01`, type fractions, wind, gust factor, temperature, cloud cover and optical depth (from cloud water); the production port is C25's.
* B09: no dumps exist (RECON.md: not run). Stand-in data are used as stated in H2; this makes no premise of this brief false.
* Precedent: the first prototype's finding that emergent dynamics do not produce coherent travelling systems on a 600 km planet is the reason for this design; nothing of its core is reused.

### H9 Exploratory runs made while building (before this header; disclosed so the reviewer can judge)

Short health runs on the generator were made to find defects, not to measure criteria: `smoke` (ranges of the outputs per set, 40 days on a coarse grid), `zonal` (10-degree band means on the Kerbin-like set, with and without waves and cells), a 30-day `tracker` run on the Kerbin-like set seed 1 with the detectors,
`frontdebug` (the gradient of one frame), a frame export for the sheet script, and the `cost` command (non-exclusive, noisy). The following changes were made after those runs; none changes a threshold of H5, they are design and detector corrections:

* the cloud and rain coefficients were set to a plausible global precipitation (they gave 40-50 % of the globe raining; stratiform threshold 0.06 to 0.12, rate 10 to 4 mm/h, cell rain threshold 0.15);
* the heat transport share `0.5 sqrt(p/p0)` was lowered to `0.3 sqrt(p/p0)` (at least 0.2) so that the poles are colder; geostrophic wind got the soft saturation (it gave 70 to 150 m/s); the lapse drop is limited to a quarter of the surface temperature;
* the land/sea contrast of the temperature uses the 30-degree smoothed ocean share and a land factor of 0.15 (a coast line made the temperature gradient 50 to 90 K per 100 km, so that every coast was a front);
* the front temperature envelope was narrowed along the track and lengthened across (the network of fronts of overlapping systems formed one connected blob of flagged cells); slot spacing 2.2 R to 1.7 R and system amplitude 0.02 to 0.025 p_s (the count of persistent minima was 1.4 per hemisphere at that time);
* detectors: D1 smoothing by a box of half the ring radius (white noise gave 27 persistent minima per hemisphere before it); D2 on zonal anomalies, the mean-width limit of 200 km (a blob of flagged cells passed as a band), the cloud arm threshold from 0.25 to 0.6 of the 95th percentile, at most one missing frame in D2 tracks (was 0);
  the tracker drift is the unwrapped east displacement (a system circles the 2424 km circle of the 50 degree parallel about 1.8 times in 69 hours; the endpoint difference gave -2.5 m/s for a planted 17 m/s);
* the `Window` evaluates one point per column and takes the vertical profile of the column (22 ms for 32 x 32 x 24 by full point evaluation, 1.0 ms by columns); `Exp` takes the bit pattern of 2^n (the `Sample` median fell from 3.5 to 2.9 microseconds in the non-exclusive run).

At the state of the last exploratory run the generator did not yet meet criteria 1 and 2 on seed 1 (about 1.9 and 2.0 persistent minima per hemisphere, median lifetime 42-45 h, 7 front events in 30 days in the north and none in the south); this is stated here, not tuned against, and is the first thing phase 2 measures properly.

### H10 Open points of the header [VERIFY]

* The criterion 9 limit is provisional until C23's number is known. Criterion 5 and 3 are REPORT items as the brief says; their ranges are plausibility ranges set from the real Earth scaled to this planet and from the owner's expectation of "three cells for fast rotators", not measurements of KSP.
* Time scales are SI days. A game day on the Kerbin-like set is 6 hours; a system (3-6 SI days) lives 12-24 Kerbin days. If the owner wants systems that live a few Kerbin days, the lifetime range of H6 is the knob; the criteria in days would then be restated by the header's author before a new sign-off.
* The tilt of the Kerbin-like set (23.4 degrees) is an input of the insolation only; KSP has no axial tilt.

### Sign-off

Reviewer: (to be filled by the independent reviewer)  date:  verdict:
Core commit at header time: (the commit that carries this file)

---------------------------------------------------------------------------------------------------------------------------

## PHASE 2 (after the sign-off)

Not started. Run order: `cost` under the exclusive lock, `determinism`, `tracker` (seeds A, B, C; waves off; rotation x1000), the level, wind, snow, ice, cell and smoothness runs, the sheets, `evaluate`.

---------------------------------------------------------------------------------------------------------------------------

## HISTORY: the first prototype (two-layer spectral model), kept for the record

Decision of that prototype: FALLBACK. A two-layer quasi-geostrophic core (spherical harmonics T21, a moisture scheme, a surface layer, epoch re-anchoring with crossfade) was built and run on seven sets and 10 simulated Kerbin years. It passed cost, determinism and rewind, smoothness and forecast, and failed stability (NaN at the first step
for the Duna-like body and the slow rotator), emergence (no persistent pressure minimum in either hemisphere, with and without the one allowed structural retry `EddyScale` 0.5), climate zones, absolute statistics (blizzard class empty), surface wind (P50 1.45 m/s), the surface-layer continuity across epochs and the negative controls (white noise found 0.41 persistent
minima per hemisphere). Finding: the two-layer core is baroclinically stable on this planet at the physical deformation radius (shear about 10 m/s against about 48 m/s), so travelling cyclones did not appear; the front detector found quasi-stationary gradient bands (east drift 0.01 m/s), not moving systems.
The owner ruling after it: the simulation is far too detailed; the seen result matters. This package is the replacement.
