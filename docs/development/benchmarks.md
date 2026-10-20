# Benchmarks of upscalers, frame generation, raytracing and latency

**For:** anyone who changes the upscaler, frame generation, the raytracing or the proxy's
latency code and wants to know what the change did to the frame rate and the latency.
**You need:** KSP with ReDefinition and the proxy installed, the save the scene probe flies
(`ReDefinitionProbe`), Python 3, and the repository open.
**You get:** the comparison table of the latest run, how to run it again with one command,
and what each column and each run's record means.

## Run the comparison

Close KSP first, then run this from the repository root:

```
python tools/benchmark_matrix.py run
```

The tool reads the scenario matrix `tools/benchmark_matrix.json`. It copies the install's
settings files and applies the graphics profile in one KSP start. Then it runs one KSP
start per frame generation kind. After the last start it puts every settings file back and
removes the settings files the run made, also when a start failed. It appends the run to
`docs/development/benchmarks/` as a JSON file and writes the table on this page again.

The default matrix needs two KSP starts on an NVIDIA GPU, with 124 phases, and takes about
30 minutes. To see the starts and phases first, without running KSP, run this:

```
python tools/benchmark_matrix.py plan
```

These options change what the tool uses:

| Option | Default | What it is |
|---|---|---|
| `--ksp` | Steam's KSP folder, or `REDEFINITION_KSP` | the KSP install |
| `--settings-template` | the install's own `settings.cfg`, as it was before the run | the mod's settings the profile start begins from |
| `--dlssg-package` | `.local/dlssg_x`, or `REDEFINITION_DLSSG_PACKAGE` | the unpacked DLSS-G package for RTX 20 and 30 GPUs |
| `--amd-dll-dir` | `.local/amd`, or `REDEFINITION_AMD_DLL_DIR` | the folder with `amd_fidelityfx_upscaler_dx12.dll` |
| `--xess-dll-dir` | `.local/xess`, or `REDEFINITION_XESS_DLL_DIR` | the folder with Intel's `libxess.dll` |
| `--raw` | `.local/benchmarks`, or `REDEFINITION_BENCH_RAW` | where each start's logs and screenshots go |
| `--only` | every start | only the starts whose id contains this text |
| `--no-profile` | the profile is applied | skip the start that applies the profile |
| `--vendor` | the vendor Windows names | the GPU vendor that decides which upscalers and latency models run |

If a DLL or the DLSS-G package is not there, the tool skips what needs it. The run's
section names what it skipped. The tool hands the AMD DLL's folder to the proxy as
`amdUpscalerDirectory` and XeSS's as `xessDirectory`, so the DLLs stay where they are. It
copies the DLSS-G package next to `KSP_x64.exe` only for the start that needs it, and
removes it after that start.

To read a run's logs again, for example after a change to the parser, run this:

```
python tools/benchmark_matrix.py parse .local/benchmarks/<run>
```

To write this page again from the JSON files, run `python tools/benchmark_matrix.py render`.

## What a start measures

Each start flies the scene probe's vessel on the launch pad
(`-redefinitionProbeStay -redefinitionProbeClouds -redefinitionProbeShot`). The mod's
settings come from the `settings.cfg` the profile start left, with the matrix's keys set
over it. After 30 seconds in the flight, the probe applies each set of keys in turn and
holds it for 12 seconds (`-redefinitionProbeShotIni`):

* A `mod.<key>` key is one of ReDefinition's own settings, by its `settings.cfg` key. The
  probe applies it as the settings window does.
* Every other key goes into `ReDefinitionProxy.ini`. The proxy re-reads the file within a
  second and logs `Config reloaded:` with every value.

A phase is one of these sets. Its measurement window opens a while after the proxy's
reload and closes when the probe takes the phase's screenshot:

| Phase | Settling time | Length |
|---|---|---|
| The first with another upscaler, raytracing mode or Ray Reconstruction | 8 s (`settle_after_rebuild_seconds`) | 24 s (`phase_repeats_after_rebuild`) |
| Every other | 2 s (`settle_seconds`) | 12 s (`phase_repeats`) |

A change of the rendering builds the upscaler and the raytracing's scene again. In the
probe's logs that took about 5 seconds, with frames of half a second in between. Every set
holds `reportSeconds=1`, so a phase has 8 to 9 reports, and 14 to 15 after a change.

Only what the proxy reads at load needs a start of its own: the choice between DLSS-G
and FSR frame generation (`dlssFrameGeneration`). DLSS-G on RTX 20 and 30 GPUs also needs
the package next to `KSP_x64.exe`. The upscaler's DLL folders are read again when the
upscaler is set up, so each start sets them once.

Every set names every key the matrix varies, since the probe's sets add to each other:

| Phase setting | Keys the probe sets |
|---|---|
| The upscaler | `mod.technique`, for example `Dlss` |
| The raytracing mode | `mod.raytracing`, `Off` or `Full` |
| Ray Reconstruction | `mod.rayReconstruction`; `True` where it does not apply |
| Frame generation off | `fgMinimumFps=1000`, which holds it off |
| Frame generation on | `fgMinimumFps=0`, which never holds it off |
| No latency model | `lowLatency=0`, `latencyBrake=0`, `antiLag2=0` |
| Reflex | `lowLatency=1`, `latencyBrake=0` |
| Brake 4 ms | `lowLatency=0`, `latencyBrake=1`, `latencyBrakeTargetMs=4` |
| Reflex and brake 4 ms | `lowLatency=1`, `latencyBrake=1`, `latencyBrakeTargetMs=4` |
| Anti-Lag 2 | `antiLag2=12`, on an AMD GPU only |

"Frame generation off" holds frame generation off while its inputs are still captured. It
is not the same as `frameGeneration = False` in the mod's settings.

## Add a scenario

Each list in `tools/benchmark_matrix.json` multiplies the phases:

* `techniques`: one line per upscaler. `log_name` is the name the upscaler's
  `Upscaler running on` line in `KSP.log` gives, so the tool can say when another one ran.
  A technique with `ray_reconstruction` is measured with Ray Reconstruction on and off for
  each raytracing mode other than `Off`.
* `raytracing`: the raytracing modes.
* `frame_generation`: one entry per frame generation kind, each a start of its own.
* `latency`: the latency models. `vendors` limits one to those GPUs.
* `baseline`: the scenario every row is compared with.
* `separate_starts`: `technique`, `raytracing` or `ray_reconstruction`, each named one
  split into starts of its own. The list is empty by default.

XeSS, for example, is this line in `techniques`:

```
{"id": "xess", "name": "XeSS", "log_name": "XeSS (DLL)", "settings": {"technique": "Xess"}, "proxy_load": {"xessDirectory": "{xess_dll_dir}"}, "requires": ["xess_dll_dir"]}
```

## The comparison table

Each row is one scenario of the latest run: the upscaler, the raytracing mode with Ray
Reconstruction where it applies, the frame generation and the latency model.

| Column | What it is |
|---|---|
| fps | the frames KSP rendered a second in the window: all frames over all frame times |
| Shown fps | the frames the monitor showed a second, from DXGI's statistics; with frame generation the generated frames count |
| Latency median ms | from the frame's start to the GPU's end of its Present, the median of the reports' medians |
| Latency p90 ms | the same, the median of the reports' 90th percentiles |
| fps vs baseline | the fps against the baseline row, in percent |
| Latency vs baseline | the latency median against the baseline row, in ms |
| fps vs FG off, no model | the fps against the same rendering with frame generation off and no latency model |
| Latency vs FG off, no model | the latency median against that row, in ms |
| fps vs previous run | the fps against the same scenario in the run before |
| Notes | what did not run as asked: another upscaler, no generated frames, a key the proxy does not have |

The latency is the proxy's own measure, the same on every GPU. The proxy logs it as
`Latency:` every `reportSeconds`. It does not include the time from the GPU to the screen.

<!-- benchmark-table:start -->
Run `2026-10-20-1715`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 71.3 | 71.3 | 22.3 | 23.8 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG off · Reflex | 71.1 | 70.8 | 22.4 | 24.0 | -0.3 % | +0.1 ms | -0.3 % | +0.1 ms | – |  |
| FSR 3 · RT Off · FG off · brake 4 ms | 71.1 | 71.1 | 22.4 | 23.8 | -0.3 % | +0.1 ms | -0.3 % | +0.1 ms | – |  |
| FSR 3 · RT Off · FG off · Reflex + brake 4 ms | 70.7 | 70.4 | 22.5 | 24.3 | -0.8 % | +0.2 ms | -0.8 % | +0.2 ms | – |  |
| FSR 3 · RT Off · FG FSR · none | 61.1 | 122.2 | 37.4 | 39.4 | -14.3 % | +15.1 ms | -14.3 % | +15.1 ms | – |  |
| FSR 3 · RT Off · FG FSR · Reflex | 60.7 | 121.2 | 36.1 | 39.0 | -14.9 % | +13.8 ms | -14.9 % | +13.8 ms | – |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 59.7 | 119.2 | 31.3 | 36.7 | -16.3 % | +9.0 ms | -16.3 % | +9.0 ms | – |  |
| FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | 59.0 | 118.0 | 30.6 | 37.5 | -17.3 % | +8.3 ms | -17.3 % | +8.3 ms | – |  |
| FSR 3 · RT Full · FG off · none | 43.4 | 43.4 | 59.4 | 61.3 | -39.1 % | +37.1 ms | – | – | – |  |
| FSR 3 · RT Full · FG off · Reflex | 43.4 | 43.4 | 59.3 | 61.5 | -39.1 % | +37.0 ms | +0.0 % | -0.1 ms | – |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 43.3 | 43.1 | 49.8 | 56.0 | -39.3 % | +27.5 ms | -0.2 % | -9.6 ms | – |  |
| FSR 3 · RT Full · FG off · Reflex + brake 4 ms | 44.0 | 44.0 | 48.4 | 54.0 | -38.3 % | +26.1 ms | +1.4 % | -11.0 ms | – |  |
| FSR 3 · RT Full · FG FSR · none | 37.1 | 74.2 | 66.9 | 69.1 | -48.0 % | +44.6 ms | -14.5 % | +7.5 ms | – |  |
| FSR 3 · RT Full · FG FSR · Reflex | 37.2 | 74.2 | 67.1 | 69.3 | -47.8 % | +44.8 ms | -14.3 % | +7.7 ms | – |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 37.1 | 74.1 | 54.7 | 56.8 | -48.0 % | +32.4 ms | -14.5 % | -4.7 ms | – |  |
| FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | 37.2 | 74.2 | 55.1 | 57.4 | -47.8 % | +32.8 ms | -14.3 % | -4.3 ms | – |  |
| DLSS · RT Off · FG off · none | 71.1 | 71.1 | 24.5 | 26.3 | -0.3 % | +2.2 ms | – | – | – |  |
| DLSS · RT Off · FG off · Reflex | 72.5 | 72.2 | 24.6 | 26.2 | +1.7 % | +2.3 ms | +2.0 % | +0.1 ms | – |  |
| DLSS · RT Off · FG off · brake 4 ms | 72.3 | 72.2 | 24.6 | 26.4 | +1.4 % | +2.3 ms | +1.7 % | +0.1 ms | – |  |
| DLSS · RT Off · FG off · Reflex + brake 4 ms | 72.0 | 71.8 | 24.5 | 26.4 | +1.0 % | +2.2 ms | +1.3 % | +0.0 ms | – |  |
| DLSS · RT Off · FG FSR · none | 57.4 | 114.8 | 41.7 | 43.3 | -19.5 % | +19.4 ms | -19.3 % | +17.2 ms | – |  |
| DLSS · RT Off · FG FSR · Reflex | 56.9 | 113.8 | 41.4 | 43.2 | -20.2 % | +19.1 ms | -20.0 % | +16.9 ms | – |  |
| DLSS · RT Off · FG FSR · brake 4 ms | 56.8 | 113.7 | 35.0 | 40.4 | -20.3 % | +12.7 ms | -20.1 % | +10.5 ms | – |  |
| DLSS · RT Off · FG FSR · Reflex + brake 4 ms | 57.2 | 114.4 | 35.4 | 40.4 | -19.8 % | +13.1 ms | -19.5 % | +10.9 ms | – |  |
| DLSS · RT Full, RR on · FG off · none | 42.4 | 42.6 | 61.3 | 62.8 | -40.5 % | +39.0 ms | – | – | – |  |
| DLSS · RT Full, RR on · FG off · Reflex | 42.5 | 42.8 | 61.4 | 62.8 | -40.4 % | +39.1 ms | +0.2 % | +0.1 ms | – |  |
| DLSS · RT Full, RR on · FG off · brake 4 ms | 42.4 | 42.4 | 48.9 | 55.5 | -40.5 % | +26.6 ms | +0.0 % | -12.4 ms | – |  |
| DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | 42.5 | 42.6 | 48.8 | 56.0 | -40.4 % | +26.5 ms | +0.2 % | -12.5 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · none | 36.0 | 72.0 | 70.2 | 71.8 | -49.5 % | +47.9 ms | -15.1 % | +8.9 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · Reflex | 36.0 | 71.9 | 70.1 | 71.9 | -49.5 % | +47.8 ms | -15.1 % | +8.8 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · brake 4 ms | 36.1 | 72.1 | 58.8 | 61.5 | -49.4 % | +36.5 ms | -14.9 % | -2.5 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | 36.0 | 71.9 | 58.5 | 61.3 | -49.5 % | +36.2 ms | -15.1 % | -2.8 ms | – |  |
| DLSS · RT Full, RR off · FG off · none | 42.0 | 41.9 | 61.6 | 63.2 | -41.1 % | +39.3 ms | – | – | – |  |
| DLSS · RT Full, RR off · FG off · Reflex | 42.4 | 42.4 | 61.7 | 63.0 | -40.5 % | +39.4 ms | +1.0 % | +0.1 ms | – |  |
| DLSS · RT Full, RR off · FG off · brake 4 ms | 42.4 | 42.3 | 48.8 | 55.7 | -40.5 % | +26.5 ms | +1.0 % | -12.8 ms | – |  |
| DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | 42.4 | 42.3 | 49.3 | 56.0 | -40.5 % | +27.0 ms | +1.0 % | -12.3 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · none | 36.0 | 72.0 | 70.0 | 71.7 | -49.5 % | +47.7 ms | -14.3 % | +8.4 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · Reflex | 36.0 | 72.0 | 69.9 | 71.6 | -49.5 % | +47.6 ms | -14.3 % | +8.3 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · brake 4 ms | 36.0 | 72.0 | 58.9 | 61.1 | -49.5 % | +36.6 ms | -14.3 % | -2.7 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | 36.0 | 72.0 | 58.7 | 60.8 | -49.5 % | +36.4 ms | -14.3 % | -2.9 ms | – |  |
| AMD DLL · RT Off · FG off · none | 71.8 | 71.7 | 23.9 | 25.4 | +0.7 % | +1.6 ms | – | – | – |  |
| AMD DLL · RT Off · FG off · Reflex | 72.1 | 72.0 | 23.6 | 25.2 | +1.1 % | +1.3 ms | +0.4 % | -0.3 ms | – |  |
| AMD DLL · RT Off · FG off · brake 4 ms | 72.2 | 72.1 | 23.9 | 25.4 | +1.3 % | +1.6 ms | +0.6 % | +0.0 ms | – |  |
| AMD DLL · RT Off · FG off · Reflex + brake 4 ms | 70.0 | 70.0 | 23.8 | 25.2 | -1.8 % | +1.5 ms | -2.5 % | -0.1 ms | – |  |
| AMD DLL · RT Off · FG FSR · none | 58.2 | 116.5 | 40.5 | 42.5 | -18.4 % | +18.2 ms | -18.9 % | +16.6 ms | – |  |
| AMD DLL · RT Off · FG FSR · Reflex | 58.3 | 116.5 | 40.5 | 42.5 | -18.2 % | +18.2 ms | -18.8 % | +16.6 ms | – |  |
| AMD DLL · RT Off · FG FSR · brake 4 ms | 58.1 | 116.2 | 36.1 | 40.4 | -18.5 % | +13.8 ms | -19.1 % | +12.2 ms | – |  |
| AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | 57.6 | 115.2 | 34.7 | 40.4 | -19.2 % | +12.4 ms | -19.8 % | +10.8 ms | – |  |
| AMD DLL · RT Full · FG off · none | 42.3 | 42.3 | 61.5 | 62.9 | -40.7 % | +39.2 ms | – | – | – |  |
| AMD DLL · RT Full · FG off · Reflex | 42.4 | 42.4 | 61.5 | 63.0 | -40.5 % | +39.2 ms | +0.2 % | +0.0 ms | – |  |
| AMD DLL · RT Full · FG off · brake 4 ms | 42.3 | 42.0 | 49.6 | 56.5 | -40.7 % | +27.3 ms | +0.0 % | -11.9 ms | – |  |
| AMD DLL · RT Full · FG off · Reflex + brake 4 ms | 42.3 | 42.0 | 49.7 | 56.1 | -40.7 % | +27.4 ms | +0.0 % | -11.8 ms | – |  |
| AMD DLL · RT Full · FG FSR · none | 36.0 | 71.9 | 69.8 | 72.0 | -49.5 % | +47.5 ms | -14.9 % | +8.3 ms | – |  |
| AMD DLL · RT Full · FG FSR · Reflex | 36.0 | 71.9 | 70.1 | 71.9 | -49.5 % | +47.8 ms | -14.9 % | +8.6 ms | – |  |
| AMD DLL · RT Full · FG FSR · brake 4 ms | 36.0 | 72.0 | 58.9 | 61.1 | -49.5 % | +36.6 ms | -14.9 % | -2.6 ms | – |  |
| AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | 35.9 | 71.9 | 58.6 | 61.2 | -49.6 % | +36.3 ms | -15.1 % | -2.9 ms | – |  |
| XeSS · RT Off · FG off · none | 71.2 | 71.0 | 25.5 | 27.5 | -0.1 % | +3.2 ms | – | – | – |  |
| XeSS · RT Off · FG off · Reflex | 71.2 | 71.1 | 25.8 | 28.0 | -0.1 % | +3.5 ms | +0.0 % | +0.3 ms | – |  |
| XeSS · RT Off · FG off · brake 4 ms | 71.4 | 71.3 | 25.8 | 27.7 | +0.1 % | +3.5 ms | +0.3 % | +0.3 ms | – |  |
| XeSS · RT Off · FG off · Reflex + brake 4 ms | 71.6 | 71.4 | 25.4 | 27.1 | +0.4 % | +3.1 ms | +0.6 % | -0.1 ms | – |  |
| XeSS · RT Off · FG FSR · none | 56.2 | 112.3 | 42.6 | 44.2 | -21.2 % | +20.3 ms | -21.1 % | +17.1 ms | – |  |
| XeSS · RT Off · FG FSR · Reflex | 56.2 | 112.3 | 42.5 | 44.3 | -21.2 % | +20.2 ms | -21.1 % | +17.0 ms | – |  |
| XeSS · RT Off · FG FSR · brake 4 ms | 56.0 | 112.0 | 36.8 | 41.1 | -21.5 % | +14.5 ms | -21.3 % | +11.3 ms | – |  |
| XeSS · RT Off · FG FSR · Reflex + brake 4 ms | 56.1 | 112.1 | 36.2 | 41.4 | -21.3 % | +13.9 ms | -21.2 % | +10.7 ms | – |  |
| XeSS · RT Full · FG off · none | 40.9 | 40.9 | 63.9 | 65.4 | -42.6 % | +41.6 ms | – | – | – |  |
| XeSS · RT Full · FG off · Reflex | 41.0 | 41.0 | 64.0 | 65.4 | -42.5 % | +41.7 ms | +0.2 % | +0.1 ms | – |  |
| XeSS · RT Full · FG off · brake 4 ms | 40.9 | 41.0 | 50.1 | 57.8 | -42.6 % | +27.8 ms | +0.0 % | -13.8 ms | – |  |
| XeSS · RT Full · FG off · Reflex + brake 4 ms | 40.9 | 41.0 | 51.3 | 56.8 | -42.6 % | +29.0 ms | +0.0 % | -12.6 ms | – |  |
| XeSS · RT Full · FG FSR · none | 35.0 | 69.8 | 72.4 | 74.1 | -50.9 % | +50.1 ms | -14.4 % | +8.5 ms | – |  |
| XeSS · RT Full · FG FSR · Reflex | 34.9 | 69.8 | 72.3 | 74.4 | -51.1 % | +50.0 ms | -14.7 % | +8.4 ms | – |  |
| XeSS · RT Full · FG FSR · brake 4 ms | 34.9 | 69.8 | 61.2 | 63.8 | -51.1 % | +38.9 ms | -14.7 % | -2.7 ms | – |  |
| XeSS · RT Full · FG FSR · Reflex + brake 4 ms | 34.8 | 69.5 | 60.8 | 63.5 | -51.2 % | +38.5 ms | -14.9 % | -3.1 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.4 | 132.9 | – | – | -6.9 % | – | -6.9 % | – | – |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex | 66.3 | 132.8 | – | – | -7.0 % | – | -7.0 % | – | – |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 65.0 | 130.1 | – | – | -8.8 % | – | -8.8 % | – | – |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | 66.2 | 132.4 | – | – | -7.2 % | – | -7.2 % | – | – |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.1 | 80.5 | – | – | -43.8 % | – | -7.6 % | – | – |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex | 40.1 | 80.3 | – | – | -43.8 % | – | -7.6 % | – | – |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.0 | 80.1 | – | – | -43.9 % | – | -7.8 % | – | – |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | 39.9 | 79.9 | – | – | -44.0 % | – | -8.1 % | – | – |  |
| DLSS · RT Off · FG DLSS-G · none | 61.4 | 122.7 | – | – | -13.9 % | – | -13.6 % | – | – |  |
| DLSS · RT Off · FG DLSS-G · Reflex | 61.5 | 123.0 | – | – | -13.7 % | – | -13.5 % | – | – |  |
| DLSS · RT Off · FG DLSS-G · brake 4 ms | 61.3 | 122.8 | – | – | -14.0 % | – | -13.8 % | – | – |  |
| DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.2 | 122.6 | – | – | -14.2 % | – | -13.9 % | – | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · none | 32.0 | 63.8 | – | – | -55.1 % | – | -24.5 % | – | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex | 32.1 | 64.0 | – | – | -55.0 % | – | -24.3 % | – | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | 32.1 | 64.0 | – | – | -55.0 % | – | -24.3 % | – | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | 32.1 | 64.0 | – | – | -55.0 % | – | -24.3 % | – | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · none | 38.3 | 76.7 | – | – | -46.3 % | – | -8.8 % | – | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex | 38.4 | 77.0 | – | – | -46.1 % | – | -8.6 % | – | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | 38.4 | 77.0 | – | – | -46.1 % | – | -8.6 % | – | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | 38.4 | 77.0 | – | – | -46.1 % | – | -8.6 % | – | – |  |
| AMD DLL · RT Off · FG DLSS-G · none | 62.4 | 124.8 | – | – | -12.5 % | – | -13.1 % | – | – |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex | 62.2 | 124.4 | – | – | -12.8 % | – | -13.4 % | – | – |  |
| AMD DLL · RT Off · FG DLSS-G · brake 4 ms | 62.5 | 125.1 | – | – | -12.3 % | – | -13.0 % | – | – |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | 62.6 | 125.2 | – | – | -12.2 % | – | -12.8 % | – | – |  |
| AMD DLL · RT Full · FG DLSS-G · none | 38.7 | 77.1 | – | – | -45.7 % | – | -8.5 % | – | – |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex | 38.8 | 77.4 | – | – | -45.6 % | – | -8.3 % | – | – |  |
| AMD DLL · RT Full · FG DLSS-G · brake 4 ms | 38.8 | 77.4 | – | – | -45.6 % | – | -8.3 % | – | – |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.8 | 77.3 | – | – | -45.6 % | – | -8.3 % | – | – |  |
| XeSS · RT Off · FG DLSS-G · none | 60.4 | 120.7 | – | – | -15.3 % | – | -15.2 % | – | – |  |
| XeSS · RT Off · FG DLSS-G · Reflex | 60.5 | 121.0 | – | – | -15.1 % | – | -15.0 % | – | – |  |
| XeSS · RT Off · FG DLSS-G · brake 4 ms | 60.3 | 121.0 | – | – | -15.4 % | – | -15.3 % | – | – |  |
| XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 60.5 | 120.9 | – | – | -15.1 % | – | -15.0 % | – | – |  |
| XeSS · RT Full · FG DLSS-G · none | 37.8 | 75.6 | – | – | -47.0 % | – | -7.6 % | – | – |  |
| XeSS · RT Full · FG DLSS-G · Reflex | 37.9 | 75.8 | – | – | -46.8 % | – | -7.3 % | – | – |  |
| XeSS · RT Full · FG DLSS-G · brake 4 ms | 37.9 | 75.7 | – | – | -46.8 % | – | -7.3 % | – | – |  |
| XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | 37.9 | 75.7 | – | – | -46.8 % | – | -7.3 % | – | – |  |
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's settings in effect, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
### Run 2026-10-20-1715

| What | Value |
|---|---|
| Date | 2026-10-20T17:15:10 to 2026-10-20T17:45:08 |
| Commit | `9496e75` on `feat/raytracing` |
| Build in the install | `dxgi.dll` dc00eff8e8fba5ff (2026-10-20T17:12:03), `GameData/ReDefinition/Plugins/ReDefinition.dll` 16f0b4d6956433d3 (2026-10-20T17:05:44) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 17:16:12.040] [ReDefinition] Probe: profile medium applied. |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-1715.json](benchmarks/2026-10-20-1715.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 68.2–73.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.2–72.6 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 61.4–73.9 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 66.6–72.5 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.4–61.9 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.1–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.4–61.3 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 53.9–61.1 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.4–43.7 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 43.1–43.6 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.4–44.4 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 43.6–44.4 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 37.1–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.1–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.5–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 36.9–37.3 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 64.6–73.2 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 71.8–73.0 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 68.7–73.6 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 67.7–73.0 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 57.1–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 54.8–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.6–57.9 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 56.1–57.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.5–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.2–42.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.4–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 39.9–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.4–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 42.3–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 42.2–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 35.9–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 67.5–73.2 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 71.2–72.9 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 71.4–72.9 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 56.5–72.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.8–58.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 58.0–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.7–58.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 54.9–58.9 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.8 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.4–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 41.8–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 42.1–42.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 35.5–36.5 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.0–36.6 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 67.7–72.4 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.4–72.3 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 69.7–72.6 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 71.1–72.0 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.5–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 55.9–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 54.0–56.9 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.0–56.6 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.4–41.5 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 40.4–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.6–41.1 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 34.4–34.9 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 66.0–66.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 66.2–66.5 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 61.1–66.6 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 65.9–66.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 38.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 39.9–40.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 40.0–40.1 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 39.5–40.1 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 58.6–61.9 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 61.3–61.7 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 60.9–61.6 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 60.8–61.6 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 14 | 30.4–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 32.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 32.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 32.1–32.1 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 14 | 36.6–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 8 | 38.3–38.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 59.9–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 59.7–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 62.2–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 62.3–62.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 36.7–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.8–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 38.7–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 57.8–60.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 60.4–60.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 58.9–60.7 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 60.2–60.8 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 15 | 36.0–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | – | 9 | 37.9–37.9 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=0 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=1 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
```

</details>
<!-- benchmark-runs:end -->
