# Measure where the frame's time goes

**For:** anyone who wants to know what costs KSP's frame, in ReDefinition, in KSP or in
another mod.
**You need:** the repository built and deployed, KSP with the proxy, and the save
`ReDefinitionProbe` that the scene probe flies.
**You get:** each measuring tool, how to start it, where its result lands, and where it
misleads.

What the tools found is on
[performance-findings.md](performance-findings.md).

## Pick the tool for the question

| Question | Tool | Result |
|---|---|---|
| Which camera, pass or mod's command buffer costs the GPU how much? | the GPU profiler | `KSP.log` |
| Which script method costs the main thread how much? | the perf monitor | `KSP.log` |
| What does a whole mod cost, including its shaders inside Unity's passes? | the mod cost probe | `KSP.log` |
| Which shader costs the GPU how much, by mod? | RenderDoc and `tools/renderdoc_gpu_times.py` | a report file |
| Where does Unity's own main thread time go: culling, rendering, physics, coroutines? | Unity's development player | `KSP.log` and a `.raw` file |
| Where does the main thread spend its time in native code: Unity's player, PhysX, the driver? | the CPU sampler and `tools/cpu_samples.py` | a report file |
| How long does Unity's render thread wait for the GPU? | the proxy's wait report | `ReDefinitionProxy.log` |
| What does the proxy's raytracing do in a slow frame? | the proxy's slow frame lines | `ReDefinitionProxy.log` |
| Does the image change if one thing is switched off? | a shot variant | a screenshot to compare |

All of them run through the scene probe. It loads `ReDefinitionProbe`, flies its vessel
on the launch pad and, with `-redefinitionProbeStay`, leaves the flight running. Add
`-redefinitionProbeClouds` to keep EVE's clouds, which the probe hides otherwise. Add
`-redefinitionProbeShot <name>` to end KSP 30 s into the flight, after a screenshot.

Close KSP before you start one. The tools write into the KSP folder, and two KSP
processes would share the logs.

## Run the GPU profiler

1. Start KSP with the probe and `-redefinitionGpuProfile`:

   ```
   KSP_x64.exe -redefinitionProbe -redefinitionProbeStay -redefinitionProbeClouds -redefinitionGpuProfile -redefinitionProbeShot gpu
   ```

2. Read the lines `GPU profiler:` in `KSP.log`. Every 10 s one block names the GPU's
   frame time, each camera's or light's share and the 45 costliest segments.
3. Read the lines `GPU profiler: a spike` for single frames over 33 ms and twice the
   usual time. Each names its five costliest segments.

The profiler sets markers in Unity's render stream. The proxy ends a D3D11 timestamp
query at each one (`src/DxgiProxy/Diagnostics/GpuProfiler.h`). A segment is the GPU time from one
marker to the next. The markers sit around the drawing scripts of every assembly, at
each camera's start, around the `Update` and `LateUpdate` of the graphics mods, inside
ReDefinition's own raytracing and upscaler work (`Probe.GpuProfiler.Mark`), and in the
proxy around its copies and waits.

A marker also ends each event of every camera and light, except the camera Unity
renders a reflection probe with. A command buffer on that camera leaves KSP's
reflection probe out, and the raytraced scene goes dark without it. Use
`-redefinitionGpuProfileNoCameras` for no markers on the cameras and lights at all.

The same labels go to RenderDoc as annotation markers, so a capture shows the same
segments.

Keep in mind:

* D3D11 timestamps mark when the GPU reaches the marker, not when earlier work ends.
  At a sync point, such as a wait for D3D12, work from earlier segments drains, and the
  segment there looks longer than it is.
* The first segment of a frame runs from the previous `Present`. It includes the time
  the GPU waits for the CPU.
* The markers cost under 1 ms a frame. Markers on every `Update` of every assembly cost
  7 ms, which is why only the graphics mods get them.

## Run the perf monitor

1. Start KSP with `-redefinitionPerfMonitor`, and add `-redefinitionPerfMonitorAll` for
   every assembly instead of ReDefinition alone.
2. Read the lines `Perf monitor:` in `KSP.log`. Every 10 s one block names each timed
   method's time a frame, its largest single frame and its calls, and KSP's physics and
   update phases.

With `-redefinitionPerfMonitorAll` the monitor also times each step of every
coroutine, named by its class and method, every mod's camera event handlers, and KSP's
crew portraits with what runs inside them. Unity sums all coroutines as one,
`CoroutinesDelayedCalls`. Use `-redefinitionPerfMonitorTypes A,B` to time every method
of ReDefinition's classes `A` and `B`.

Keep in mind:

* Each timed call costs a little. A method called a thousand times a frame looks
  dearer than it is.
* A method's largest frame can include a garbage collection that happened to start
  inside it.

## Run the mod cost probe

1. Start KSP with the probe, the GPU profiler and `-redefinitionProbeModCost`, without
   `-redefinitionProbeShot`:

   ```
   KSP_x64.exe -redefinitionProbe -redefinitionProbeStay -redefinitionProbeClouds -redefinitionGpuProfile -redefinitionProbeModCost
   ```

2. Wait about 8 minutes. KSP ends by itself.
3. Read the block `Mod cost` in `KSP.log`.

The probe switches each target off and on in turn: on, off, on, off, on. It measures
5 s after 3 s to settle, and compares the off windows with the on windows around them.
A mod's target is its behaviours, the renderers that draw with its shaders and its
command buffers. The other targets are KSP's crew portraits, the shadow distance halved
and two shadow cascades instead of four (`src/Probe/ModCost.cs`).

Keep in mind:

* The probe cannot switch off a mod's shaders where the mod replaces KSP's own, as
  Deferred does. Scatterer's sky and ocean are not reached either.
* Switching a mod off and on mid-flight is nothing a game does. Each row says how many
  exceptions it caused meanwhile.
* The GPU column counts from `Present` to `Present`. If the CPU limits the frame, a
  target that only costs the CPU shows in the main thread column instead.

## Capture a frame with RenderDoc

1. Run:

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\renderdoc_probe.ps1 -Stay
   ```

   KSP runs under RenderDoc, flies for 25 s and captures one frame into
   `ReDefinitionCaptures\renderdoc_frame<n>.rdc`.
2. Run `tools/renderdoc_gpu_times.py` in RenderDoc's Python, as its first lines say.
   The report sums the GPU time by mod, by marker, by shader and by render target.

The script takes the mod from the start of the shader's name (`MODS` in the script).

Keep in mind:

* RenderDoc runs without the proxy. The capture has no raytracing, no DLSS and no
  frame generation.
* RenderDoc times each draw alone, with the GPU idle around it. Many small draws come
  out dearer than in the game. The report gives the time a draw for this reason.

## Run Unity's development player

1. Run:

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity_profile.ps1
   ```

2. Read the block `Unity's samplers` in `KSP.log`. It names the main thread's time in
   each of Unity's samplers, such as `Culling`, `Camera.Render`, `Physics.Processing`
   and `CoroutinesDelayedCalls`.
3. For Unity's own view, open the editor's *Profiler* window, choose *Load* and open
   `ReDefinitionCaptures\unity-profile.raw`.

KSP's release player has none of Unity's samplers. The script puts the development
player of the same Unity version, 2019.4.18f1, in place of KSP's `UnityPlayer.dll` for
one run and puts KSP's back afterwards. It takes the player from the editor's Windows
support, `Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono`.

Keep in mind:

* The development player refuses Unity calls in a MonoBehaviour's constructor, which
  the release player lets pass. Mods that make such calls throw there and not in the
  game. HUDReplacer's GUI skins do.
* A recording of 30 s takes about 3 GB.

## Sample the main thread in native code

1. Start KSP with the probe and `-redefinitionProbeCpuSample`:

   ```
   KSP_x64.exe -redefinitionProbe -redefinitionProbeStay -redefinitionProbeClouds -redefinitionProbeCpuSample
   ```

   From 20 s into the flight, the proxy samples the main thread for 30 s into
   `ReDefinitionCaptures\<time>-run\cpu-samples.txt`. KSP then ends by itself.
2. Run `python tools/cpu_samples.py <that file> [report.txt]`.

The sampler is a thread of the proxy's (`src/DxgiProxy/Latency/CpuSampler.h`). About a thousand
times a second it stops the main thread for a few microseconds and copies its
instruction pointer and the top of its stack. The script names the addresses with the
symbols of Unity's and Microsoft's symbol servers, cached in
`%LOCALAPPDATA%\ReDefinition\symbols`. The report gives the time by module and by
function where the thread was, and by function anywhere on its stack.

Keep in mind:

* The stack copy holds stale return addresses as well. A function's share anywhere on
  the stack is an upper bound.
* Code in no module is Mono's compiled C#, KSP's and the mods' scripts. The perf monitor
  times those by method.

## Compare a shot with one thing changed

1. Take the shot to compare with, with the variant `none`, which waits as long as a
   change does:

   ```
   KSP_x64.exe -redefinitionProbe -redefinitionProbeStay -redefinitionProbeClouds -redefinitionProbeShot v-base -redefinitionProbeShotVariant none
   ```

2. Take the same shot with a variant, for example `-redefinitionProbeShot v-cascades2
   -redefinitionProbeShotVariant cascades2`.
3. Take a second shot with `none`. The difference between the two `none` shots is the
   noise of the scene.
4. Compare the shots, and look at them side by side.

The variants are in `src/Probe/ShotVariant.cs`:

| Variant | What it changes |
|---|---|
| `cascades2` | two shadow cascades instead of the quality level's |
| `cascades1Low` | one cascade at low resolution, the distance kept |
| `distanceHalf` | the shadow distance halved |
| `shadowsMinimal` | one cascade, 200 m, low resolution |
| `noDeferredAo` | Deferred's ambient occlusion command buffers taken off the cameras |
| `noTufxAo` | TUFX's ambient occlusion off in its volumes' profiles |
| `portraitLayers` | the crew portraits' cameras draw only their interiors' and kerbals' layers |
| `portraitShadows` | the shadow distance 10 m while a crew portrait renders |
| `noSun` | KSP's sun lights at intensity 0 |
| `noLocalLights` | every point and spot light off, each logged |
| `detailAsDiffuse` | `KSP/Scenery/Diffuse Detail` drawn with `KSP/Scenery/Diffuse` |
| `flagProbes` | the renderers named flag set to blend the light probes |
| `noFlags` | the renderers named flag hidden |

Each changes its one thing 5 s into the flight, so the perf monitor's and the GPU
profiler's reports after it measure the change as well.

Some comparisons need no second run. Each takes the same view 10 s after its change:

* `-redefinitionProbeShotRaytracing <mode,mode,...>` switches the raytracing to each mode
  in turn, as the settings window does, and saves `<shot>-<mode>.png`.
* `-redefinitionProbeShotIni "key=value,key=value;key=value"` applies each set in turn
  and saves `<shot>-ini<n>.png` 12 s later. A key is one of the proxy's live keys, which
  the probe writes into `ReDefinitionProxy.ini`; the proxy reads the file again within a
  second. A key `mod.<key>` is one of ReDefinition's own settings, by its `settings.cfg`
  key, such as `mod.technique=Dlss` or `mod.raytracing=Full`, applied as the settings
  window applies it. The values as they were go back at the end.
* `-redefinitionProbeSettingsWindow <tab>` opens the settings window on that tab before
  each of those shots, built anew so that it shows the set.
* `-redefinitionProbeDiagnostics "<name>@<commands>|<name>@<commands>"` opens the
  diagnostics window and shoots it after each step as `diagnostics-<name>.png`, then ends
  KSP. The commands are separated by commas: `show`, `hide`, `category=<page>`,
  `list=<state|registrations|code>`, `search=<text>`, `changed=<1|0>`, `pin=<id>`,
  `set=<id>:<value>`, `technique=<name>`, `scroll=<pixels>` and `reset`.
* `-redefinitionProbeStagesAt <renderer>[@<parent>]` logs the scene camera's image after
  each of its stages at the middle of the nearest renderer so named.
* `-redefinitionProbeLightBuffer` saves the light buffer after the G-buffer and before
  the lights: what each surface brings into the lighting of its own.
* `-redefinitionProbeMaterials <part of a name>` logs the renderers so named with their
  materials' shaders, queues and keywords.

Keep in mind:

* The grass, the clouds and the raytracing's noise change from shot to shot. Compare
  after a blur, and against the difference of the two `none` shots.
* A shot without `-redefinitionProbeShotVariant` comes 2 s earlier, and the clouds have
  moved by then. Compare with `none` only.
* `-redefinitionProbeBurst <n>` takes n frames one after the other after the shot, for
  what changes from frame to frame.
* `-redefinitionProbeDays <n>` starts n of Kerbin's days later. At the launch pad, 0.05
  gives a low evening sun, 0.15 and later the night.
* `-redefinitionProbeVessel Acapello` flies the save's vessel in orbit.

## Read the proxy's waits and slow frames

The proxy writes these into `ReDefinitionProxy.log` on its own, every 10 s:

* Under each `D3D12 proxy:` line, `The render thread waited, per frame:` names how long
  Unity's render thread waited at the proxy's CPU waits. They are a frame in flight, a
  raytracing slot, and frame generation's inputs (`src/DxgiProxy/Latency/WaitStats.h`).
* `Raytracing: a slow frame` names a raytracing frame over 15 ms of GPU time with its
  scene build in parts and each pass. It is said at most 30 times a session.
* The raytracing's status line in `KSP.log` gives `extra build` and `extra trace`, the
  ocean's trace timed apart from the frame's.
* `Raytracing: <n> meshes built in one frame` names a frame that builds 50 meshes or
  more, with their triangles, vertices and largest mesh.

## Measure frame generation

DLSS frame generation only generates frames while the KSP window is in the foreground. If
the line `D3D12 proxy:` says `presented/rendered 1.00`, the run measured nothing of it.
The proxy's log then says that the game's window is not in the foreground. Start the run
and leave the computer alone until KSP ends. `tools/benchmark_matrix.py` keeps the window
in front for you, and the machine must still not be used while it runs
([benchmarks.md](benchmarks.md)).

Two probe arguments serve such runs:

* `-redefinitionProbeFpsCap <n>` caps the rendered frames at n a second and turns V-Sync
  off, so that all of frame generation's frames fit the display's refresh rate and the
  desktop recording sees them.
* `-redefinitionProbeShowHud` keeps KSP's interface in the frames, which the probe hides
  otherwise.

**What frame generation does to the HUD.** `tools/hud_probe.py` starts KSP once per case
of a DLSS-G configuration, the package's preset and the proxy's keys. It flies the launch
pad with the HUD showing and the camera sweeping (`-redefinitionProbeShowHud`,
`-redefinitionProbeFpsCap`), and records the monitor's frames, the generated ones
included. Python 3 with numpy and Pillow runs it:

```
python tools/hud_probe.py run <out folder> [--cases auto,c,...]
python tools/hud_probe.py analyze <case folder>... [--regions ...]
python tools/hud_probe.py crops <case folder> <out folder>
```

`run` copies the install's settings files before it and puts them back after it, as the
benchmark does. `analyze` compares the HUD's regions of each generated frame with the
rendered frames either side of it, and `crops` writes side-by-side crops of the HUD. The
results are in [frame-generation.md](frame-generation.md#dlss-frame-generation-and-ksps-hud).
