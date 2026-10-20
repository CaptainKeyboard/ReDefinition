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
settings files, applies the graphics profile in one KSP start, and then runs one KSP start
per group of scenarios. After the last start it puts every settings file back, also when a
start failed. It appends the run to `docs/development/benchmarks/` as a JSON file and writes
the table on this page again.

The default matrix needs 14 KSP starts on an NVIDIA GPU and takes about 45 minutes. To see
the starts and phases first, without running KSP, run this:

```
python tools/benchmark_matrix.py plan
```

These options change what the tool uses:

| Option | Default | What it is |
|---|---|---|
| `--ksp` | Steam's KSP folder, or `REDEFINITION_KSP` | the KSP install |
| `--settings-template` | the install's own `settings.cfg`, as it was before the run | the mod's settings each start begins from |
| `--dlssg-package` | `.local/dlssg_x`, or `REDEFINITION_DLSSG_PACKAGE` | the unpacked DLSS-G package for RTX 20 and 30 GPUs |
| `--amd-dll-dir` | `.local/amd`, or `REDEFINITION_AMD_DLL_DIR` | the folder with `amd_fidelityfx_upscaler_dx12.dll` |
| `--raw` | `.local/benchmarks`, or `REDEFINITION_BENCH_RAW` | where each start's logs and screenshots go |
| `--only` | every start | only the starts whose id contains this text |
| `--no-profile` | the profile is applied | skip the start that applies the profile |
| `--vendor` | the vendor Windows names | the GPU vendor that decides which latency models run |

If the DLSS-G package or the AMD DLL is not there, the tool skips the starts that need it.
The run's section names what it skipped. The tool copies the DLSS-G package next to
`KSP_x64.exe` only for the starts that need it, and removes it after each one. It hands the
AMD DLL's folder to the proxy as `amdUpscalerDirectory`, so the DLL stays where it is.

To read a run's logs again, for example after a change to the parser, run this:

```
python tools/benchmark_matrix.py parse .local/benchmarks/<run>
```

To write this page again from the JSON files, run `python tools/benchmark_matrix.py render`.

## What a start measures

Each start flies the scene probe's vessel on the launch pad
(`-redefinitionProbeStay -redefinitionProbeClouds -redefinitionProbeShot`). After 30
seconds in the flight, the probe writes each set of the proxy's live keys into
`ReDefinitionProxy.ini` in turn and holds it for 12 seconds
(`-redefinitionProbeShotIni`). The proxy re-reads the file within a second and logs
`Config reloaded:` with every value.

A phase is one of these sets. Its measurement window opens 3 seconds after the proxy's
reload, so that frame generation and the latency brake settle first (`settle_seconds`).
The window closes when the probe takes the phase's screenshot. The tool reads every report
the proxy wrote inside the window. With `reportSeconds=2` that is three or four reports a
phase.

What the proxy reads only at load, and what the mod reads from `settings.cfg`, needs a
start of its own:

| Setting | Where | Why a start of its own |
|---|---|---|
| The upscaler (`technique`) | the mod's `settings.cfg` | each start's settings file is written before KSP starts |
| The raytracing mode (`raytracing`) | the mod's `settings.cfg` | the same; the probe's `-redefinitionProbeShotRaytracing` switches it only after all key sets, with the start's keys |
| Ray Reconstruction (`rayReconstruction`) | the mod's `settings.cfg` | the same |
| DLSS-G or FSR frame generation (`dlssFrameGeneration`) | `ReDefinitionProxy.ini` | the proxy reads it at load, and DLSS-G on RTX 20 and 30 GPUs needs the package next to `KSP_x64.exe` |

Frame generation on and off, and every latency model, are live keys. One start covers all
of them:

| Phase setting | Keys the probe sets |
|---|---|
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

Each list in `tools/benchmark_matrix.json` multiplies the starts or the phases:

* `techniques`: one line per upscaler. `log_name` is the name the upscaler's
  `Upscaler running on` line in `KSP.log` gives, so the tool can say when another one ran.
  A technique with `ray_reconstruction` gets a start with Ray Reconstruction on and one
  with it off for each raytracing mode other than `Off`.
* `raytracing`: the raytracing modes.
* `frame_generation`: one entry per frame generation kind, each a start of its own.
* `latency`: the latency models, each a phase. `vendors` limits one to those GPUs.
* `baseline`: the scenario every row is compared with.

To add XeSS, for example, add this line to `techniques`:

```
{"id": "xess", "name": "XeSS", "log_name": "XeSS", "settings": {"technique": "Xess"}},
```

`phase_repeats` above 1 holds each set for that many times 12 seconds, with more reports
per phase and a longer run.

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
No run yet.
<!-- benchmark-table:end -->

## The runs

Each run names its date, the commit, the build in the install, the machine and the
profile. Its collapsed table says what ran in each phase. The JSON file next to this page
holds the full record of every phase: the proxy's whole configuration as it logged it, the
mod's `settings.cfg` as the start wrote it, the upscaler's line, and each measured value.

<!-- benchmark-runs:start -->
No run yet.
<!-- benchmark-runs:end -->
