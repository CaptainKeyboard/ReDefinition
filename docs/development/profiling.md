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
query at each one (`src/DxgiProxy/GpuProfiler.h`). A segment is the GPU time from one
marker to the next. The markers sit around the drawing scripts of every assembly, at
each camera's start, around the `Update` and `LateUpdate` of the graphics mods, inside
ReDefinition's own raytracing and upscaler work (`Probe.GpuProfiler.Mark`), and in the
proxy around its copies and waits.

With `-redefinitionGpuProfileCameras` a marker also ends each event of every camera and
light. Such a marker leaves the raytraced sun out of the frame, so the scene looks as
if in shadow. The GPU does the same work, so the times hold, but a shot from such a run
does not. Why the markers do this is not known.

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

The variants are `cascades2`, `distanceHalf`, `shadowsMinimal`, `noDeferredAo`,
`noTufxAo`, `portraitLayers` and `portraitShadows` (`src/Probe/ShotVariant.cs`). Each
changes its one thing 5 s into the flight, so the perf monitor's and the GPU profiler's
reports after it measure the change as well.

Keep in mind:

* The grass, the clouds and the raytracing's noise change from shot to shot. Compare
  after a blur, and against the difference of the two `none` shots.
* A shot without `-redefinitionProbeShotVariant` comes 2 s earlier, and the clouds have
  moved by then. Compare with `none` only.

## Read the proxy's waits and slow frames

The proxy writes these into `ReDefinitionProxy.log` on its own, every 10 s:

* Under each `D3D12 proxy:` line, `The render thread waited, per frame:` names how long
  Unity's render thread waited at the proxy's CPU waits. They are a frame in flight, a
  raytracing slot, and frame generation's inputs (`src/DxgiProxy/WaitStats.h`).
* `Raytracing: a slow frame` names a raytracing frame over 15 ms of GPU time with its
  scene build in parts and each pass. It is said at most 30 times a session.
* The raytracing's status line in `KSP.log` gives `extra build` and `extra trace`, the
  ocean's trace timed apart from the frame's.
* `Raytracing: <n> meshes built in one frame` names a frame that builds 50 meshes or
  more, with their triangles, vertices and largest mesh.

## Measure frame generation

Frame generation only generates frames while the KSP window has the focus. If the line
`D3D12 proxy:` says `presented/rendered 1.00`, the run measured nothing of it. Start
the run and leave the computer alone until KSP ends.
