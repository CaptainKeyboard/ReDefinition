# Low latency

**For:** anyone changing ReDefinition's latency code.
**You need:** the repository. The code is in `src/DxgiProxy/LowLatency.cpp` and
`src/Bridges/ReflexFrames.cs`.
**You get:** what lowers the latency on each GPU, why a brake in the proxy works where
Reflex does not, how the latency is measured, and what was measured.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## What lowers the latency

`LowLatency` picks one of these for the game's frames, and says which in the settings
window's *Low latency* row and the log (`LowLatency::Describe`):

| Kind | What | Runs |
|---|---|---|
| 1 | NVIDIA Reflex through NVAPI on the proxy's Direct3D 12 device | on an NVIDIA GPU where Streamline's Reflex does not run: frame generation off, FSR 3's, Ray Reconstruction alone |
| 2 | Streamline's Reflex | with DLSS frame generation, which requires it |
| 3 | AMD Anti-Lag 2 | on a Radeon RX 5000 or newer, where `antiLag2` is 12 or 11 |
| 4 | Intel XeLL | with XeSS frame generation, which requires it; alone on an Intel GPU, where `xell` is 1 |
| 0 | none | the text says why |

The frame's start, the end of its simulation and the first Direct3D call of its render
thread reach `LowLatency` through the exports and the render event that Streamline's
Reflex uses too (`ReflexFrames`, `KspReflexFrameStart`). The Present comes from the
swapchain proxy. Sleep and the markers go on every frame whether low latency mode is on or
not, as NVIDIA and Intel ask **[doc]**. XeLL's markers are Reflex's, at the same points and
with the same values.

**One at a time.** Intel: "Latency reduction solutions other than XeLL are not supported
and may cause unexpected behavior" (XeSS-FG developer guide, Requirements). AMD asks the
same of Anti-Lag 2: "Any other forms of latency reduction ... should be disabled"
**[doc]**. While XeLL's low latency mode is on, Reflex, Anti-Lag 2 and the brake are off.
With Anti-Lag 2 on, the brake is off.

**XeLL on a GPU other than Intel's** lowers the latency only while XeSS frame generation
generates: "If the condition is not fulfilled then XeLL will not perform Latency reduction"
**[doc]**. While XeSS frame generation is held off, the brake takes over. It is held off for
its minimum frame rate, by the mod's switch or for a missing present id. The brake steps back
when it generates again. The log says each hand-over, at most every 10 seconds, with the
changes counted. Reflex and Anti-Lag 2 stay off under XeSS frame generation's swapchain.

**V-Sync.** XeLL's low latency mode needs a sync interval of 1: "when VSync is enabled,
sync interval for IDXGISwapChain::Present must always be 1 when Low Latency is active"
**[doc]**. The present path clamps it, and KSP's registration holds the setting the same
way (R8 in [reference/requirements.md](../reference/requirements.md)).

## Why the brake works

**[meas]** Reflex lowers nothing in this pipeline. Unity renders on Direct3D 11 and the
proxy presents on Direct3D 12. The frames queue up in the proxy's own waits, not in the
driver's queue, which is the one Reflex sees. With raytracing on *Full*, the wait is for a
raytracing slot, a frame's allocator, or the Present. On an RTX A4500 the latency at that
load was 58 ms with Reflex's mode on or off, on either device. Streamline's Reflex under
DLSS frame generation changed nothing either.

The brake (`LowLatency::Brake`) puts the wait where it costs latency least. It runs on
the main thread at the start of each frame, before the game reads its input:

1. It adds up the time the render thread waited in the proxy: the CPU waits of
   `WaitStats.h`, and with `latencyBrakeWaits=1` the Present's.
2. It sleeps, on a high-resolution waitable timer, for an amount that an integrating
   controller adjusts each frame by a fifth of the difference between that wait and
   `latencyBrakeTargetMs`. A hitch counts as one frame's worth at most. The sleep is
   at most half a frame's work, so a long wait cannot hold the game back after it.
3. The target is the waiting left in each frame, so the GPU is still fed in time.

Under DLSS frame generation the brake sleeps in `KspReflexFrameStart`, ahead of
Streamline's `slReflexSleep` and its simulation marker (`BrakeBeforeFrame`), so that
Reflex sleeps and marks after it as before. `latencyBrakeWithDlssg=0` leaves DLSS frame
generation to Reflex alone.

Every `reportSeconds` the log says `Latency brake: sleeps ... ms before the frame ... ms of
waits in the proxy ... left a frame of ... ms work`.

## How the latency is measured

The measure is the same on every GPU, so that what lowers it can be told apart where a
vendor's own report is missing **[src]**. A frame's start on the main thread is stamped.
The GPU's end of its Present is found through a fence of the proxy's own on the
presenting queue, and a thread that waits on it. The proxy logs the median and the 90th
percentile of the frames since the last report as `Latency:`, with what lowered it. It
reads about 1.3 ms above NVIDIA's own report **[meas]** (RTX A4500: 59.1 against 57.7 ms
with raytracing on *Full*).

A second `Latency:` line splits the median into stages, from timestamps the proxy takes. They
are the simulation, the way from its end to the Present, the Present, the way from its end to
the GPU's end, and the render thread's share of the second. At raytracing *Full* with FSR 3 and
the brake at 4 ms, the stages were 7.6, 15.0, 2.4 and 23.5 ms **[meas]**. With Reflex the log
adds NVIDIA's report as `Low latency:` lines, with its stages.

The diagnostics window's strip shows the last median and 90th percentile
(`KspLatencyMeasure`).

## Measured

An RTX A4500 at the Medium profile, 2560 by 1440 rendered at 1707 by 960, the launch pad,
FSR 3 as the upscaler, from the run `2026-10-21-0241`
([benchmarks.md](benchmarks.md)). Latency median in ms, and in brackets the rendered
frames a second:

| Raytracing and frame generation | none | Reflex | Brake 4 ms | Reflex and brake |
|---|---|---|---|---|
| Off, no frame generation | 21.8 (72.4) | 22.0 (72.2) | 21.9 (72.5) | 22.0 (72.8) |
| Off, FSR | 29.6 (66.7) | 29.1 (66.0) | 28.7 (65.9) | 28.8 (65.8) |
| Off, DLSS-G | 46.6 (66.1) | 46.8 (66.1) | 45.7 (66.0) | 45.7 (66.0) |
| Full, no frame generation | 57.8 (45.0) | 57.8 (45.2) | 48.6 (45.2) | 49.2 (45.1) |
| Full, FSR | 60.2 (39.6) | 60.1 (39.6) | 45.7 (39.4) | 45.6 (39.3) |
| Full, DLSS-G | 72.1 (40.2) | 72.3 (40.4) | 57.8 (40.2) | 58.0 (40.2) |

* Reflex changes the latency by no more than half a millisecond in any row.
* The brake takes 9 to 14.5 ms off at raytracing *Full* and under 1 ms off without
  raytracing, at the same frame rate within 1 %.
* A lower target lowers it further **[meas]**. With FSR 3 and DLSS-G, raytracing *Full*,
  at 4, 2 and 1 ms, the latency was 59.0, 53.3 and 49.3 ms against 71.8 ms with no
  brake. The frame rate went from 40.2 to 40.2, 40.1 and 40.0. The 95th percentile of the
  frame time went from 25.5 to 27.3 ms at 4 ms.

**XeSS frame generation with XeLL** on the same machine, the cross-vendor way **[meas]**:

* Without raytracing it showed 32.9 ms at 63.0 rendered frames a second. That is 11.1 ms
  above no frame generation, and 3.3 ms above FSR frame generation.
* With raytracing on *Full* it showed 56.4 ms at 39.0 rendered frames a second. That is
  below FSR frame generation (60.2 ms) and DLSS-G (72.1 ms) without the brake, and 1.4 ms
  below no frame generation at all (57.8 ms).
* Its rendered frame rate is 13 % below the same rendering without frame generation at
  both loads. FSR frame generation is 8 % below without raytracing and 12 % at *Full*.

## Open

| Question | What settles it |
|---|---|
| Anti-Lag 2 on a Radeon: whether it lowers more than the brake | the benchmark's `antilag2` model on an AMD GPU, which the matrix skips elsewhere |
| XeLL on an Intel GPU, and XeSS frame generation with more than one generated frame | a run on an Intel Arc |
