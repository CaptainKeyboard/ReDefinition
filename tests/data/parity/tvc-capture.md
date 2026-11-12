# TVC capture list (package T11, run once by T09's series)

TVC is measured once, in one integrated game start, by the same scripts as the reference and our pass (`tools/weather_metrics/parity_checks.py`).
Its figures fill the TVC column of the result table; they are never the pass or fail truth. This file lists the views and settings.

## Setup

- Installed build: True Volumetric Clouds 3.2.2.0 (`EVEManager.dll`) with the pack `GameData/StockVolumetricClouds`, Scatterer present, Deferred present.
- Graphics profile Medium on the A4500 (the cost reference of tvc-parity Q11), 1707x960 and 2560x1440, TUFX auto exposure unless a view says otherwise.
- `cloudSource` = Volumetric for the whole start; our own pass is off. Nothing of the pack is copied: the capture folders live under `tests/data/parity/tvc/` only as numbers and frames the series writes.
- One start, variants through `SeriesProbe variants[]`; the series is T09's (`tests/series/rows/T09.json`); this package ships the rows of the harness itself (`tests/series/rows/T11.json`).
- Capture format: one folder per check id (`tests/data/parity/tvc/<check id>/`) with `meta.json` and `.npy` arrays, as `parity_checks.load_capture` reads them. A view writes only the keys its check function names.

TVC cannot be fed our synthetic density fields. Its columns are measured on its own Kerbin layers at views that isolate the same behaviour, with the fixed UT, sun and camera of the table below. A check whose behaviour has no such view has `tvc: none` in `checks.json`.
Cloud alpha is not an output of TVC: where a check needs alpha, the view is captured twice (TVC layers on and off, same camera, same UT) and the alpha is the normalised difference of the two frames; the figure is marked approximate in the table.

## Views

| View id | Used by | Camera and settings | What is written |
|---|---|---|---|
| TV-DECK-2M | R01-horizon-below | Kerbin, camera 2 m above the ground at the KSC, looking north at 0 deg pitch; layer `Kerbin-base-layer` at coverage 1 (config switch), sun at 38 deg elevation | `alpha` (3 columns around the image centre, 480 rows), `camera` (radius 600000, h_cam, the layer's base/top/sigma as measured from the config's type table) |
| TV-DECK-2KM | R01-horizon-inside | Camera at 2000 m inside the base layer, same layer, pitch 0 | as above |
| TV-DECK-8KM | R01-horizon-above | Camera at 8000 m above the base layer, pitch -8 deg | as above |
| TV-NOISE-STRIPE | R02-noise-spectrum, R02-noise-repetition | Camera at 15 km looking straight down over the base layer at coverage 1, stripes of 40 x 20 km sampled from the on/off difference | `alpha_stripes`, `dx`, `tau_map`, `tile_lag` (the layer's noise tile distance) |
| TV-TWOSLAB | R05-stack-alpha, R05-boundary-crossing | Base layer and cumulonimbus layer both on, camera at 2 m looking up at 40 deg; then a vertical path through the base layer's lower boundary at 20 m/s | `alpha1`, `alpha2`, `alpha12`; `luminance` per frame |
| TV-CELL-SILVER | R06-silver-lining | One isolated cumulus of the base layer at 8 km, sun 8 deg off the view axis, then 75 deg off, same elevation | `edge_near_sun`, `edge_far_sun` |
| TV-CELL-SIDE | R08-base-top | The same cell from the side, sun 50 deg elevation, 90 deg azimuth | `base`, `top` |
| TV-CELL-FRONT | R09-powder | The same cell, sun behind the camera, 35 deg elevation | `edge_j`, `interior_j`, `ref_dip` |
| TV-SUNSTEP | R10-cache-direct, R10-cache-ambient | Fixed camera, UT stepped so that the sun moves 5 deg, 600 frames at 60 fps | `luminance` per frame of the cell region |
| TV-CLIMB | R10-cache-jump, R18-handover | Climb 0 to 260 km at 1 km per 20 frames over the same lat/lon | `altitude_m`, `luminance` per frame |
| TV-SHADOW-45 | R11-*, R25-rt-ratio | Plane terrain (KSC runway), a solid cloud slab at 2000 m (base layer coverage 1), sun at 45 deg | `shift_m`, `height_m`, `elevation_deg`, `x_m`, `profile`, `shadow`, `lit`, `tau_sun` |
| TV-SHADOW-TAU | R12-shadow-vs-tau | The same scene at four optical depths (layer density scale 0.1, 1, 5, 20) | `taus`, `factors` |
| TV-OCEAN | R13-ocean-land | A shadow edge crossing the shore | `water`, `land` |
| TV-MAPVIEW | R13-disc-shadow | Map view, a layer's disc shadow over the home continent | `measured_latlon`, `true_latlon` |
| TV-STATIC | R14-convergence | Static camera 600 frames at render scale 1.0 | `error_to_reference`, `cycle_frames` (TVC: 12) |
| TV-TURN | R14-ghost, R16-fg-duplicates | The C21 turn runs (2 then 12 deg per frame), DLSS K/M/L, FSR 3, XeSS at render scale 0.667 | `ghost_move.<upscaler>`, `trail_px.<upscaler>`, `baseline`, `duplicates` |
| TV-COST | R15-cost | `-redefinitionGpuProfile` exclusive runs at the six reference views | `ms` per view |
| TV-MOUNTAIN | R17-depth-edge | A mountain silhouette in front of the base layer | `cloud_edge_x`, `depth_edge_x` |
| TV-RAIN | R19-* | `Kerbin-Weather-1` rain, vessel at 0 and 60 m/s, wind set | `angle_deg`, `fall_speed`, `wind`, `vessel`; `coverage`, `density` |
| TV-IVA-RAIN | R20-* | IVA in rain, then dry air | `measured_s`, `config_s` |
| TV-WET | R21-* | Rain for 10 minutes, then dry; then a 6 h warp jump | `wet_over_dry`, `wet_diffuse`; `jump`, `stepwise` |
| TV-LIGHTNING | R22-* | `Kerbin-Weather-1` thunder type, a bolt at a known distance, warp 1x and 5x | `distance_m`, `delay_s`; `flash_lum`, `base_lum`; `bolts_above_warp`, `bolts_below_coverage` |
| TV-AERIAL | R23-aerial | The base layer's deck at 10, 30 and 60 km, Scatterer on and off | `d_m`, `luminance`, `l_near`, `l_sky`, `visual_range_m` |
| TV-EXPOSURE | R24-exposure | TUFX fixed and auto exposure on the same view | `ratio_fixed`, `ratio_auto` |

The reference column has no capture list: `python tools/weather_metrics/parity_run.py reference <folder>` writes it from the renderer.
