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
Run `2026-10-20-2347`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 70.3 | 70.1 | 22.6 | 23.6 | +0.0 % | +0.0 ms | – | – | – | baseline |
| FSR 3 · RT Off · FG off · Reflex | 70.3 | 70.1 | 22.6 | 24.1 | +0.0 % | +0.0 ms | +0.0 % | +0.0 ms | – |  |
| FSR 3 · RT Off · FG off · brake 4 ms | 70.1 | 69.8 | 22.5 | 23.9 | -0.3 % | -0.1 ms | -0.3 % | -0.1 ms | – |  |
| FSR 3 · RT Off · FG off · Reflex + brake 4 ms | 70.5 | 70.5 | 22.5 | 23.8 | +0.3 % | -0.1 ms | +0.3 % | -0.1 ms | – |  |
| FSR 3 · RT Off · FG FSR · none | 61.6 | 123.1 | 36.7 | 39.0 | -12.4 % | +14.1 ms | -12.4 % | +14.1 ms | – |  |
| FSR 3 · RT Off · FG FSR · Reflex | 61.4 | 122.8 | 36.7 | 39.1 | -12.7 % | +14.1 ms | -12.7 % | +14.1 ms | – |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 61.6 | 123.2 | 33.8 | 37.2 | -12.4 % | +11.2 ms | -12.4 % | +11.2 ms | – |  |
| FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | 61.3 | 122.6 | 34.3 | 37.6 | -12.8 % | +11.7 ms | -12.8 % | +11.7 ms | – |  |
| FSR 3 · RT Full · FG off · none | 43.9 | 43.8 | 58.3 | 59.9 | -37.6 % | +35.7 ms | – | – | – |  |
| FSR 3 · RT Full · FG off · Reflex | 44.1 | 44.0 | 58.6 | 60.0 | -37.3 % | +36.0 ms | +0.5 % | +0.3 ms | – |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 43.9 | 44.0 | 50.5 | 55.7 | -37.6 % | +27.9 ms | +0.0 % | -7.8 ms | – |  |
| FSR 3 · RT Full · FG off · Reflex + brake 4 ms | 44.0 | 44.0 | 50.5 | 55.0 | -37.4 % | +27.9 ms | +0.2 % | -7.8 ms | – |  |
| FSR 3 · RT Full · FG FSR · none | 37.2 | 74.2 | 65.9 | 68.3 | -47.1 % | +43.3 ms | -15.3 % | +7.6 ms | – |  |
| FSR 3 · RT Full · FG FSR · Reflex | 37.2 | 74.4 | 66.5 | 68.6 | -47.1 % | +43.9 ms | -15.3 % | +8.2 ms | – |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 37.1 | 74.0 | 50.6 | 58.5 | -47.2 % | +28.0 ms | -15.5 % | -7.7 ms | – |  |
| FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | 37.2 | 74.4 | 50.3 | 57.2 | -47.1 % | +27.7 ms | -15.3 % | -8.0 ms | – |  |
| FSR 3 · RT Full, radiance cache · FG off · none | 44.4 | 44.6 | 57.8 | 59.3 | -36.8 % | +35.2 ms | – | – | – |  |
| FSR 3 · RT Full, radiance cache · FG FSR · none | 37.5 | 75.0 | 66.2 | 67.8 | -46.7 % | +43.6 ms | -15.5 % | +8.4 ms | – |  |
| DLSS · RT Off · FG off · none | 68.2 | 67.9 | 24.2 | 25.7 | -3.0 % | +1.6 ms | – | – | – |  |
| DLSS · RT Off · FG off · Reflex | 68.1 | 68.0 | 24.2 | 26.0 | -3.1 % | +1.6 ms | -0.1 % | +0.0 ms | – |  |
| DLSS · RT Off · FG off · brake 4 ms | 70.1 | 70.0 | 24.2 | 25.6 | -0.3 % | +1.6 ms | +2.8 % | +0.0 ms | – |  |
| DLSS · RT Off · FG off · Reflex + brake 4 ms | 69.6 | 69.3 | 24.2 | 25.8 | -1.0 % | +1.6 ms | +2.1 % | +0.0 ms | – |  |
| DLSS · RT Off · FG FSR · none | 57.2 | 114.4 | 41.0 | 42.7 | -18.6 % | +18.4 ms | -16.1 % | +16.8 ms | – |  |
| DLSS · RT Off · FG FSR · Reflex | 57.3 | 114.6 | 40.9 | 42.7 | -18.5 % | +18.3 ms | -16.0 % | +16.7 ms | – |  |
| DLSS · RT Off · FG FSR · brake 4 ms | 57.3 | 114.4 | 35.8 | 40.4 | -18.5 % | +13.2 ms | -16.0 % | +11.6 ms | – |  |
| DLSS · RT Off · FG FSR · Reflex + brake 4 ms | 57.0 | 114.0 | 35.5 | 39.0 | -18.9 % | +12.9 ms | -16.4 % | +11.3 ms | – |  |
| DLSS · RT Full, RR on · FG off · none | 35.2 | 34.9 | 75.5 | 77.2 | -49.9 % | +52.9 ms | – | – | – |  |
| DLSS · RT Full, RR on · FG off · Reflex | 35.3 | 35.0 | 75.5 | 77.1 | -49.8 % | +52.9 ms | +0.3 % | +0.0 ms | – |  |
| DLSS · RT Full, RR on · FG off · brake 4 ms | 35.3 | 35.0 | 63.7 | 66.7 | -49.8 % | +41.1 ms | +0.3 % | -11.8 ms | – |  |
| DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | 35.3 | 35.0 | 64.0 | 66.7 | -49.8 % | +41.4 ms | +0.3 % | -11.5 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · none | 30.6 | 61.0 | 83.8 | 86.2 | -56.5 % | +61.2 ms | -13.1 % | +8.3 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · Reflex | 30.6 | 61.0 | 84.0 | 86.1 | -56.5 % | +61.4 ms | -13.1 % | +8.5 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · brake 4 ms | 30.6 | 61.0 | 72.8 | 75.1 | -56.5 % | +50.2 ms | -13.1 % | -2.7 ms | – |  |
| DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | 30.5 | 61.0 | 73.0 | 75.0 | -56.6 % | +50.4 ms | -13.4 % | -2.5 ms | – |  |
| DLSS · RT Full, RR off · FG off · none | 42.3 | 42.5 | 61.2 | 62.6 | -39.8 % | +38.6 ms | – | – | – |  |
| DLSS · RT Full, RR off · FG off · Reflex | 42.5 | 42.6 | 61.2 | 62.6 | -39.5 % | +38.6 ms | +0.5 % | +0.0 ms | – |  |
| DLSS · RT Full, RR off · FG off · brake 4 ms | 42.5 | 42.4 | 49.2 | 56.4 | -39.5 % | +26.6 ms | +0.5 % | -12.0 ms | – |  |
| DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | 42.3 | 42.4 | 48.4 | 56.7 | -39.8 % | +25.8 ms | +0.0 % | -12.8 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · none | 36.0 | 72.0 | 69.5 | 71.5 | -48.8 % | +46.9 ms | -14.9 % | +8.3 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · Reflex | 36.0 | 71.9 | 69.5 | 71.6 | -48.8 % | +46.9 ms | -14.9 % | +8.3 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · brake 4 ms | 36.0 | 72.0 | 56.0 | 58.2 | -48.8 % | +33.4 ms | -14.9 % | -5.2 ms | – |  |
| DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | 36.0 | 72.0 | 56.8 | 58.9 | -48.8 % | +34.2 ms | -14.9 % | -4.4 ms | – |  |
| AMD DLL · RT Off · FG off · none | 69.5 | 69.4 | 23.8 | 25.3 | -1.1 % | +1.2 ms | – | – | – |  |
| AMD DLL · RT Off · FG off · Reflex | 69.9 | 69.8 | 23.8 | 25.4 | -0.6 % | +1.2 ms | +0.6 % | +0.0 ms | – |  |
| AMD DLL · RT Off · FG off · brake 4 ms | 69.3 | 69.1 | 24.0 | 25.4 | -1.4 % | +1.4 ms | -0.3 % | +0.2 ms | – |  |
| AMD DLL · RT Off · FG off · Reflex + brake 4 ms | 69.9 | 69.8 | 23.8 | 25.2 | -0.6 % | +1.2 ms | +0.6 % | +0.0 ms | – |  |
| AMD DLL · RT Off · FG FSR · none | 58.1 | 116.1 | 40.0 | 42.1 | -17.4 % | +17.4 ms | -16.4 % | +16.2 ms | – |  |
| AMD DLL · RT Off · FG FSR · Reflex | 58.1 | 116.0 | 40.1 | 42.1 | -17.4 % | +17.5 ms | -16.4 % | +16.3 ms | – |  |
| AMD DLL · RT Off · FG FSR · brake 4 ms | 58.2 | 116.2 | 36.6 | 40.2 | -17.2 % | +14.0 ms | -16.3 % | +12.8 ms | – |  |
| AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | 57.8 | 115.5 | 35.7 | 39.8 | -17.8 % | +13.1 ms | -16.8 % | +11.9 ms | – |  |
| AMD DLL · RT Full · FG off · none | 42.3 | 42.3 | 61.2 | 62.5 | -39.8 % | +38.6 ms | – | – | – |  |
| AMD DLL · RT Full · FG off · Reflex | 42.5 | 42.4 | 61.2 | 62.7 | -39.5 % | +38.6 ms | +0.5 % | +0.0 ms | – |  |
| AMD DLL · RT Full · FG off · brake 4 ms | 42.4 | 42.4 | 50.1 | 56.3 | -39.7 % | +27.5 ms | +0.2 % | -11.1 ms | – |  |
| AMD DLL · RT Full · FG off · Reflex + brake 4 ms | 42.5 | 42.5 | 49.7 | 56.2 | -39.5 % | +27.1 ms | +0.5 % | -11.5 ms | – |  |
| AMD DLL · RT Full · FG FSR · none | 36.0 | 72.0 | 69.5 | 71.3 | -48.8 % | +46.9 ms | -14.9 % | +8.3 ms | – |  |
| AMD DLL · RT Full · FG FSR · Reflex | 36.0 | 71.9 | 69.6 | 71.4 | -48.8 % | +47.0 ms | -14.9 % | +8.4 ms | – |  |
| AMD DLL · RT Full · FG FSR · brake 4 ms | 36.0 | 72.0 | 56.8 | 59.0 | -48.8 % | +34.2 ms | -14.9 % | -4.4 ms | – |  |
| AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | 36.0 | 72.0 | 56.4 | 58.8 | -48.8 % | +33.8 ms | -14.9 % | -4.8 ms | – |  |
| XeSS · RT Off · FG off · none | 68.2 | 68.0 | 24.9 | 26.6 | -3.0 % | +2.3 ms | – | – | – |  |
| XeSS · RT Off · FG off · Reflex | 68.6 | 68.2 | 24.8 | 26.5 | -2.4 % | +2.2 ms | +0.6 % | -0.1 ms | – |  |
| XeSS · RT Off · FG off · brake 4 ms | 68.3 | 68.1 | 24.8 | 26.3 | -2.8 % | +2.2 ms | +0.1 % | -0.1 ms | – |  |
| XeSS · RT Off · FG off · Reflex + brake 4 ms | 68.8 | 68.5 | 24.8 | 26.4 | -2.1 % | +2.2 ms | +0.9 % | -0.1 ms | – |  |
| XeSS · RT Off · FG FSR · none | 56.2 | 112.2 | 41.9 | 43.8 | -20.1 % | +19.3 ms | -17.6 % | +17.0 ms | – |  |
| XeSS · RT Off · FG FSR · Reflex | 56.2 | 112.2 | 42.0 | 43.4 | -20.1 % | +19.4 ms | -17.6 % | +17.1 ms | – |  |
| XeSS · RT Off · FG FSR · brake 4 ms | 56.1 | 112.0 | 37.6 | 41.3 | -20.2 % | +15.0 ms | -17.7 % | +12.7 ms | – |  |
| XeSS · RT Off · FG FSR · Reflex + brake 4 ms | 56.1 | 112.1 | 37.4 | 41.5 | -20.2 % | +14.8 ms | -17.7 % | +12.5 ms | – |  |
| XeSS · RT Full · FG off · none | 40.9 | 40.9 | 63.6 | 65.2 | -41.8 % | +41.0 ms | – | – | – |  |
| XeSS · RT Full · FG off · Reflex | 41.0 | 41.0 | 63.5 | 65.1 | -41.7 % | +40.9 ms | +0.2 % | -0.1 ms | – |  |
| XeSS · RT Full · FG off · brake 4 ms | 41.0 | 41.0 | 51.1 | 58.1 | -41.7 % | +28.5 ms | +0.2 % | -12.5 ms | – |  |
| XeSS · RT Full · FG off · Reflex + brake 4 ms | 41.0 | 41.0 | 50.7 | 57.8 | -41.7 % | +28.1 ms | +0.2 % | -12.9 ms | – |  |
| XeSS · RT Full · FG FSR · none | 35.0 | 70.0 | 71.7 | 74.0 | -50.2 % | +49.1 ms | -14.4 % | +8.1 ms | – |  |
| XeSS · RT Full · FG FSR · Reflex | 34.9 | 69.9 | 72.3 | 74.1 | -50.4 % | +49.7 ms | -14.7 % | +8.7 ms | – |  |
| XeSS · RT Full · FG FSR · brake 4 ms | 35.0 | 70.0 | 60.1 | 62.8 | -50.2 % | +37.5 ms | -14.4 % | -3.5 ms | – |  |
| XeSS · RT Full · FG FSR · Reflex + brake 4 ms | 34.9 | 69.9 | 60.2 | 63.2 | -50.4 % | +37.6 ms | -14.7 % | -3.4 ms | – |  |
| FSR 3 · RT Full, opacity micromaps · FG off · none | 39.6 | 39.9 | 65.8 | 67.4 | -43.7 % | +43.2 ms | – | – | – |  |
| FSR 3 · RT Full, opacity micromaps · FG FSR · none | 34.0 | 68.0 | 74.3 | 76.2 | -51.6 % | +51.7 ms | -14.1 % | +8.5 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.0 | 132.0 | 47.0 | 49.5 | -6.1 % | +24.4 ms | -6.1 % | +24.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex | 65.9 | 131.9 | 47.0 | 49.6 | -6.3 % | +24.4 ms | -6.3 % | +24.4 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 66.1 | 132.2 | 47.6 | 49.7 | -6.0 % | +25.0 ms | -6.0 % | +25.0 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | 65.8 | 131.9 | 47.4 | 49.7 | -6.4 % | +24.8 ms | -6.4 % | +24.8 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.1 | 80.5 | 72.5 | 74.1 | -43.0 % | +49.9 ms | -8.7 % | +14.2 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex | 40.1 | 80.2 | 72.5 | 74.4 | -43.0 % | +49.9 ms | -8.7 % | +14.2 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.3 | 80.8 | 72.8 | 74.2 | -42.7 % | +50.2 ms | -8.2 % | +14.5 ms | – |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | 40.1 | 80.4 | 73.3 | 74.9 | -43.0 % | +50.7 ms | -8.7 % | +15.0 ms | – |  |
| FSR 3 · RT Full, radiance cache · FG DLSS-G · none | 39.7 | 79.4 | 73.1 | 74.9 | -43.5 % | +50.5 ms | -10.6 % | +15.3 ms | – |  |
| DLSS · RT Off · FG DLSS-G · none | 60.9 | 121.7 | 52.1 | 53.7 | -13.4 % | +29.5 ms | -10.7 % | +27.9 ms | – |  |
| DLSS · RT Off · FG DLSS-G · Reflex | 61.0 | 121.9 | 52.0 | 54.0 | -13.2 % | +29.4 ms | -10.6 % | +27.8 ms | – |  |
| DLSS · RT Off · FG DLSS-G · brake 4 ms | 60.9 | 121.9 | 52.1 | 53.9 | -13.4 % | +29.5 ms | -10.7 % | +27.9 ms | – |  |
| DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.0 | 122.0 | 52.2 | 53.9 | -13.2 % | +29.6 ms | -10.6 % | +28.0 ms | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · none | 31.9 | 63.7 | 91.5 | 93.3 | -54.6 % | +68.9 ms | -9.4 % | +16.0 ms | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex | 32.1 | 64.0 | 91.7 | 93.1 | -54.3 % | +69.1 ms | -8.8 % | +16.2 ms | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | 32.1 | 64.0 | 91.7 | 93.3 | -54.3 % | +69.1 ms | -8.8 % | +16.2 ms | – |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | 32.1 | 64.0 | 91.9 | 93.3 | -54.3 % | +69.3 ms | -8.8 % | +16.4 ms | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · none | 38.2 | 76.6 | 76.1 | 77.9 | -45.7 % | +53.5 ms | -9.7 % | +14.9 ms | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex | 38.4 | 77.0 | 76.6 | 78.0 | -45.4 % | +54.0 ms | -9.2 % | +15.4 ms | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | 38.4 | 77.0 | 76.1 | 77.8 | -45.4 % | +53.5 ms | -9.2 % | +14.9 ms | – |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | 38.4 | 77.0 | 76.1 | 78.0 | -45.4 % | +53.5 ms | -9.2 % | +14.9 ms | – |  |
| AMD DLL · RT Off · FG DLSS-G · none | 61.9 | 123.8 | 51.0 | 52.9 | -11.9 % | +28.4 ms | -10.9 % | +27.2 ms | – |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex | 62.3 | 124.7 | 51.1 | 52.9 | -11.4 % | +28.5 ms | -10.4 % | +27.3 ms | – |  |
| AMD DLL · RT Off · FG DLSS-G · brake 4 ms | 62.1 | 124.2 | 50.9 | 52.8 | -11.7 % | +28.3 ms | -10.6 % | +27.1 ms | – |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | 62.2 | 124.6 | 51.2 | 52.9 | -11.5 % | +28.6 ms | -10.5 % | +27.4 ms | – |  |
| AMD DLL · RT Full · FG DLSS-G · none | 38.6 | 76.9 | 75.3 | 77.2 | -45.1 % | +52.7 ms | -8.7 % | +14.1 ms | – |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex | 38.8 | 77.4 | 75.4 | 77.2 | -44.8 % | +52.8 ms | -8.3 % | +14.2 ms | – |  |
| AMD DLL · RT Full · FG DLSS-G · brake 4 ms | 38.8 | 77.2 | 75.4 | 77.2 | -44.8 % | +52.8 ms | -8.3 % | +14.2 ms | – |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.8 | 77.2 | 75.4 | 77.1 | -44.8 % | +52.8 ms | -8.3 % | +14.2 ms | – |  |
| XeSS · RT Off · FG DLSS-G · none | 59.8 | 119.6 | 52.9 | 54.7 | -14.9 % | +30.3 ms | -12.3 % | +28.0 ms | – |  |
| XeSS · RT Off · FG DLSS-G · Reflex | 60.2 | 120.4 | 53.1 | 54.7 | -14.4 % | +30.5 ms | -11.7 % | +28.2 ms | – |  |
| XeSS · RT Off · FG DLSS-G · brake 4 ms | 60.1 | 120.1 | 52.9 | 54.6 | -14.5 % | +30.3 ms | -11.9 % | +28.0 ms | – |  |
| XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 60.2 | 120.3 | 52.9 | 54.5 | -14.4 % | +30.3 ms | -11.7 % | +28.0 ms | – |  |
| XeSS · RT Full · FG DLSS-G · none | 37.7 | 75.2 | 77.3 | 79.0 | -46.4 % | +54.7 ms | -7.8 % | +13.7 ms | – |  |
| XeSS · RT Full · FG DLSS-G · Reflex | 37.8 | 75.5 | 77.3 | 79.1 | -46.2 % | +54.7 ms | -7.6 % | +13.7 ms | – |  |
| XeSS · RT Full · FG DLSS-G · brake 4 ms | 37.9 | 75.5 | 77.4 | 79.1 | -46.1 % | +54.8 ms | -7.3 % | +13.8 ms | – |  |
| XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | 37.8 | 75.5 | 77.3 | 78.9 | -46.2 % | +54.7 ms | -7.6 % | +13.7 ms | – |  |
| FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | 37.0 | 73.9 | 78.7 | 80.5 | -47.4 % | +56.1 ms | -6.6 % | +12.9 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G preset C · none | 71.6 | 71.7 | 22.4 | 23.4 | +1.8 % | -0.2 ms | +1.8 % | -0.2 ms | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG DLSS-G preset C · none | 44.9 | 44.8 | 55.8 | 57.3 | -36.1 % | +33.2 ms | +2.3 % | -2.5 ms | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | 72.0 | 71.8 | 22.3 | 23.6 | +2.4 % | -0.3 ms | +2.4 % | -0.3 ms | +0.1 % |  |
| FSR 3 · RT Off · FG XeSS-FG · XeLL | 59.4 | 118.8 | 34.3 | 39.3 | -15.5 % | +11.7 ms | -15.5 % | +11.7 ms | -1.0 % |  |
| FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | 44.9 | 44.8 | 59.2 | 60.7 | -36.1 % | +36.6 ms | +2.3 % | +0.9 ms | -0.2 % |  |
| FSR 3 · RT Full · FG XeSS-FG · XeLL | 36.7 | 73.6 | 59.6 | 62.8 | -47.8 % | +37.0 ms | -16.4 % | +1.3 ms | +0.0 % |  |
| FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | 45.2 | 45.2 | 58.6 | 60.1 | -35.7 % | +36.0 ms | +1.8 % | +0.8 ms | +0.0 % |  |
| FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | 37.0 | 74.1 | 59.2 | 62.0 | -47.4 % | +36.6 ms | -16.7 % | +1.4 ms | +0.3 % |  |
| DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | 69.2 | 69.1 | 24.2 | 25.8 | -1.6 % | +1.6 ms | +1.5 % | +0.0 ms | -0.1 % |  |
| DLSS · RT Off · FG XeSS-FG · XeLL | 54.9 | 109.6 | 38.9 | 42.4 | -21.9 % | +16.3 ms | -19.5 % | +14.7 ms | -0.4 % |  |
| DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | 35.2 | 34.9 | 76.1 | 77.8 | -49.9 % | +53.5 ms | +0.0 % | +0.6 ms | -0.3 % |  |
| DLSS · RT Full, RR on · FG XeSS-FG · XeLL | 29.8 | 59.8 | 74.1 | 76.4 | -57.6 % | +51.5 ms | -15.3 % | -1.4 ms | +0.3 % |  |
| DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | 42.5 | 42.8 | 63.3 | 65.0 | -39.5 % | +40.7 ms | +0.5 % | +2.1 ms | +0.2 % |  |
| DLSS · RT Full, RR off · FG XeSS-FG · XeLL | 35.0 | 69.9 | 62.3 | 65.7 | -50.2 % | +39.7 ms | -17.3 % | +1.1 ms | +0.3 % |  |
| AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | 70.3 | 70.1 | 23.8 | 25.4 | +0.0 % | +1.2 ms | +1.2 % | +0.0 ms | +0.3 % |  |
| AMD DLL · RT Off · FG XeSS-FG · XeLL | 56.1 | 112.0 | 38.1 | 41.3 | -20.2 % | +15.5 ms | -19.3 % | +14.3 ms | +0.5 % |  |
| AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | 42.9 | 42.9 | 62.1 | 63.7 | -39.0 % | +39.5 ms | +1.4 % | +0.9 ms | +0.2 % |  |
| AMD DLL · RT Full · FG XeSS-FG · XeLL | 35.4 | 70.9 | 61.9 | 64.5 | -49.6 % | +39.3 ms | -16.3 % | +0.7 ms | +0.3 % |  |
| XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | 70.6 | 70.6 | 24.9 | 26.9 | +0.4 % | +2.3 ms | +3.5 % | +0.0 ms | +1.1 % |  |
| XeSS · RT Off · FG XeSS-FG · XeLL | 53.5 | 107.0 | 39.7 | 42.7 | -23.9 % | +17.1 ms | -21.6 % | +14.8 ms | -0.4 % |  |
| XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | 42.0 | 41.8 | 64.3 | 66.1 | -40.3 % | +41.7 ms | +2.7 % | +0.7 ms | +0.5 % |  |
| XeSS · RT Full · FG XeSS-FG · XeLL | 34.7 | 69.2 | 63.0 | 65.7 | -50.6 % | +40.4 ms | -15.2 % | -0.6 ms | +0.3 % |  |
| FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | 41.1 | 40.9 | 65.1 | 67.0 | -41.5 % | +42.5 ms | +3.8 % | -0.7 ms | +0.2 % |  |
| FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | 34.1 | 68.0 | 64.4 | 66.3 | -51.5 % | +41.8 ms | -13.9 % | -1.4 ms | +0.6 % |  |
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's settings in effect, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
### Run 2026-10-20-2347

| What | Value |
|---|---|
| Date | 2026-10-20T23:47:50 to 2026-10-21T00:30:38 |
| Commit | `a02fdd2` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 57fa8380b776e4a5 (2026-10-20T23:45:44), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 23:48:52.605] [ReDefinition] Probe: profile medium applied. |
| Matrix | `tools/benchmark_matrix.json`, 4 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2347.json](benchmarks/2026-10-20-2347.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 67.1–71.2 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 68.2–70.8 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.7–71.0 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.2–71.4 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 61.5–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 59.4–62.0 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 61.4–62.2 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 60.3–62.0 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.8–44.2 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 44.0–44.2 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 43.0–44.5 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 43.7–44.2 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 37.1–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.8–37.6 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 36.6–37.6 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 36.7–37.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 41.8–44.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.4–37.7 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 60.6–70.7 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 56.8–70.9 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 69.4–70.9 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 65.6–70.9 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 55.9–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 57.1–57.5 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.8–57.9 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.5–57.7 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 33.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 35.0–35.3 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 35.2–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 30.5–30.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 39.8–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.1–42.6 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.1–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 41.8–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.9–36.0 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.8–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 35.8–36.2 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 64.9–71.0 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.3–70.7 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 66.0–70.6 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 69.1–70.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.9–58.3 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 57.7–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 57.7–58.6 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 56.8–58.4 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.1–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.4–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.5 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.3–42.7 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 35.6–36.4 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.3 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.7–36.2 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 63.1–69.9 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 65.1–69.6 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 64.3–69.8 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 67.8–69.4 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 56.0–56.4 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 55.7–56.3 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 55.2–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 54.7–56.6 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 38.7–41.1 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.1 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.7–41.2 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.6–35.3 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 34.9–35.0 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 34.9–35.0 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.6–39.8 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 33.9–34.0 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.2–66.8 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.4–66.3 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.9–66.4 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.2–66.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 37.4–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 40.0–40.3 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 40.2–40.4 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 39.7–40.3 |  |
| dlssg | FSR 3 · RT Full, radiance cache · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 37.3–40.1 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 57.0–61.8 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.8–61.2 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.7–61.9 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 60.8–61.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 30.0–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 32.1–32.1 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.1–32.2 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.1–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.4–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.3–38.5 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.3–38.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 57.8–63.1 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.0–62.4 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 61.0–62.5 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.1–62.3 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 36.1–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.8 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.7–38.8 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 55.7–60.5 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–60.4 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.7–60.5 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.0–60.5 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.3–37.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.6–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.8–37.9 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.8–37.9 |  |
| dlssg | FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 34.8–37.2 |  |
| dlssg-c | FSR 3 · RT Off · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 9 | 68.9–72.5 | frame generation asked for, presented/rendered 1.00 |
| dlssg-c | FSR 3 · RT Full · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 14 | 43.4–45.2 | frame generation asked for, presented/rendered 1.00 |
| xessfg | FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 9 | 67.8–73.2 |  |
| xessfg | FSR 3 · RT Off · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 58.3–59.9 |  |
| xessfg | FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 43.5–45.2 |  |
| xessfg | FSR 3 · RT Full · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 36.3–36.9 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 42.7–45.5 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 36.8–37.2 |  |
| xessfg | DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 61.3–71.9 |  |
| xessfg | DLSS · RT Off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 53.9–55.2 |  |
| xessfg | DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 33.0–35.6 |  |
| xessfg | DLSS · RT Full, RR on · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 29.5–29.8 |  |
| xessfg | DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.8–42.7 |  |
| xessfg | DLSS · RT Full, RR off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.0–35.3 |  |
| xessfg | AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 66.0–72.0 |  |
| xessfg | AMD DLL · RT Off · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 55.7–56.4 |  |
| xessfg | AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 40.6–43.3 |  |
| xessfg | AMD DLL · RT Full · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 35.3–35.7 |  |
| xessfg | XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 64.9–72.3 |  |
| xessfg | XeSS · RT Off · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 48.4–54.6 |  |
| xessfg | XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.5–42.2 |  |
| xessfg | XeSS · RT Full · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.3–34.9 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 38.7–41.4 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 34.0–34.2 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg-c: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
xessfg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=xess rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=1 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(set)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 10 frames, 1309 meshes, 514301 triangles; the slowest 0.62 ms of the GPU, 6.35 ms of the CPU
* dlssg: 10 frames, 1321 meshes, 556686 triangles; the slowest 0.64 ms of the GPU, 2.89 ms of the CPU
* dlssg-c: 5 frames, 623 meshes, 266707 triangles; the slowest 0.61 ms of the GPU, 5.36 ms of the CPU
* xessfg: 10 frames, 1245 meshes, 496559 triangles; the slowest 0.64 ms of the GPU, 3.66 ms of the CPU

</details>

### Run 2026-10-20-2334

| What | Value |
|---|---|
| Date | 2026-10-20T23:34:49 to 2026-10-20T23:44:21 |
| Commit | `f2c569e` with local changes on `feat/raytracing` |
| Build in the install | `dxgi.dll` 0dc39d0d30890ddc (2026-10-20T22:39:26), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 23:35:52.187] [ReDefinition] Probe: profile medium applied. |
| Matrix | `tools/benchmark_matrix.json`, 1 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2334.json](benchmarks/2026-10-20-2334.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| xessfg | FSR 3 · RT Off · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 9 | 70.3–72.4 |  |
| xessfg | FSR 3 · RT Off · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 59.2–60.4 |  |
| xessfg | FSR 3 · RT Full · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 42.5–45.6 |  |
| xessfg | FSR 3 · RT Full · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.6–36.9 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 42.7–45.5 |  |
| xessfg | FSR 3 · RT Full, radiance cache · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 36.6–37.2 |  |
| xessfg | DLSS · RT Off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 63.6–71.7 |  |
| xessfg | DLSS · RT Off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 54.8–55.4 |  |
| xessfg | DLSS · RT Full, RR on · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 33.5–35.5 |  |
| xessfg | DLSS · RT Full, RR on · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 29.5–29.8 |  |
| xessfg | DLSS · RT Full, RR off · FG off (XeSS-FG loaded) · XeLL | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 40.1–42.7 |  |
| xessfg | DLSS · RT Full, RR off · FG XeSS-FG · XeLL | DLSS Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.7–35.1 |  |
| xessfg | AMD DLL · RT Off · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 63.9–71.4 |  |
| xessfg | AMD DLL · RT Off · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 55.5–56.1 |  |
| xessfg | AMD DLL · RT Full · FG off (XeSS-FG loaded) · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 40.5–43.1 |  |
| xessfg | AMD DLL · RT Full · FG XeSS-FG · XeLL | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 35.1–35.6 |  |
| xessfg | XeSS · RT Off · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 65.0–71.0 |  |
| xessfg | XeSS · RT Off · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 51.3–54.2 |  |
| xessfg | XeSS · RT Full · FG off (XeSS-FG loaded) · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 15 | 39.5–42.0 |  |
| xessfg | XeSS · RT Full · FG XeSS-FG · XeLL | XeSS (DLL) Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 9 | 34.5–34.7 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG off (XeSS-FG loaded) · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing (XeLL's markers only) | 14 | 38.5–41.6 |  |
| xessfg | FSR 3 · RT Full, opacity micromaps · FG XeSS-FG · XeLL | FSR 3 Quality, 1707x960 to 2560x1440 | XeSS-FG | 2.00 | XeLL | 8 | 33.8–34.0 |  |

The proxy's configuration in each start, as the proxy logged it at load:

```
xessfg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=xess rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=1 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=1 xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(set)
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* xessfg: 10 frames, 1308 meshes, 552862 triangles; the slowest 2.98 ms of the GPU, 7.30 ms of the CPU

</details>

### Run 2026-10-20-2247

| What | Value |
|---|---|
| Date | 2026-10-20T22:47:05 to 2026-10-20T23:33:55 |
| Commit | `a0913cb` on `feat/raytracing` |
| Build in the install | `dxgi.dll` 0dc39d0d30890ddc (2026-10-20T22:39:26), `GameData/ReDefinition/Plugins/ReDefinition.dll` e9d02b228931a8bb (2026-10-20T22:46:54) |
| Machine | NVIDIA RTX A4500, driver 597.16, AMD Ryzen 5 5600X 6-Core Processor, 31.9 GB, Microsoft Windows 11 Pro 26300, screen 2560x1440 rendered at 1707x960, 120 Hz |
| Profile | `medium`: [LOG 22:48:43.839] [ReDefinition] Probe: profile medium applied. |
| Matrix | `tools/benchmark_matrix.json`, 4 starts, 2 s settling per phase, 8 s after a change of the rendering |
| Skipped | latency antilag2 (only on AMD) |
| Full record | [benchmarks/2026-10-20-2247.json](benchmarks/2026-10-20-2247.json) |

<details><summary>What ran in each phase</summary>

| Start | Scenario | Upscaler that ran | Frame generation that ran | presented/rendered | Lowered by | Reports | fps range | Notes |
|---|---|---|---|---|---|---|---|---|
| fsr | FSR 3 · RT Off · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 9 | 70.7–71.7 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 63.3–71.9 |  |
| fsr | FSR 3 · RT Off · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 67.3–71.6 |  |
| fsr | FSR 3 · RT Off · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 69.9–71.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 60.0–61.7 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 60.5–61.8 |  |
| fsr | FSR 3 · RT Off · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 60.6–61.8 |  |
| fsr | FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 58.9–61.3 |  |
| fsr | FSR 3 · RT Full · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 10 | 44.3–44.4 | no frame time report inside the phase |
| fsr | FSR 3 · RT Full · FG off · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 44.1–44.3 |  |
| fsr | FSR 3 · RT Full · FG off · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 43.9–44.5 |  |
| fsr | FSR 3 · RT Full · FG off · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 43.8–44.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.1–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 37.2–37.3 |  |
| fsr | FSR 3 · RT Full · FG FSR · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 37.2–37.5 |  |
| fsr | FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.9–37.6 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 42.1–44.7 |  |
| fsr | FSR 3 · RT Full, radiance cache · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 37.3–37.8 |  |
| fsr | DLSS · RT Off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 57.4–70.1 |  |
| fsr | DLSS · RT Off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 66.8–69.9 |  |
| fsr | DLSS · RT Off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 68.7–70.2 |  |
| fsr | DLSS · RT Off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 65.7–70.0 |  |
| fsr | DLSS · RT Off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 56.6–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 56.6–57.6 |  |
| fsr | DLSS · RT Off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 56.3–57.7 |  |
| fsr | DLSS · RT Off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 55.9–57.7 |  |
| fsr | DLSS · RT Full, RR on · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 33.4–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 34.8–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 9 | 35.3–35.4 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 30.6–30.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · none | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 40.2–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 42.5–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG off · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.4–42.8 |  |
| fsr | DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 41.9–42.7 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · none | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.0–36.1 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 35.9–36.2 |  |
| fsr | DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 36.0–36.2 |  |
| fsr | AMD DLL · RT Off · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 63.7–70.1 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 69.6–70.3 |  |
| fsr | AMD DLL · RT Off · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 68.9–70.1 |  |
| fsr | AMD DLL · RT Off · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 64.3–70.5 |  |
| fsr | AMD DLL · RT Off · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 57.9–58.4 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 56.9–58.7 |  |
| fsr | AMD DLL · RT Off · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 8 | 56.0–58.8 |  |
| fsr | AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 8 | 57.6–58.5 |  |
| fsr | AMD DLL · RT Full · FG off · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 40.1–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 42.5–42.6 |  |
| fsr | AMD DLL · RT Full · FG off · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 42.2–42.7 |  |
| fsr | AMD DLL · RT Full · FG off · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 42.3–42.9 |  |
| fsr | AMD DLL · RT Full · FG FSR · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 36.0–36.1 |  |
| fsr | AMD DLL · RT Full · FG FSR · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 36.0–36.2 |  |
| fsr | AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 35.6–36.2 |  |
| fsr | XeSS · RT Off · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 15 | 64.4–69.9 |  |
| fsr | XeSS · RT Off · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 9 | 65.4–69.5 |  |
| fsr | XeSS · RT Off · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 9 | 67.8–70.0 |  |
| fsr | XeSS · RT Off · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 68.4–69.9 |  |
| fsr | XeSS · RT Off · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 55.3–56.6 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 8 | 56.0–56.5 |  |
| fsr | XeSS · RT Off · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 55.4–56.9 |  |
| fsr | XeSS · RT Off · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 54.4–56.9 |  |
| fsr | XeSS · RT Full · FG off · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 39.8–41.3 |  |
| fsr | XeSS · RT Full · FG off · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex | 8 | 40.9–41.2 |  |
| fsr | XeSS · RT Full · FG off · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | the brake | 8 | 40.9–41.5 |  |
| fsr | XeSS · RT Full · FG off · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | none | 1.00 | Reflex and the brake | 8 | 40.7–41.3 |  |
| fsr | XeSS · RT Full · FG FSR · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 9 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex | 9 | 34.9–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | the brake | 9 | 34.8–35.1 |  |
| fsr | XeSS · RT Full · FG FSR · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | FSR | 2.00 | Reflex and the brake | 9 | 34.8–35.1 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG off · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | nothing | 14 | 37.7–40.0 |  |
| fsr | FSR 3 · RT Full, opacity micromaps · FG FSR · none | FSR 3 Quality, 1707x960 to 2560x1440 | FSR | 2.00 | nothing | 8 | 34.0–34.1 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 65.8–66.5 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.2–66.2 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 65.8–66.3 |  |
| dlssg | FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 64.8–66.2 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 2.00 | Streamline's Reflex | 10 | 39.9–40.8 | no frame time report inside the phase |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.5–40.6 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.4–40.6 |  |
| dlssg | FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 40.3–40.5 |  |
| dlssg | FSR 3 · RT Full, radiance cache · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 37.2–40.3 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 59.0–61.5 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 61.0–61.4 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–61.4 |  |
| dlssg | DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 59.9–61.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 29.8–32.2 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 32.2–32.3 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · none | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 35.9–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | DLSS Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.5–38.6 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 58.2–62.8 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 62.4–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.3–62.7 |  |
| dlssg | AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 62.4–62.7 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · none | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 36.1–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.7–39.0 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.8–38.9 |  |
| dlssg | AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | AMD FSR (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 38.9–38.9 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 15 | 56.6–60.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.7–60.7 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 60.1–60.6 |  |
| dlssg | XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 59.3–60.7 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · none | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 35.3–38.1 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 38.0–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 8 | 37.3–38.0 |  |
| dlssg | XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | XeSS (DLL) Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 9 | 37.9–38.0 |  |
| dlssg | FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | FSR 3 Quality, 1707x960 to 2560x1440 | DLSS-G | 2.00 | Streamline's Reflex | 14 | 34.5–37.4 |  |
| dlssg-c | FSR 3 · RT Off · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 8 | 68.3–71.6 | frame generation asked for, presented/rendered 1.00 |
| dlssg-c | FSR 3 · RT Full · FG DLSS-G preset C · none | FSR 3 Quality, 1707x960 to 2560x1440 | none | 1.00 | Streamline's Reflex | 14 | 42.0–45.2 | frame generation asked for, presented/rendered 1.00 |
| xessfg | – | – | – | – | – | – | – | no logs: the start did not run |

The proxy's configuration in each start, as the proxy logged it at load:

```
fsr: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=fsr rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=1000 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(set) xessDirectory=(set) streamlineDirectory=(none) xessFgDirectory=(none)
dlssg-c: enabled=1 allowTearing=1 measureOnly=0 frameGeneration=1 frameGenerationTechnique=dlss rrPath=auto hudLessColour=1 fgDepthInverted=1 fgNegateMotionScale=0 fgFlipInputs=1 fgFlipHudLess=0 fgCameraBasis=1 fgVSync=0 fgHalfRefreshLimit=0 fgAsyncWorkloads=0 fgObjectSeparationMetres=0.000000 dlssgMode=on pacing=0.100000/0.100000 fgMinimumFps=0 lowLatency=0 latencyBrake=0/4.000000/1 antiLag2=0 xell=1 xellLowLatencyBoost=0 vendorDiagnostics=0 streamlineImgui=0 fgShowOnlyGenerated=0 xessFgUiComposition=0 xessFgUiMode=auto xessFgFrames=1 xessFgSceneChangeThreshold=0.700000 xessFgTagInterpolatedFrames=0 xessFgPresentFailedInterpolation=0 xessFgOwnWaitable=auto xessFgGraceFrames=3 xessFgBadConstants=skip xessFgAllowOld=0 xessFgResetOnChange=0 xessFgResetDebounce=0 xellLimitLive=1 rtOwnQueue=0 rtCutoutTest=0 rtOpacityMicromaps=0 rtOmmMaxSubdivision=6 rtSunUnfiltered=0 rtSunAtrousSteps=0 rtSunHistory=4 rtFoliageTransmission=0.000000 rtRadianceCache=0 rtRadianceCacheEntries=20 rtRadianceCacheCellPixels=8.000000 rtRadianceCacheMinCell=0.125000 rtRadianceCacheSamples=256 rtRadianceCacheMaxAge=50 rtRadianceCacheDebug=0 rtBlasBatch=1 rtBlasPool=1 rtBlasBudget=0 rtBlasCompaction=2 rtBlasRefineBudget=65536 rtTlasFastTrace=0 rtMegaGeometry=0 reportSeconds=1 logInputsSeconds=0 dlssDirectory=(none) amdUpscalerDirectory=(none) xessDirectory=(none) streamlineDirectory=(none) xessFgDirectory=(none)
xessfg: no Config line
```

The raytracing's frames that built 50 meshes or more, at the flight's start:

* fsr: 10 frames, 1430 meshes, 516162 triangles; the slowest 3.09 ms of the GPU, 8.65 ms of the CPU
* dlssg: 10 frames, 1326 meshes, 528768 triangles; the slowest 3.09 ms of the GPU, 4.41 ms of the CPU
* dlssg-c: 5 frames, 642 meshes, 274748 triangles; the slowest 0.61 ms of the GPU, 2.70 ms of the CPU

</details>

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
