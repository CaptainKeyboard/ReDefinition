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
Run `2026-10-20-1750`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 70.3 | 70.1 | 22.5 | 24.1 | +0.0 % | +0.0 ms | – | – | -1.4 % | baseline |
| FSR 3 · RT Off · FG off · Reflex | 71.2 | 71.1 | 22.4 | 23.8 | +1.3 % | -0.1 ms | +1.3 % | -0.1 ms | +0.1 % |  |
| FSR 3 · RT Off · FG off · brake 4 ms | 71.7 | 71.5 | 22.5 | 23.7 | +2.0 % | +0.0 ms | +2.0 % | +0.0 ms | +0.8 % |  |
| FSR 3 · RT Off · FG off · Reflex + brake 4 ms | 70.8 | 70.8 | 22.6 | 23.9 | +0.7 % | +0.1 ms | +0.7 % | +0.1 ms | +0.1 % |  |
| FSR 3 · RT Off · FG FSR · none | 61.2 | 122.2 | 36.4 | 39.2 | -12.9 % | +13.9 ms | -12.9 % | +13.9 ms | +0.2 % |  |
| FSR 3 · RT Off · FG FSR · Reflex | 61.1 | 122.1 | 36.7 | 39.0 | -13.1 % | +14.2 ms | -13.1 % | +14.2 ms | +0.7 % |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 60.4 | 120.9 | 34.0 | 37.5 | -14.1 % | +11.5 ms | -14.1 % | +11.5 ms | +1.2 % |  |
| FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | 60.0 | 119.9 | 34.3 | 37.0 | -14.7 % | +11.8 ms | -14.7 % | +11.8 ms | +1.7 % |  |
| FSR 3 · RT Full · FG off · none | 43.4 | 43.6 | 59.0 | 61.2 | -38.3 % | +36.5 ms | – | – | +0.0 % |  |
| FSR 3 · RT Full · FG off · Reflex | 43.6 | 43.6 | 58.1 | 60.4 | -38.0 % | +35.6 ms | +0.5 % | -0.9 ms | +0.5 % |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 43.9 | 44.0 | 49.2 | 54.9 | -37.6 % | +26.7 ms | +1.2 % | -9.8 ms | +1.4 % |  |
| FSR 3 · RT Full · FG off · Reflex + brake 4 ms | 44.0 | 44.0 | 49.5 | 54.9 | -37.4 % | +27.0 ms | +1.4 % | -9.5 ms | +0.0 % |  |
| FSR 3 · RT Full · FG FSR · none | 37.1 | 74.0 | 67.5 | 69.3 | -47.2 % | +45.0 ms | -14.5 % | +8.5 ms | +0.0 % |  |
| FSR 3 · RT Full · FG FSR · Reflex | 37.1 | 74.0 | 66.8 | 69.0 | -47.2 % | +44.3 ms | -14.5 % | +7.8 ms | -0.3 % |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 37.1 | 74.2 | 52.4 | 54.6 | -47.2 % | +29.9 ms | -14.5 % | -6.6 ms | +0.0 % |  |
| FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | 37.1 | 74.4 | 52.5 | 56.2 | -47.2 % | +30.0 ms | -14.5 % | -6.5 ms | -0.3 % |  |
| DLSS · RT Off · FG off · none | 69.4 | 69.1 | 24.3 | 25.9 | -1.3 % | +1.8 ms | – | – | -2.4 % |  |
| DLSS · RT Off · FG off · Reflex | 70.3 | 70.2 | 24.2 | 25.9 | +0.0 % | +1.7 ms | +1.3 % | -0.1 ms | -3.0 % |  |
| DLSS · RT Off · FG off · brake 4 ms | 69.2 | 69.0 | 24.4 | 26.1 | -1.6 % | +1.9 ms | -0.3 % | +0.1 ms | -4.3 % |  |
| DLSS · RT Off · FG off · Reflex + brake 4 ms | 70.9 | 70.8 | 24.3 | 25.7 | +0.9 % | +1.8 ms | +2.2 % | +0.0 ms | -1.5 % |  |
| DLSS · RT Off · FG FSR · none | 57.0 | 113.9 | 40.8 | 42.6 | -18.9 % | +18.3 ms | -17.9 % | +16.5 ms | -0.7 % |  |
| DLSS · RT Off · FG FSR · Reflex | 56.7 | 113.3 | 41.2 | 42.9 | -19.3 % | +18.7 ms | -18.3 % | +16.9 ms | -0.4 % |  |
| DLSS · RT Off · FG FSR · brake 4 ms | 56.9 | 113.7 | 35.9 | 40.2 | -19.1 % | +13.4 ms | -18.0 % | +11.6 ms | +0.2 % |  |
| DLSS · RT Off · FG FSR · Reflex + brake 4 ms | 56.6 | 113.1 | 34.7 | 40.5 | -19.5 % | +12.2 ms | -18.4 % | +10.4 ms | -1.0 % |  |
| DLSS · RT Full, RR on · FG off · none | 42.3 | 42.4 | 61.2 | 62.7 | -39.8 % | +38.7 ms | – | – | -0.2 % |  |
| DLSS · RT Full, RR on · FG off · Reflex | 42.5 | 42.6 | 61.4 | 62.9 | -39.5 % | +38.9 ms | +0.5 % | +0.2 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG off · brake 4 ms | 42.4 | 42.4 | 49.3 | 56.1 | -39.7 % | +26.8 ms | +0.2 % | -11.9 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | 42.3 | 42.3 | 48.7 | 56.6 | -39.8 % | +26.2 ms | +0.0 % | -12.5 ms | -0.5 % |  |
| DLSS · RT Full, RR on · FG FSR · none | 35.2 | 70.3 | 68.8 | 71.3 | -49.9 % | +46.3 ms | -16.8 % | +7.6 ms | -2.2 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex | 36.0 | 71.9 | 69.6 | 71.6 | -48.8 % | +47.1 ms | -14.9 % | +8.4 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG FSR · brake 4 ms | 36.0 | 72.1 | 57.5 | 59.8 | -48.8 % | +35.0 ms | -14.9 % | -3.7 ms | -0.3 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | 36.1 | 72.1 | 56.3 | 59.1 | -48.6 % | +33.8 ms | -14.7 % | -4.9 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG off · none | 42.2 | 42.2 | 61.2 | 62.9 | -40.0 % | +38.7 ms | – | – | +0.5 % |  |
| DLSS · RT Full, RR off · FG off · Reflex | 42.4 | 42.4 | 61.2 | 62.7 | -39.7 % | +38.7 ms | +0.5 % | +0.0 ms | +0.0 % |  |
| DLSS · RT Full, RR off · FG off · brake 4 ms | 42.3 | 42.1 | 49.0 | 56.9 | -39.8 % | +26.5 ms | +0.2 % | -12.2 ms | -0.2 % |  |
| DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | 42.3 | 42.1 | 49.6 | 56.8 | -39.8 % | +27.1 ms | +0.2 % | -11.6 ms | -0.2 % |  |
| DLSS · RT Full, RR off · FG FSR · none | 35.9 | 71.9 | 69.6 | 71.5 | -48.9 % | +47.1 ms | -14.9 % | +8.4 ms | -0.3 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex | 35.9 | 72.0 | 69.7 | 71.6 | -48.9 % | +47.2 ms | -14.9 % | +8.5 ms | -0.3 % |  |
| DLSS · RT Full, RR off · FG FSR · brake 4 ms | 35.9 | 71.8 | 57.8 | 60.8 | -48.9 % | +35.3 ms | -14.9 % | -3.4 ms | -0.3 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | 35.9 | 71.9 | 57.6 | 61.0 | -48.9 % | +35.1 ms | -14.9 % | -3.6 ms | -0.3 % |  |
| AMD DLL · RT Off · FG off · none | 68.9 | 68.7 | 24.0 | 25.5 | -2.0 % | +1.5 ms | – | – | -4.0 % |  |
| AMD DLL · RT Off · FG off · Reflex | 70.2 | 70.0 | 23.8 | 25.5 | -0.1 % | +1.3 ms | +1.9 % | -0.2 ms | -2.6 % |  |
| AMD DLL · RT Off · FG off · brake 4 ms | 70.4 | 70.2 | 23.9 | 25.4 | +0.1 % | +1.4 ms | +2.2 % | -0.1 ms | -2.5 % |  |
| AMD DLL · RT Off · FG off · Reflex + brake 4 ms | 69.8 | 69.8 | 23.9 | 25.5 | -0.7 % | +1.4 ms | +1.3 % | -0.1 ms | -0.3 % |  |
| AMD DLL · RT Off · FG FSR · none | 58.1 | 116.1 | 40.5 | 42.3 | -17.4 % | +18.0 ms | -15.7 % | +16.5 ms | -0.2 % |  |
| AMD DLL · RT Off · FG FSR · Reflex | 57.9 | 115.8 | 39.9 | 42.1 | -17.6 % | +17.4 ms | -16.0 % | +15.9 ms | -0.7 % |  |
| AMD DLL · RT Off · FG FSR · brake 4 ms | 58.0 | 116.0 | 36.0 | 40.2 | -17.5 % | +13.5 ms | -15.8 % | +12.0 ms | -0.2 % |  |
| AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | 57.4 | 114.8 | 35.5 | 39.9 | -18.3 % | +13.0 ms | -16.7 % | +11.5 ms | -0.3 % |  |
| AMD DLL · RT Full · FG off · none | 42.3 | 42.2 | 61.3 | 63.0 | -39.8 % | +38.8 ms | – | – | +0.0 % |  |
| AMD DLL · RT Full · FG off · Reflex | 42.4 | 42.2 | 61.3 | 63.1 | -39.7 % | +38.8 ms | +0.2 % | +0.0 ms | +0.0 % |  |
| AMD DLL · RT Full · FG off · brake 4 ms | 42.3 | 42.2 | 49.6 | 55.9 | -39.8 % | +27.1 ms | +0.0 % | -11.7 ms | +0.0 % |  |
| AMD DLL · RT Full · FG off · Reflex + brake 4 ms | 42.3 | 42.1 | 49.8 | 56.6 | -39.8 % | +27.3 ms | +0.0 % | -11.5 ms | +0.0 % |  |
| AMD DLL · RT Full · FG FSR · none | 34.6 | 69.1 | 69.8 | 72.1 | -50.8 % | +47.3 ms | -18.2 % | +8.5 ms | -3.9 % |  |
| AMD DLL · RT Full · FG FSR · Reflex | 33.1 | 66.0 | 75.7 | 78.1 | -52.9 % | +53.2 ms | -21.7 % | +14.4 ms | -8.1 % |  |
| AMD DLL · RT Full · FG FSR · brake 4 ms | 34.8 | 69.5 | 57.7 | 61.7 | -50.5 % | +35.2 ms | -17.7 % | -3.6 ms | -3.3 % |  |
| AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | 36.0 | 71.8 | 57.8 | 60.4 | -48.8 % | +35.3 ms | -14.9 % | -3.5 ms | +0.3 % |  |
| XeSS · RT Off · FG off · none | 67.4 | 67.3 | 25.0 | 26.8 | -4.1 % | +2.5 ms | – | – | -5.3 % |  |
| XeSS · RT Off · FG off · Reflex | 69.2 | 69.0 | 25.2 | 26.9 | -1.6 % | +2.7 ms | +2.7 % | +0.2 ms | -2.8 % |  |
| XeSS · RT Off · FG off · brake 4 ms | 69.5 | 69.2 | 25.1 | 26.7 | -1.1 % | +2.6 ms | +3.1 % | +0.1 ms | -2.7 % |  |
| XeSS · RT Off · FG off · Reflex + brake 4 ms | 68.3 | 68.0 | 24.9 | 26.6 | -2.8 % | +2.4 ms | +1.3 % | -0.1 ms | -4.6 % |  |
| XeSS · RT Off · FG FSR · none | 56.1 | 112.1 | 42.3 | 44.3 | -20.2 % | +19.8 ms | -16.8 % | +17.3 ms | -0.2 % |  |
| XeSS · RT Off · FG FSR · Reflex | 56.1 | 112.1 | 42.7 | 44.4 | -20.2 % | +20.2 ms | -16.8 % | +17.7 ms | -0.2 % |  |
| XeSS · RT Off · FG FSR · brake 4 ms | 56.0 | 112.1 | 37.2 | 42.0 | -20.3 % | +14.7 ms | -16.9 % | +12.2 ms | +0.0 % |  |
| XeSS · RT Off · FG FSR · Reflex + brake 4 ms | 56.1 | 112.1 | 37.9 | 41.8 | -20.2 % | +15.4 ms | -16.8 % | +12.9 ms | +0.0 % |  |
| XeSS · RT Full · FG off · none | 40.9 | 40.9 | 63.6 | 65.3 | -41.8 % | +41.1 ms | – | – | +0.0 % |  |
| XeSS · RT Full · FG off · Reflex | 40.9 | 41.0 | 63.9 | 65.4 | -41.8 % | +41.4 ms | +0.0 % | +0.3 ms | -0.2 % |  |
| XeSS · RT Full · FG off · brake 4 ms | 40.8 | 41.0 | 51.2 | 58.4 | -42.0 % | +28.7 ms | -0.2 % | -12.4 ms | -0.2 % |  |
| XeSS · RT Full · FG off · Reflex + brake 4 ms | 40.7 | 40.7 | 52.2 | 59.0 | -42.1 % | +29.7 ms | -0.5 % | -11.4 ms | -0.5 % |  |
| XeSS · RT Full · FG FSR · none | 34.9 | 69.6 | 70.8 | 73.5 | -50.4 % | +48.3 ms | -14.7 % | +7.2 ms | -0.3 % |  |
| XeSS · RT Full · FG FSR · Reflex | 34.8 | 69.4 | 70.9 | 73.4 | -50.5 % | +48.4 ms | -14.9 % | +7.3 ms | -0.3 % |  |
| XeSS · RT Full · FG FSR · brake 4 ms | 34.7 | 69.3 | 55.5 | 60.3 | -50.6 % | +33.0 ms | -15.2 % | -8.1 ms | -0.6 % |  |
| XeSS · RT Full · FG FSR · Reflex + brake 4 ms | 34.7 | 69.1 | 54.8 | 60.3 | -50.6 % | +32.3 ms | -15.2 % | -8.8 ms | -0.3 % |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.1 | 132.9 | 46.0 | 48.9 | -6.0 % | +23.5 ms | -6.0 % | +23.5 ms | -0.5 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex | 64.1 | 128.6 | 37.7 | 48.0 | -8.8 % | +15.2 ms | -8.8 % | +15.2 ms | -3.3 % |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 66.1 | 133.0 | 48.0 | 49.7 | -6.0 % | +25.5 ms | -6.0 % | +25.5 ms | +1.7 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | 66.2 | 132.4 | 48.0 | 49.8 | -5.8 % | +25.5 ms | -5.8 % | +25.5 ms | +0.0 % |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.2 | 80.7 | 72.5 | 74.2 | -42.8 % | +50.0 ms | -7.4 % | +13.5 ms | +0.2 % |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex | 40.3 | 80.9 | 72.7 | 74.4 | -42.7 % | +50.2 ms | -7.1 % | +13.7 ms | +0.5 % |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.3 | 80.7 | 72.6 | 74.3 | -42.7 % | +50.1 ms | -7.1 % | +13.6 ms | +0.7 % |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | 40.2 | 80.5 | 72.8 | 74.5 | -42.8 % | +50.3 ms | -7.4 % | +13.8 ms | +0.8 % |  |
| DLSS · RT Off · FG DLSS-G · none | 61.4 | 122.9 | 51.8 | 53.5 | -12.7 % | +29.3 ms | -11.5 % | +27.5 ms | +0.0 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex | 61.3 | 122.7 | 50.0 | 52.7 | -12.8 % | +27.5 ms | -11.7 % | +25.7 ms | -0.3 % |  |
| DLSS · RT Off · FG DLSS-G · brake 4 ms | 61.3 | 122.8 | 50.4 | 52.9 | -12.8 % | +27.9 ms | -11.7 % | +26.1 ms | +0.0 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.6 | 123.2 | 51.2 | 53.3 | -12.4 % | +28.7 ms | -11.2 % | +26.9 ms | +0.7 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · none | 32.0 | 63.8 | 91.3 | 93.3 | -54.5 % | +68.8 ms | -24.3 % | +30.1 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex | 32.2 | 64.0 | 91.5 | 93.4 | -54.2 % | +69.0 ms | -23.9 % | +30.3 ms | +0.3 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | 32.1 | 64.0 | 91.3 | 93.2 | -54.3 % | +68.8 ms | -24.1 % | +30.1 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | 32.2 | 64.1 | 91.2 | 93.0 | -54.2 % | +68.7 ms | -23.9 % | +30.0 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · none | 38.4 | 76.7 | 76.1 | 77.8 | -45.4 % | +53.6 ms | -9.0 % | +14.9 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex | 38.5 | 77.0 | 76.1 | 77.5 | -45.2 % | +53.6 ms | -8.8 % | +14.9 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | 38.5 | 77.0 | 75.5 | 77.4 | -45.2 % | +53.0 ms | -8.8 % | +14.3 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | 38.5 | 77.0 | 75.9 | 77.5 | -45.2 % | +53.4 ms | -8.8 % | +14.7 ms | +0.3 % |  |
| AMD DLL · RT Off · FG DLSS-G · none | 62.5 | 125.0 | 49.0 | 52.0 | -11.1 % | +26.5 ms | -9.3 % | +25.0 ms | +0.2 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex | 61.9 | 123.9 | 45.8 | 50.2 | -11.9 % | +23.3 ms | -10.2 % | +21.8 ms | -0.5 % |  |
| AMD DLL · RT Off · FG DLSS-G · brake 4 ms | 62.7 | 125.4 | 49.1 | 52.0 | -10.8 % | +26.6 ms | -9.0 % | +25.1 ms | +0.3 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.2 | 122.6 | 38.0 | 51.3 | -12.9 % | +15.5 ms | -11.2 % | +14.0 ms | -2.2 % |  |
| AMD DLL · RT Full · FG DLSS-G · none | 38.7 | 77.2 | 74.9 | 77.0 | -45.0 % | +52.4 ms | -8.5 % | +13.6 ms | +0.0 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex | 38.9 | 77.5 | 75.5 | 77.0 | -44.7 % | +53.0 ms | -8.0 % | +14.2 ms | +0.3 % |  |
| AMD DLL · RT Full · FG DLSS-G · brake 4 ms | 38.8 | 77.5 | 75.1 | 77.2 | -44.8 % | +52.6 ms | -8.3 % | +13.8 ms | +0.0 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.9 | 77.6 | 75.5 | 77.1 | -44.7 % | +53.0 ms | -8.0 % | +14.2 ms | +0.3 % |  |
| XeSS · RT Off · FG DLSS-G · none | 59.2 | 118.4 | 49.6 | 53.2 | -15.8 % | +27.1 ms | -12.2 % | +24.6 ms | -2.0 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex | 60.8 | 121.6 | 52.3 | 54.1 | -13.5 % | +29.8 ms | -9.8 % | +27.3 ms | +0.5 % |  |
| XeSS · RT Off · FG DLSS-G · brake 4 ms | 60.7 | 121.4 | 51.8 | 53.9 | -13.7 % | +29.3 ms | -9.9 % | +26.8 ms | +0.7 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 60.8 | 121.5 | 52.7 | 54.2 | -13.5 % | +30.2 ms | -9.8 % | +27.7 ms | +0.5 % |  |
| XeSS · RT Full · FG DLSS-G · none | 37.9 | 75.6 | 77.1 | 78.8 | -46.1 % | +54.6 ms | -7.3 % | +13.5 ms | +0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex | 38.0 | 75.9 | 77.2 | 78.7 | -45.9 % | +54.7 ms | -7.1 % | +13.6 ms | +0.3 % |  |
| XeSS · RT Full · FG DLSS-G · brake 4 ms | 38.0 | 75.8 | 77.1 | 78.9 | -45.9 % | +54.6 ms | -7.1 % | +13.5 ms | +0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.0 | 75.8 | 77.4 | 79.2 | -45.9 % | +54.9 ms | -7.1 % | +13.8 ms | +0.3 % |  |
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's settings in effect, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
### Run 2026-10-20-1750

| What | Value |
|---|---|
| Date | 2026-10-20T17:50:03 to 2026-10-20T18:20:06 |
| Commit | `13027f0` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 66f2b347196d25b8 (2026-10-20T17:46:58), `GameData/ReDefinition/Plugins/ReDefinition.dll` 0af52ec7ce941ba0 (2026-10-20T17:47:03) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 17:51:05.741] [ReDefinition] Probe: profile medium applied. |
| Matrix | `tools/benchmark_matrix.json`, 2 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-1750.json](benchmarks/2026-10-20-1750.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 68.0–72.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.2–72.4 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 71.1–72.4 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 66.9–71.9 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.3–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.7–61.6 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.9–61.5 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.2–61.2 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.5–43.9 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 43.3–44.0 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 43.8–44.1 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 43.8–44.1 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.0–37.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.0–37.1 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.8–37.4 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.9–37.5 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 63.9–71.4 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 67.0–71.2 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 65.8–71.1 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 70.3–71.4 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.4–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 52.2–57.5 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.7–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.7–57.5 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.6–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.4–42.6 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.1–42.8 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 41.7–43.0 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 31.6–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.6–36.3 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.3–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.3–42.5 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 41.8–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 41.9–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.3–36.3 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.8–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.7–36.3 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 65.6–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 69.2–70.9 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 69.9–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.1–71.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.8–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 57.4–58.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.8–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 54.5–58.5 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 40.5–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.3–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 41.6–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.1–42.5 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 32.2–36.2 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 32.1–33.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 33.1–35.9 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.4 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 62.9–70.0 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 65.5–70.5 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.5–70.7 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 63.4–70.5 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 54.8–57.0 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 55.7–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 55.4–56.6 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 55.4–56.5 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.9–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.0 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.6–41.0 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 39.6–41.3 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 34.8–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.7–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 33.9–35.3 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 33.7–35.0 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 63.1–66.9 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.5–66.6 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 63.8–66.7 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.5–66.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 38.4–40.5 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.3–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 39.8–40.3 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 58.5–62.5 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.9–62.2 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.9–61.8 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.6–62.6 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 30.4–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 36.2–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.4–38.6 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 59.5–63.2 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.3–62.9 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.2–62.9 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.2–62.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.9–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.8–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 51.7–61.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.4–61.0 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.5–61.4 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.5–60.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.0–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.0–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.9–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 37.9–38.1 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=0 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 dlssFrameGeneration=1 hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 rtOwnQueue=0 rtCutoutTest=0 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none)
```

</details>

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
