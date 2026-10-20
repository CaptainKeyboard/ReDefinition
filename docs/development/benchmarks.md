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
Run `2026-10-20-2247`. Its machine and options are under [the runs](#the-runs).

| Scenario | fps | Shown fps | Latency median ms | Latency p90 ms | fps vs baseline | Latency vs baseline | fps vs FG off, no model | Latency vs FG off, no model | fps vs previous run | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| FSR 3 · RT Off · FG off · none | 71.1 | 71.0 | 22.4 | 23.8 | +0.0 % | +0.0 ms | – | – | +1.1 % | baseline |
| FSR 3 · RT Off · FG off · Reflex | 69.7 | 69.6 | 22.4 | 23.9 | -2.0 % | +0.0 ms | -2.0 % | +0.0 ms | -2.1 % |  |
| FSR 3 · RT Off · FG off · brake 4 ms | 70.7 | 70.4 | 22.5 | 23.9 | -0.6 % | +0.1 ms | -0.6 % | +0.1 ms | -1.4 % |  |
| FSR 3 · RT Off · FG off · Reflex + brake 4 ms | 70.9 | 70.8 | 22.6 | 24.1 | -0.3 % | +0.2 ms | -0.3 % | +0.2 ms | +0.1 % |  |
| FSR 3 · RT Off · FG FSR · none | 61.3 | 122.6 | 36.8 | 39.3 | -13.8 % | +14.4 ms | -13.8 % | +14.4 ms | +0.2 % |  |
| FSR 3 · RT Off · FG FSR · Reflex | 61.4 | 122.8 | 36.8 | 39.0 | -13.6 % | +14.4 ms | -13.6 % | +14.4 ms | +0.5 % |  |
| FSR 3 · RT Off · FG FSR · brake 4 ms | 61.4 | 122.9 | 33.9 | 37.5 | -13.6 % | +11.5 ms | -13.6 % | +11.5 ms | +1.7 % |  |
| FSR 3 · RT Off · FG FSR · Reflex + brake 4 ms | 60.6 | 121.2 | 34.4 | 37.5 | -14.8 % | +12.0 ms | -14.8 % | +12.0 ms | +1.0 % |  |
| FSR 3 · RT Full · FG off · none | 44.3 | 44.2 | 58.2 | 59.7 | -37.7 % | +35.8 ms | – | – | +2.1 % | no frame time report inside the phase |
| FSR 3 · RT Full · FG off · Reflex | 44.3 | 44.1 | 58.3 | 59.8 | -37.7 % | +35.9 ms | +0.0 % | +0.1 ms | +1.6 % |  |
| FSR 3 · RT Full · FG off · brake 4 ms | 44.2 | 44.1 | 49.4 | 54.7 | -37.8 % | +27.0 ms | -0.2 % | -8.8 ms | +0.7 % |  |
| FSR 3 · RT Full · FG off · Reflex + brake 4 ms | 44.1 | 44.0 | 49.9 | 54.6 | -38.0 % | +27.5 ms | -0.5 % | -8.3 ms | +0.2 % |  |
| FSR 3 · RT Full · FG FSR · none | 37.2 | 74.6 | 66.8 | 68.5 | -47.7 % | +44.4 ms | -16.0 % | +8.6 ms | +0.3 % |  |
| FSR 3 · RT Full · FG FSR · Reflex | 37.2 | 74.5 | 66.8 | 68.7 | -47.7 % | +44.4 ms | -16.0 % | +8.6 ms | +0.3 % |  |
| FSR 3 · RT Full · FG FSR · brake 4 ms | 37.3 | 74.6 | 50.5 | 57.0 | -47.5 % | +28.1 ms | -15.8 % | -7.7 ms | +0.5 % |  |
| FSR 3 · RT Full · FG FSR · Reflex + brake 4 ms | 37.3 | 74.4 | 50.9 | 55.5 | -47.5 % | +28.5 ms | -15.8 % | -7.3 ms | +0.5 % |  |
| FSR 3 · RT Full, radiance cache · FG off · none | 44.4 | 44.6 | 57.6 | 59.3 | -37.6 % | +35.2 ms | – | – | – |  |
| FSR 3 · RT Full, radiance cache · FG FSR · none | 37.5 | 75.0 | 65.9 | 67.8 | -47.3 % | +43.5 ms | -15.5 % | +8.3 ms | – |  |
| DLSS · RT Off · FG off · none | 66.2 | 66.0 | 24.2 | 26.1 | -6.9 % | +1.8 ms | – | – | -4.6 % |  |
| DLSS · RT Off · FG off · Reflex | 69.3 | 69.1 | 24.2 | 25.9 | -2.5 % | +1.8 ms | +4.7 % | +0.0 ms | -1.4 % |  |
| DLSS · RT Off · FG off · brake 4 ms | 69.6 | 69.6 | 24.1 | 25.6 | -2.1 % | +1.7 ms | +5.1 % | -0.1 ms | +0.6 % |  |
| DLSS · RT Off · FG off · Reflex + brake 4 ms | 68.9 | 68.7 | 24.3 | 25.8 | -3.1 % | +1.9 ms | +4.1 % | +0.1 ms | -2.8 % |  |
| DLSS · RT Off · FG FSR · none | 57.2 | 114.3 | 40.4 | 42.4 | -19.5 % | +18.0 ms | -13.6 % | +16.2 ms | +0.4 % |  |
| DLSS · RT Off · FG FSR · Reflex | 57.3 | 114.6 | 40.5 | 42.5 | -19.4 % | +18.1 ms | -13.4 % | +16.3 ms | +1.1 % |  |
| DLSS · RT Off · FG FSR · brake 4 ms | 56.9 | 113.7 | 36.4 | 40.2 | -20.0 % | +14.0 ms | -14.0 % | +12.2 ms | +0.0 % |  |
| DLSS · RT Off · FG FSR · Reflex + brake 4 ms | 57.1 | 114.4 | 36.2 | 40.7 | -19.7 % | +13.8 ms | -13.7 % | +12.0 ms | +0.9 % |  |
| DLSS · RT Full, RR on · FG off · none | 35.2 | 34.9 | 75.6 | 76.9 | -50.5 % | +53.2 ms | – | – | -16.8 % |  |
| DLSS · RT Full, RR on · FG off · Reflex | 35.4 | 35.0 | 75.6 | 77.3 | -50.2 % | +53.2 ms | +0.6 % | +0.0 ms | -16.7 % |  |
| DLSS · RT Full, RR on · FG off · brake 4 ms | 35.3 | 35.0 | 63.3 | 66.3 | -50.4 % | +40.9 ms | +0.3 % | -12.3 ms | -16.7 % |  |
| DLSS · RT Full, RR on · FG off · Reflex + brake 4 ms | 35.3 | 35.0 | 63.2 | 66.2 | -50.4 % | +40.8 ms | +0.3 % | -12.4 ms | -16.5 % |  |
| DLSS · RT Full, RR on · FG FSR · none | 30.7 | 61.0 | 84.1 | 85.9 | -56.8 % | +61.7 ms | -12.8 % | +8.5 ms | -12.8 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex | 30.7 | 61.0 | 83.7 | 85.9 | -56.8 % | +61.3 ms | -12.8 % | +8.1 ms | -14.7 % |  |
| DLSS · RT Full, RR on · FG FSR · brake 4 ms | 30.6 | 61.0 | 72.9 | 75.0 | -57.0 % | +50.5 ms | -13.1 % | -2.7 ms | -15.0 % |  |
| DLSS · RT Full, RR on · FG FSR · Reflex + brake 4 ms | 30.6 | 61.0 | 73.0 | 74.9 | -57.0 % | +50.6 ms | -13.1 % | -2.6 ms | -15.2 % |  |
| DLSS · RT Full, RR off · FG off · none | 42.5 | 42.8 | 60.7 | 62.3 | -40.2 % | +38.3 ms | – | – | +0.7 % |  |
| DLSS · RT Full, RR off · FG off · Reflex | 42.6 | 43.0 | 60.7 | 62.3 | -40.1 % | +38.3 ms | +0.2 % | +0.0 ms | +0.5 % |  |
| DLSS · RT Full, RR off · FG off · brake 4 ms | 42.6 | 42.8 | 49.3 | 56.3 | -40.1 % | +26.9 ms | +0.2 % | -11.4 ms | +0.7 % |  |
| DLSS · RT Full, RR off · FG off · Reflex + brake 4 ms | 42.4 | 42.6 | 49.8 | 56.2 | -40.4 % | +27.4 ms | -0.2 % | -10.9 ms | +0.2 % |  |
| DLSS · RT Full, RR off · FG FSR · none | 36.1 | 72.0 | 69.5 | 71.4 | -49.2 % | +47.1 ms | -15.1 % | +8.8 ms | +0.6 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex | 36.1 | 72.0 | 69.8 | 71.5 | -49.2 % | +47.4 ms | -15.1 % | +9.1 ms | +0.6 % |  |
| DLSS · RT Full, RR off · FG FSR · brake 4 ms | 36.1 | 72.0 | 57.3 | 60.1 | -49.2 % | +34.9 ms | -15.1 % | -3.4 ms | +0.6 % |  |
| DLSS · RT Full, RR off · FG FSR · Reflex + brake 4 ms | 36.1 | 72.0 | 57.7 | 60.4 | -49.2 % | +35.3 ms | -15.1 % | -3.0 ms | +0.6 % |  |
| AMD DLL · RT Off · FG off · none | 67.9 | 67.6 | 23.9 | 25.5 | -4.5 % | +1.5 ms | – | – | -1.5 % |  |
| AMD DLL · RT Off · FG off · Reflex | 69.9 | 70.0 | 23.8 | 25.3 | -1.7 % | +1.4 ms | +2.9 % | -0.1 ms | -0.4 % |  |
| AMD DLL · RT Off · FG off · brake 4 ms | 69.5 | 69.4 | 23.8 | 25.3 | -2.3 % | +1.4 ms | +2.4 % | -0.1 ms | -1.3 % |  |
| AMD DLL · RT Off · FG off · Reflex + brake 4 ms | 69.2 | 69.1 | 24.0 | 25.4 | -2.7 % | +1.6 ms | +1.9 % | +0.1 ms | -0.9 % |  |
| AMD DLL · RT Off · FG FSR · none | 58.2 | 116.1 | 40.0 | 42.0 | -18.1 % | +17.6 ms | -14.3 % | +16.1 ms | +0.2 % |  |
| AMD DLL · RT Off · FG FSR · Reflex | 58.1 | 116.2 | 39.8 | 41.8 | -18.3 % | +17.4 ms | -14.4 % | +15.9 ms | +0.3 % |  |
| AMD DLL · RT Off · FG FSR · brake 4 ms | 57.8 | 115.6 | 36.5 | 40.1 | -18.7 % | +14.1 ms | -14.9 % | +12.6 ms | -0.3 % |  |
| AMD DLL · RT Off · FG FSR · Reflex + brake 4 ms | 58.3 | 116.5 | 36.5 | 40.3 | -18.0 % | +14.1 ms | -14.1 % | +12.6 ms | +1.6 % |  |
| AMD DLL · RT Full · FG off · none | 42.4 | 42.4 | 61.0 | 62.5 | -40.4 % | +38.6 ms | – | – | +0.2 % |  |
| AMD DLL · RT Full · FG off · Reflex | 42.5 | 42.8 | 61.2 | 62.8 | -40.2 % | +38.8 ms | +0.2 % | +0.2 ms | +0.2 % |  |
| AMD DLL · RT Full · FG off · brake 4 ms | 42.5 | 42.6 | 49.9 | 55.8 | -40.2 % | +27.5 ms | +0.2 % | -11.1 ms | +0.5 % |  |
| AMD DLL · RT Full · FG off · Reflex + brake 4 ms | 42.5 | 42.6 | 49.8 | 56.8 | -40.2 % | +27.4 ms | +0.2 % | -11.2 ms | +0.5 % |  |
| AMD DLL · RT Full · FG FSR · none | 36.1 | 72.1 | 69.8 | 71.4 | -49.2 % | +47.4 ms | -14.9 % | +8.8 ms | +4.3 % |  |
| AMD DLL · RT Full · FG FSR · Reflex | 36.1 | 72.0 | 69.3 | 71.1 | -49.2 % | +46.9 ms | -14.9 % | +8.3 ms | +9.1 % |  |
| AMD DLL · RT Full · FG FSR · brake 4 ms | 36.1 | 72.0 | 55.0 | 57.7 | -49.2 % | +32.6 ms | -14.9 % | -6.0 ms | +3.7 % |  |
| AMD DLL · RT Full · FG FSR · Reflex + brake 4 ms | 36.0 | 71.9 | 54.9 | 58.6 | -49.4 % | +32.5 ms | -15.1 % | -6.1 ms | +0.0 % |  |
| XeSS · RT Off · FG off · none | 68.6 | 68.4 | 24.9 | 26.6 | -3.5 % | +2.5 ms | – | – | +1.8 % |  |
| XeSS · RT Off · FG off · Reflex | 68.7 | 68.7 | 24.9 | 26.6 | -3.4 % | +2.5 ms | +0.1 % | +0.0 ms | -0.7 % |  |
| XeSS · RT Off · FG off · brake 4 ms | 69.1 | 68.9 | 24.8 | 26.4 | -2.8 % | +2.4 ms | +0.7 % | -0.1 ms | -0.6 % |  |
| XeSS · RT Off · FG off · Reflex + brake 4 ms | 69.3 | 69.1 | 24.7 | 26.4 | -2.5 % | +2.3 ms | +1.0 % | -0.2 ms | +1.5 % |  |
| XeSS · RT Off · FG FSR · none | 56.1 | 112.1 | 41.3 | 43.6 | -21.1 % | +18.9 ms | -18.2 % | +16.4 ms | +0.0 % |  |
| XeSS · RT Off · FG FSR · Reflex | 56.3 | 112.4 | 42.2 | 43.8 | -20.8 % | +19.8 ms | -17.9 % | +17.3 ms | +0.4 % |  |
| XeSS · RT Off · FG FSR · brake 4 ms | 56.2 | 112.2 | 37.6 | 41.8 | -21.0 % | +15.2 ms | -18.1 % | +12.7 ms | +0.4 % |  |
| XeSS · RT Off · FG FSR · Reflex + brake 4 ms | 56.2 | 112.3 | 37.7 | 41.6 | -21.0 % | +15.3 ms | -18.1 % | +12.8 ms | +0.2 % |  |
| XeSS · RT Full · FG off · none | 41.0 | 40.9 | 63.4 | 64.8 | -42.3 % | +41.0 ms | – | – | +0.2 % |  |
| XeSS · RT Full · FG off · Reflex | 41.1 | 41.0 | 63.3 | 64.8 | -42.2 % | +40.9 ms | +0.2 % | -0.1 ms | +0.5 % |  |
| XeSS · RT Full · FG off · brake 4 ms | 41.1 | 41.0 | 50.8 | 57.6 | -42.2 % | +28.4 ms | +0.2 % | -12.6 ms | +0.7 % |  |
| XeSS · RT Full · FG off · Reflex + brake 4 ms | 41.0 | 41.0 | 50.2 | 58.1 | -42.3 % | +27.8 ms | +0.0 % | -13.2 ms | +0.7 % |  |
| XeSS · RT Full · FG FSR · none | 35.0 | 70.0 | 71.5 | 73.5 | -50.8 % | +49.1 ms | -14.6 % | +8.1 ms | +0.3 % |  |
| XeSS · RT Full · FG FSR · Reflex | 35.0 | 69.9 | 71.8 | 73.7 | -50.8 % | +49.4 ms | -14.6 % | +8.4 ms | +0.6 % |  |
| XeSS · RT Full · FG FSR · brake 4 ms | 35.0 | 69.8 | 59.8 | 62.1 | -50.8 % | +37.4 ms | -14.6 % | -3.6 ms | +0.9 % |  |
| XeSS · RT Full · FG FSR · Reflex + brake 4 ms | 34.9 | 69.9 | 60.0 | 62.6 | -50.9 % | +37.6 ms | -14.9 % | -3.4 ms | +0.6 % |  |
| FSR 3 · RT Full, opacity micromaps · FG off · none | 39.7 | 39.9 | 65.7 | 67.3 | -44.2 % | +43.3 ms | – | – | – |  |
| FSR 3 · RT Full, opacity micromaps · FG FSR · none | 34.0 | 68.0 | 74.0 | 75.7 | -52.2 % | +51.6 ms | -14.4 % | +8.3 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G · none | 66.2 | 132.6 | 47.4 | 49.7 | -6.9 % | +25.0 ms | -6.9 % | +25.0 ms | +0.2 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex | 65.8 | 131.6 | 47.2 | 49.5 | -7.5 % | +24.8 ms | -7.5 % | +24.8 ms | +2.7 % |  |
| FSR 3 · RT Off · FG DLSS-G · brake 4 ms | 66.1 | 132.4 | 47.1 | 49.7 | -7.0 % | +24.7 ms | -7.0 % | +24.7 ms | +0.0 % |  |
| FSR 3 · RT Off · FG DLSS-G · Reflex + brake 4 ms | 65.8 | 131.5 | 47.3 | 49.7 | -7.5 % | +24.9 ms | -7.5 % | +24.9 ms | -0.6 % |  |
| FSR 3 · RT Full · FG DLSS-G · none | 40.5 | 80.9 | 71.6 | 73.3 | -43.0 % | +49.2 ms | -8.6 % | +13.4 ms | +0.7 % | no frame time report inside the phase |
| FSR 3 · RT Full · FG DLSS-G · Reflex | 40.6 | 81.0 | 71.7 | 73.5 | -42.9 % | +49.3 ms | -8.4 % | +13.5 ms | +0.7 % |  |
| FSR 3 · RT Full · FG DLSS-G · brake 4 ms | 40.5 | 81.0 | 72.2 | 73.9 | -43.0 % | +49.8 ms | -8.6 % | +14.0 ms | +0.5 % |  |
| FSR 3 · RT Full · FG DLSS-G · Reflex + brake 4 ms | 40.4 | 81.0 | 72.1 | 74.0 | -43.2 % | +49.7 ms | -8.8 % | +13.9 ms | +0.5 % |  |
| FSR 3 · RT Full, radiance cache · FG DLSS-G · none | 39.9 | 79.9 | 72.7 | 74.5 | -43.9 % | +50.3 ms | -10.1 % | +15.1 ms | – |  |
| DLSS · RT Off · FG DLSS-G · none | 61.0 | 122.0 | 51.8 | 53.7 | -14.2 % | +29.4 ms | -7.9 % | +27.6 ms | -0.7 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex | 61.1 | 122.4 | 52.1 | 53.9 | -14.1 % | +29.7 ms | -7.7 % | +27.9 ms | -0.3 % |  |
| DLSS · RT Off · FG DLSS-G · brake 4 ms | 61.0 | 122.2 | 51.8 | 53.6 | -14.2 % | +29.4 ms | -7.9 % | +27.6 ms | -0.5 % |  |
| DLSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 61.0 | 122.0 | 51.7 | 53.5 | -14.2 % | +29.3 ms | -7.9 % | +27.5 ms | -1.0 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · none | 32.0 | 63.8 | 91.0 | 93.2 | -55.0 % | +68.6 ms | -9.1 % | +15.4 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex | 32.2 | 64.1 | 91.2 | 92.9 | -54.7 % | +68.8 ms | -8.5 % | +15.6 ms | +0.0 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · brake 4 ms | 32.2 | 64.2 | 91.2 | 92.9 | -54.7 % | +68.8 ms | -8.5 % | +15.6 ms | +0.3 % |  |
| DLSS · RT Full, RR on · FG DLSS-G · Reflex + brake 4 ms | 32.2 | 64.2 | 90.7 | 92.7 | -54.7 % | +68.3 ms | -8.5 % | +15.1 ms | +0.0 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · none | 38.3 | 76.6 | 75.8 | 77.7 | -46.1 % | +53.4 ms | -9.9 % | +15.1 ms | -0.3 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex | 38.5 | 77.0 | 75.9 | 77.6 | -45.9 % | +53.5 ms | -9.4 % | +15.2 ms | +0.0 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · brake 4 ms | 38.5 | 77.0 | 75.8 | 77.6 | -45.9 % | +53.4 ms | -9.4 % | +15.1 ms | +0.0 % |  |
| DLSS · RT Full, RR off · FG DLSS-G · Reflex + brake 4 ms | 38.5 | 77.0 | 75.6 | 77.5 | -45.9 % | +53.2 ms | -9.4 % | +14.9 ms | +0.0 % |  |
| AMD DLL · RT Off · FG DLSS-G · none | 62.0 | 123.9 | 50.5 | 52.7 | -12.8 % | +28.1 ms | -8.7 % | +26.6 ms | -0.8 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex | 62.5 | 125.1 | 50.7 | 52.6 | -12.1 % | +28.3 ms | -8.0 % | +26.8 ms | +1.0 % |  |
| AMD DLL · RT Off · FG DLSS-G · brake 4 ms | 62.5 | 125.0 | 51.0 | 52.7 | -12.1 % | +28.6 ms | -8.0 % | +27.1 ms | -0.3 % |  |
| AMD DLL · RT Off · FG DLSS-G · Reflex + brake 4 ms | 62.5 | 125.0 | 50.5 | 52.7 | -12.1 % | +28.1 ms | -8.0 % | +26.6 ms | +2.1 % |  |
| AMD DLL · RT Full · FG DLSS-G · none | 38.7 | 77.3 | 75.5 | 77.1 | -45.6 % | +53.1 ms | -8.7 % | +14.5 ms | +0.0 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex | 38.9 | 77.6 | 75.4 | 77.0 | -45.3 % | +53.0 ms | -8.3 % | +14.4 ms | +0.0 % |  |
| AMD DLL · RT Full · FG DLSS-G · brake 4 ms | 38.9 | 77.7 | 75.5 | 77.0 | -45.3 % | +53.1 ms | -8.3 % | +14.5 ms | +0.3 % |  |
| AMD DLL · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.9 | 77.6 | 75.5 | 77.0 | -45.3 % | +53.1 ms | -8.3 % | +14.5 ms | +0.0 % |  |
| XeSS · RT Off · FG DLSS-G · none | 60.2 | 120.3 | 52.5 | 54.5 | -15.3 % | +30.1 ms | -12.2 % | +27.6 ms | +1.7 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex | 60.3 | 120.7 | 52.6 | 54.4 | -15.2 % | +30.2 ms | -12.1 % | +27.7 ms | -0.8 % |  |
| XeSS · RT Off · FG DLSS-G · brake 4 ms | 60.4 | 120.6 | 52.5 | 54.4 | -15.0 % | +30.1 ms | -12.0 % | +27.6 ms | -0.5 % |  |
| XeSS · RT Off · FG DLSS-G · Reflex + brake 4 ms | 60.3 | 120.8 | 52.6 | 54.3 | -15.2 % | +30.2 ms | -12.1 % | +27.7 ms | -0.8 % |  |
| XeSS · RT Full · FG DLSS-G · none | 37.8 | 75.5 | 76.9 | 78.9 | -46.8 % | +54.5 ms | -7.8 % | +13.5 ms | -0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex | 38.0 | 75.9 | 77.2 | 78.8 | -46.6 % | +54.8 ms | -7.3 % | +13.8 ms | +0.0 % |  |
| XeSS · RT Full · FG DLSS-G · brake 4 ms | 37.9 | 75.8 | 76.8 | 78.8 | -46.7 % | +54.4 ms | -7.6 % | +13.4 ms | -0.3 % |  |
| XeSS · RT Full · FG DLSS-G · Reflex + brake 4 ms | 38.0 | 75.9 | 77.2 | 78.7 | -46.6 % | +54.8 ms | -7.3 % | +13.8 ms | +0.0 % |  |
| FSR 3 · RT Full, opacity micromaps · FG DLSS-G · none | 37.1 | 74.5 | 78.2 | 80.0 | -47.8 % | +55.8 ms | -6.5 % | +12.5 ms | – |  |
| FSR 3 · RT Off · FG DLSS-G preset C · none | 70.6 | 70.6 | 22.3 | 23.6 | -0.7 % | -0.1 ms | -0.7 % | -0.1 ms | – | frame generation asked for, presented/rendered 1.00 |
| FSR 3 · RT Full · FG DLSS-G preset C · none | 44.9 | 44.8 | 55.6 | 57.2 | -36.8 % | +33.2 ms | +1.4 % | -2.6 ms | – | frame generation asked for, presented/rendered 1.00 |
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's settings in effect, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
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
