# C21 volume pass: player results (A4500, WeatherPlayer, Medium tier, 2026-11-11)

Commands (worktree `atmo/C21fix2`, through `tools/build_weather_player.py --series <name>`; the cost series is an exclusive timing run):

    C21-cost   C21-scene   C21-turn-a   C21-turn-b   (b alone fails the K/M/L/FSR rows because they are in a)

## Cost (ms, mean of 150 frames; frame totals are medians)

| view | res | steps | 1707x960 march+composite | 2560x1440 march+composite |
|---|---|---|---|---|
| side 8 km | 0.5 | 16 | 0.186 | 0.365 |
| side 8 km | 0.5 | 32 | 0.456 | 0.661 |
| side 8 km | 0.5 | 64 | 0.666 | 1.380 |
| side 8 km | 1.0 | 32 | 1.115 | 2.379 |
| side 8 km | 0.25 | 32 | 0.146 | 0.259 |
| inside cloud | 0.5 | 16 / 32 / 64 | 0.140 / 0.241 / 0.548 | |
| inside cloud | 1.0 | 32 | 0.767 | |
| far 25 km | 0.5 | 16 / 32 / 64 | 0.232 / 0.459 / 0.883 | |
| far 25 km | 1.0 | 32 | 1.549 | |

Rows: `cost_half_32_1707x960_ms` 0.459 (limit 0.7), `cost_half_32_2560x1440_ms` 0.931 (limit 1.3), full resolution 32 steps 1.549 at 1707x960 (above 1.5: rejected).
The earlier builder figure of 0.61 to 0.66 ms at 1707x960 was not reproduced by this run; the limit follows the reviewer's derivation (spike worst view 0.62 ms plus 15 % = 0.7).

## Curtain inside the cell, 1707x960, 600 frames per configuration

Method (a): the curtain is drawn by a command buffer (`CurtainPass.Record`) between two timestamp marks, so that the segment encloses its draws; the run fails if the smallest segment is zero.
Method (b): whole-frame GPU time with the curtain on and off at the same pose, difference of the medians and of the 95th percentiles.

| steps | (a) segment median ms | smallest segment ms | (b) median difference ms | (b) p95 difference ms |
|---|---|---|---|---|
| 4 | 0.118 | 0.115 | -0.002 | 0.012 |
| 8 | 0.146 | 0.142 | -0.004 | 0.016 |
| 16 | 0.259 | 0.221 | -0.007 | 0.122 |

`cost_curtain_inside_ms` = 0.259 at 16 steps (limit 0.7). The frame-difference method resolves nothing below the frame's own noise at this scene size (frame median 0.8 ms); the segment is the number.

## Temporal (render scale 0.667, turn 2 then 12 deg per frame, far view)

| upscaler | ghost_move | trail_px | shimmer | ghost_still |
|---|---|---|---|---|
| DLSS K | 0.00245 | 0.0065 | 0.00027 | 0.0023 |
| DLSS M | 0.00668 | 0.0082 | 0.00121 | 0.0079 |
| DLSS L | 0.00807 | 0.0085 | 0.00103 | 0.0104 |
| FSR 3 | 0.00348 | 0.0103 | 0.00027 | 0.0042 |
| XeSS | 0.00305 | 0.0071 | 0.00114 | 0.0032 |
| native | 0.00366 | 0.0113 | 0.00003 | |

mv_match 0.9994 (limit 0.98). A/B at DLSS K / FSR 3 (ghost_move, trail_px, shimmer):

| variant | DLSS K | FSR 3 |
|---|---|---|
| analytic motion on | 0.00245 / 0.0065 / 0.00027 | 0.00348 / 0.0103 / 0.00027 |
| analytic motion off | 0.00238 / 0.0064 / 0.00027 | 0.00340 / 0.0098 / 0.00027 |
| fixed IGN (no jitter key) | 0.00276 / 0.0077 / 0.00023 | 0.00349 / 0.0103 / 0.00025 |
| pass off | 0.00165 / 0.0063 / 0.00020 | 0.00246 / 0.0091 / 0.00016 |

## Scene (views, A/B, half-resolution A/B)

Look numbers (default tuning): luma_ratio V1a 0.905, V1b 0.932, V2 0.955, V3 1.449, V4 1.363, V5 1.424, V6 low 0.939, V6 noon 0.933; base_darkness V1a 0.177, V1b 0.166, V6 low 0.160, V6 noon 0.180; edge_contrast V1a 0.098, V1b -0.036, V6 low -0.039, V6 noon -0.038. contrib_overlap 0.984 to 1.0 on every expected view (limit 0.8). window_deterministic 1, core radius 7.5 km (5 to 25 km), 2 cores, resample 15.7 ms spread over the tier's frames (0.65 ms per frame), allocations in Prepare 0 bytes per frame.

Half-resolution A/B at the 25 km view against full size:

| variant | luma delta | edge ratio | structure error | mean abs difference |
|---|---|---|---|---|
| half | 0.00002 | 0.9986 | 0.905 | 0.0157 |
| quarter | 0.00017 | 0.9985 | 0.916 | 0.0166 |
| vertical half | 0.00002 | 0.9988 | 1.023 | 0.0172 |

The structure error of 0.9 to 1.0 says the high-pass content differs from full size; `volumeResolution` stays 1.0 until E40 judges the look in the full stack.

## Frame generation replay

Duplicates per interpolated frame for DLSS-G, FSR-FG and XeSS-FG: not measured. The rows `fg_duplicates.*` in `tests/series/rows/C21.json` are optional; the measurement needs a recorded dump of the volume scene replayed with `tools/replay_fg.ps1`.
