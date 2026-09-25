# The upscaler

**For:** anyone changing ReDefinition's upscaling.
**You need:** the repository. The code is in `src/Upscaler/`, `src/Fsr3/`, `src/Bridges/` and
`src/DxgiProxy/`.
**You get:** what upscaling in KSP rests on, how each technique is driven, and what it
costs.

ReDefinition upscales three ways. FSR 3 runs in Unity. DLSS and AMD's upscaler DLL run in the
proxy. All three are driven by the engine's own motion vectors.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## KSP writes object motion vectors

Every temporal upscaler needs colour, depth and motion vectors. Without per-object motion
vectors, rotating parts and propellers would smear.

**[meas]** They are there. With the camera still, any motion in the buffer is object
motion. What tells object motion from camera motion is the shape of the distribution, not
the share of pixels:

| Case | p50 | max | max/p50 |
|---|---|---|---|
| camera motion, full screen field | 10.63 | 20.07 | 1.9 |
| object motion against a still background | 0.00 | 33.00 | > 1000 |

A flight is no test for this. KSP's floating origin keeps the vessel still relative to the
camera and moves the world, which looks like camera motion. The VAB with the rotation tool
is the test.

**[meas]** No visible shader on the development install has a `MotionVectors` pass, 0 of 64
over 3419 renderers. The vectors work anyway. Unity's built-in
`Hidden/Internal-MotionVectors` covers motion by transform, and skinned meshes where
`SkinnedMeshRenderer.skinnedMotionVectors` is on **[src]**. `SkinnedMotionVectors` switches
that on for kerbals and flags while the rig runs. Geometry a material's vertex function
moves gets no motion vectors.

## FSR3Unity on Unity 2019.4

`src/Fsr3` and the FSR shaders in `unity/Assets/ReDefinition/Shaders` are
[FSR3Unity](https://github.com/ndepoel/FSR3Unity) (MIT, Nico de Poel) at FSR 3.1.3. It is a
C# reimplementation of AMD's FidelityFX backend with AMD's FSR 3 compute shaders, adapted
for Unity 2019.4 and KSP.

FSR3Unity needs Unity 2020.1 for `multi_compile` in compute shaders. KSP is 2019.4.18f1,
and shader bundles must come from exactly that version. Of the seven keywords, six are set
once at initialisation. The seventh, `APPLY_SHARPENING`, decides between two accumulate
passes. The combination is therefore known, and it is baked in: one variant per shader, a
second accumulate pass with sharpening, and each set once with `HDR_COLOR_INPUT` and once
without (`_ldr`). The rig picks the set by the camera, since KSP renders without HDR in the
editors and with it in flight.

**The shaders** are made from FSR3Unity's by `tools/port_fsr3_shaders.py`. It bakes the
keywords in. It turns the `cbuffer` blocks into `StructuredBuffer`s, since
`SetComputeConstantBufferParam` also arrived only in 2020.1. It adds FSR 3.1.4's four
tuning constants, AMD's `FFX_API_CONFIGURE_UPSCALE_KEY_*`, at AMD's defaults. The getter
functions and passes stay as they are.

The constants go into a structured buffer rather than global uniforms.
`SetComputeIntParams` and friends write raw into the constant buffer following HLSL's
packing, so an `int2` would take 16 bytes and FSR would compute with an image of 0x0.

**The C# runtime** is adapted in the source; the larger changes are marked `ReDefinition:`
there.

* The constants bound as structured buffers (`Fsr3Constants.cs`), and the intermediate
  textures bound to their UAV slots, which Unity 2019.4 does not do by name.
* No keywords; `VerifyBakedFlags` reports at run time when the configuration asked for
  is not the one baked into the shaders.
* FSR 3.1.4's tuning constants in `DispatchDescription`.
* The automatic reactive mask: its history filled from the current frame on a reset,
  and the opaque-only image compared with the image right after the transparent queue
  (`ColorPostAlpha`).
* The RCAS constants computed, so that sharpness goes past FidelityFX's 1; also for RCAS
  after another upscaler (`RcasSharpener`).
* Without texture arrays and the callbacks interface; profiler samples by name, the
  form Unity 2019.4's `CommandBuffer.BeginSample` takes.

**Sharpening.** Sharpness above 0 builds the context with the sharpening accumulate pass
and RCAS after it. At 0 the accumulate pass writes straight into the output. Moving the
slider between 0 and a value above rebuilds the rig.

## DLSS and AMD's upscaler DLL

Both run in the `dxgi.dll` proxy, on the same inputs as FSR 3. The rig writes a packet per
frame into its dispatch buffer, executed from `OnRenderImage`. A render event hands the
packet to the proxy on Unity's render thread (`src/Bridges/NativeUpscalerLink.cs`,
`src/Bridges/PacketRing.cs`). Jitter and motion vector scale are FSR's, in pixels at render
size (NVIDIA's DLSS Programming Guide 3.6.1, 3.7.3; AMD's `ffx_upscale.h`) **[doc]**.

* **DLSS** (`src/DxgiProxy/Dlss.cpp`, `src/Bridges/DlssBridge.cs`) runs through NGX on
  Unity's Direct3D 11 device, with auto exposure, and the render preset the player chose.
  Default lets the DLSS library choose. DLSS sharpens nothing itself, so with sharpness
  above 0 it writes into the input of FSR 3's RCAS pass (`RcasSharpener`), which writes the
  output. DLSS renders at the size it asks for, and another size makes a new rig.
* **AMD's DLL** (`src/DxgiProxy/AmdUpscaler.cpp`, `src/Bridges/AmdUpscalerBridge.cs`) runs
  on the proxy's Direct3D 12 device. Colour, depth and motion vectors are copied into
  textures both devices share, AMD's upscaler runs on the Direct3D 12 queue, and the result
  is copied back into Unity's output, with a shared fence between the two. The DLL decides
  which upscaler runs: FSR 4 where the DLL and the GPU have it, otherwise FSR 3.1.
* **The proxy's state** is 0 before the first frame, 1 while it upscales, -1 once something
  stopped it, and -2 when that holds until the GPU, driver or DLL changes. The proxy's
  output is shown only once it has reported an upscaled frame; before that the image
  without upscaling is shown. A technique that stays stopped gives way to FSR 3: the add-on
  builds a new rig with FSR 3 in its place, and the diagnostics window offers to try the
  technique again.

The files the player needs, and where they go: [player/installing.md](../player/installing.md).

## What is different about KSP

* **The camera stack.** `GalaxyCamera` paints the stars, `Camera ScaledSpace` the distant
  planets, and `Camera 01` and `Camera 00` the near scene. They all paint into one frame
  buffer, with `InternalCamera`, `Main Camera` and others in other scenes. The list of
  cameras follows KerbalVR, which redirects the same stack. An image effect on one camera
  would make Unity render it into its own texture and lose what the cameras below drew. So
  the whole 3D stack renders into one shared render texture (`CameraRedirect`), and a
  presenter camera behind it scales the result into the frame buffer before the UI cameras
  (`UpscalerPresenter`). `FXCamera` and `FXDepthCamera` are not redirected, since they are
  not part of the image.
* **The jitter puts back the projection it found.** Once a script assigns a camera's
  projection matrix, Unity stops following its field of view. After each render, a
  projection Unity computed is Unity's again. A projection a script set is written back,
  unless that script changed it during the render. KSP's `CameraOffCenter` in the editors
  is such a script.
* **The jitter goes on in `OnPreRender`, not `OnPreCull`.** Unity culls and builds the
  shadow cascades between the two: shadows are built from the unjittered matrix, the
  image is rendered offset. It also wins the write order against mods that assign the
  projection in their own `OnPreCull`, such as Scatterer's TAA. The unjittered matrix is
  stored in `nonJitteredProjectionMatrix` first.
* **Depth comes from `BuiltinRenderTextureType.ResolvedDepth`.** `.Depth` delivers
  nothing in the deferred path, and the depth sub-element of the camera target does not
  exist in 2019.4.
* **Motion vectors and depth are copied on the scene camera.**
  `BuiltinRenderTextureType.MotionVectors` is valid only inside the rendering of the
  camera that produces it, so a command buffer at `BeforeImageEffects` copies them there.
* **Game-wide quality settings** (`QualityOverrides`). While the upscaler runs, the LOD
  bias is multiplied by display height over render height, since Unity picks LODs by
  covered pixels. The mipmap bias is FSR's own `log2(render / display) - 1` and a
  different quantity. MSAA is held at off, and every profile sets KSP's own setting
  off as well (`ALL_PROFILES` in `KSP.cfg`). Anisotropic filtering is forced on so
  the negative mipmap bias does not shimmer. Shadow distance and cascades are left alone. KSP's
  `SetQualityLevel` resets these, and `OnGameSettingsApplied` puts them back. Where KSP
  writes a value there other than ReDefinition's, that value becomes the one restored when
  the upscaler stops. MSAA from KSP's settings screen is such a value.
* **Mipmap bias** (`KspMipmapBias`) touches only textures on renderers a redirected camera
  sees.

## Other mods in the image

### Temporal and spatial filters in `HostStack`

An upscaler is a temporal filter. A second one in front of it averages the image twice, and
both jitter the same projection. `HostStack` reads the other mods' state by reflection, so
no mod is a reference, and, while a graphics profile is chosen, puts that state into what
the upscaler needs:

* TUFX's antialiasing to `None`, on the profile and on the live `PostProcessLayer`s.
* Scatterer's `TemporalAntiAliasing` and SMAA off, and Deferred's SMAA in the editors.
  SMAA hangs a command buffer at `AfterForwardAlpha`, before the capture at
  `BeforeImageEffects`, so the upscaler would receive smoothed edges, which is what it
  reconstructs from. The buffer outlives the component and is taken off too. Deferred adds
  a fresh copy on every editor scene load, so the editors are checked once a second.
* Kerbal Frame Generator's frame blend off, if installed. Its switch is only in memory,
  and it goes back on when no profile is chosen, where it still holds the value written
  here.

EVE's temporal upscaling for its clouds is EVE's own reconstruction and stays.

With Scatterer's TAA component present, even at zero jitter, the rig's jitter does not
reach `Camera 00` and `Camera ScaledSpace`, and both writing `nonJitteredProjectionMatrix`
shift the whole image **[meas]**. So the component is switched off.

**Switching Scatterer's TAA off is not enough on its own.** Three of its effects outlive
the component, two of them the scene:

* its **replacement motion vector shader**, installed globally with
  `GraphicsSettings.SetCustomShader` and never reverted;
* **`TAA_UseFloatingOriginCameraMotion` and `TAA_PreviousFrameTransform`**, shader
  globals frozen at their last value;
* **`motionVectorGenerationMode` per vessel renderer**, set to `ForceNoMotion` on load and
  corrected every frame. Disabled, they freeze and stop writing object motion, and the
  vessel smears.

All three are cleaned up, and reasserted after scene changes, since Scatterer rebuilds
its components. The shader and `TAA_UseFloatingOriginCameraMotion` are put back, and
only where the value is still the one written, so a newer choice by the player or
another mod stands. `TAA_PreviousFrameTransform` stays at identity, and the renderers
freed from `ForceNoMotion` stay on `Object`, which is what writes their motion.

### The distant planets

`Camera ScaledSpace` draws the planets seen from afar, their atmospheres and the clouds on
them; `Camera 00` draws the near scene over it. The rig's motion vectors are `Camera 00`'s,
and where it draws nothing Unity fills them with its camera motion at the far plane: the
camera's turn, nothing of the planet's rotation or of the vessel travelling along its orbit
**[src]**. A planet moving across the screen, fast under time warp, was reprojected as if
it stood still, and trailed.

KSP places and turns every scaled body in `LateUpdate` and hides a faded one by disabling
its renderer (`ScaledMovement`, `ScaledSpaceFader`, decompiled). As `Camera 00` culls,
`ScaledSpaceMotion` takes up to eight bodies in view, largest on screen first, with their
transforms and the scaled camera's view and projection. Pass 2 of the cloud motion shader
follows the scaled camera's ray through every pixel where `Camera 00`'s depth is at the far
plane to the nearest body it meets, on the surface or, passing it, within the atmosphere.
It moves that point back by the body's change of transform since the previous frame and
writes the difference of the two projections in Unity's encoding. EVE's clouds are
blended over the result. The scaled camera is left as it is: asking Unity for its motion
vectors made the picture flicker everywhere with DLSS. EVE's two-dimensional cloud layers
drift by a shader and keep the planet's motion under them.

Measured in a Unity 2019.4.18f1 player on Direct3D 11, with a moving sphere drawn by a
camera that renders into a render texture: the pass writes on exactly the sphere's pixels,
with the values Unity's own motion vectors carry there, `y` up.

### The active vessel

Measured on the runway with DLSS presets L and M: while the aircraft rolled fast, 0.5 to
1.2% of its pixels, in up to a quarter of the frames, carried Unity's camera motion, the
motion of standing ground at that depth, instead of the part's own, off by up to a hundred
pixels. Most were on the canards, the elevons, the nose and the landing gear. At a
standstill both agree and it does not show. The upscalers blend in the wrong history at
those edges, and they flicker.

Unity's depth sources in the deferred path, `ResolvedDepth`, `Depth` and
`_CameraDepthTexture`, hold what the deferred pass drew and nothing of what the forward
pass draws after it. Measured in `MotionVectorCheck` with a forward-only cube in front of
a deferred sphere: 0 at the cube in all three, 0.0664 in the camera's own depth buffer.
Parts KSP draws in the forward pass were missing from the depth every upscaler and frame
generation read. In NVIDIA's alignment test the parts under the vessel showed through it.
Pass 2 of `Hidden/ReDefinition/MotionAudit` writes each of the vessel's opaque renderers'
depth into the captured depth where it is nearer, skinned ones included, before the motion
vectors are written. The check writes the cube's depth the same way and finds 0.0664.

`VesselMotionVectors` draws each mesh renderer of the active vessel once more at the end
of the capture with `Hidden/ReDefinition/MotionAudit`, rasterised with the scene camera's
jittered projection. Where its depth is the captured depth, pass 1 writes the motion
vector Unity writes for an object: this frame's position against the previous frame's,
through the renderer's previous matrix and the previous view-projection. It runs before
the other mods' hooks, so a mod's own motion vectors for a part stay on top. Skinned
renderers and materials above queue 2500 are left out, and so are frames after an origin
shift or a reset. NVIDIA's guide suggests drawing the motion vectors of problem objects
separately (3.6.4).
The Debug tab's *Vessel depth and motion vectors* switches both back to Unity's.

### Between the physics steps

KSP steps physics at 50 Hz and sets no `Rigidbody.interpolation` on the parts
(decompiled), so the vessel and the camera on it move only on a step. At about 59 rendered
frames a second one in six has no step. The screen recording on the runway measured the
ground's shift between shown frames at 4 to 7 px, then twice 0, about nine times a second.
Frame generation's worst frames were the ones right after such a standstill.
`RenderInterpolation` moves the active vessel's topmost part transforms by
`(previous - current) * (1 - alpha)` as the frame's first camera culls, and puts their
local positions back at the end of the frame, before the next physics step. The camera is
never moved by itself: following the vessel, KSP hangs its pivot on the vessel, so it
moves along; a camera standing free does not follow the vessel and stays. Other vessels
are left as KSP draws them. Unity's own interpolation would leave KSP's physics reading
positions a step old. `StepInterpolator` holds the arithmetic; its test runs 600 frames at
59 per second over 50 Hz steps and finds every frame moved by the same distance to a
millimetre, where the positions alone stand still in 80 to 120 of them. Left alone: a
floating origin shift, a jump over 50 m, a packed vessel, Krakensbane, IVA and map view.

### The active vessel's shadow

Frame generation moves each pixel of the HUD-less image with its motion vector, the motion
of the surface. The vessel's shadow on that surface moves with the vessel. Under a vessel
the camera follows, the shadow stands on the screen while the ground runs, and the
generated frames dragged it along with the ground: in a recording on the runway, the
shadow of a canard 90 px off in every generated frame, or gone. `VesselShadowLayer` takes
the shadow out of the HUD-less image where that is the smaller error. DLSS frame
generation then treats the difference to the frame, the shadow, like the interface and
does not move it. NVIDIA describes the HUD-less image only as a guide for the interface;
that it keeps the shadow in place was measured in the harness replay, not promised.

It works per pixel, since a shadow has no one motion:

* **Whose shadow.** The vessel's renderers are drawn from the light into a map of their
  distance along the light and how far they moved since the previous frame. A pixel counts
  where the vessel blocks the light within 0.5 m of it. Terrain, buildings, grass and
  clouds never count.
* **How much of it.** Unity's own screen-space shadow mask for the light, copied at
  `LightEvent.AfterScreenspaceMask`, holds the soft edge, the cascades and the filtering
  as drawn. Unity lights a point with ambient plus direct times the mask; with the
  shadow strength that gives the share of the sun the vessel takes.
* **Where it lay before.** The occluding point, as far along the light as the map says,
  moved as its renderer moved, projected along the light onto a plane through the
  surface point. Left in place the shadow is off by its own screen motion, moved with the
  surface by the difference to the surface's motion vector. The weight is the one error
  squared over both: 1 where the shadow stands, 0 where it moves with the ground, as under
  a vessel standing still and a turning camera.
* **How dark full shadow is.** The mean colour in full shadow against lit ground within
  3 m of it, per channel, from mip chains of both at half the display size. Whatever the
  sun's colour, the shadow strength, the atmosphere or the post-processing do is measured
  in the finished image.

The light is the brightest enabled directional light with shadows that lights layer 15.
With several stars (Kopernicus), only that one's shadow is handled. Its direction is read
in the scene camera's `OnPreRender`, where Unity has built the cascades from it
(`CameraRedirect`). The floating origin and Krakensbane move the world between frames:
the vessel and the camera by minus `SharedFrame.OriginShift`, the ground by minus
`BodyShift`, which holds the Krakensbane step in fast flight. The previous frame's
matrices are carried across by the first, and the surface the shadow fell on lay off by
the difference. Left alone: a frame with a history reset, and whatever is drawn over the
shadow, such as engine plumes or dust, which leaves the HUD-less image with it. The
Debug switch **Vessel shadow in frame generation**, not saved, turns it off.

The light map covers the bounds of the vessel's renderers. Renderers whose bounds are not
finite, longer than 250 m on a side or farther than 500 m from the vessel neither size it
nor are drawn into it (`CasterBounds`): in flight one renderer of a spaceplane reported
extents of 10^18 m, and a map that wide never found the lit ground next to the shadow.
Every ten seconds the log reads back one frame's samples: how many lie in shadow in
Unity's mask, in the vessel's shadow, weighted, and on lit ground to compare with.
**Record the screen** writes the layer's inputs and results of one frame into a folder
ending in `-shadow`.

### EVE's volumetric clouds

EVE casts the clouds' rays through the projection Unity binds, but reprojects their
history and makes their motion vectors with the non-jittered projection
(`DeferredRaymarchedVolumetricCloudsRenderer.OnPreRender`, decompiled) **[src]**. The
clouds write no depth and no motion vectors into the camera's buffers.

* **Jitter** (`EveCloudMotion`): while EVE prepares the clouds for a camera the rig
  jitters, a Harmony postfix hands it that camera's jittered projection, and the jitter
  handed over is kept.
* **Motion vectors** (`CloudMotionVectors`): EVE's own
  (`scattererReconstructedCloudMotionVectors`, with `scattererReconstructedCloud`) are
  read at `AfterForwardAlpha`, the jitter is taken out again, and they are blended over
  Unity's at the capture the way Scatterer's TAA blends them. The clouds' vectors count
  where they cover the pixel, over the sky by their transmittance, and over geometry once
  less than a tenth of it is left.
* The motion vectors only with Scatterer's cloud reconstruction shader
  (`Scatterer-EVE/ReconstructRaymarchedClouds`), whose output was read; the jitter
  wherever EVE's renderer and its `GetNonJitteredProjectionMatrixForCamera` are found.
  The Debug switch *EVE clouds: jitter and motion vectors* is on at every start.

### TUFX's post-processing

TUFX's profile is a `PostProcessLayer` on a redirected camera, so every effect would run
at render size before the upscaler. AMD sorts the effects (`super-resolution-upscaler.md`,
"Post processing A" and "B") **[doc]**: screen-space reflections, ambient occlusion and
exposure before the upscaler; film grain, chromatic aberration, vignette, tonemapping,
bloom, depth of field and motion blur after it.

`TufxPostProcessing` keeps ambient occlusion, screen-space reflections, fog, auto exposure
and effects other mods inject before the built-in stack on the redirected layer. A layer of
ReDefinition's, on a camera that never renders, draws the rest over the upscaler's output
from the same volumes. Which layer draws what is decided right after PostProcessing blends
a layer's settings, in a Harmony postfix on `UpdateSettings`. This happens only while the
image the upscaler receives is HDR, because an 8-bit target clamps the highlights that
bloom and tonemapping need. Depth of field and motion blur after the upscaler get the rig's
copies of depth and motion vectors. Dithering happens once, on the final image. The Debug
switch *TUFX after upscaling* is on by default.

### The masks for FSR 3

`UpscalerMasks` draws two masks, both off by default:

* **Transparency and composition** covers what motion vectors do not follow though nothing
  is blended: EVE's clouds by their transmittance and fade, Scatterer's ocean by its ocean
  G-buffer depth.
* **Reactive** covers what is blended over the scene: particles and their trails, engine
  effects, re-entry. It is either drawn from the transparent renderers in view with the
  mask shader, occluded by a copy of the scene's depth, or made by FSR's own generator from
  an opaque-only copy and the image after the transparent queue.

Both are recorded at the scene camera's `AfterForwardAlpha`. AMD's guidance for both is in
`super-resolution-upscaler.md` of the FidelityFX SDK, under "Reactive mask" and
"Transparency and composition mask".

## What the upscaler buys on the development machine

**[meas]** KSP 1.12.5, Unity 2019.4.18f1, Direct3D 11, `Camera 00` deferred (Deferred),
TUFX, Parallax, Scatterer, EVE; RTX 4090 at 3440x1440, FSR 3 UltraPerformance = 1147x480,
11 % of the pixels:

| | fps | frame time | added |
|---|---|---|---|
| upscaler off | 97 | 10.31 ms | the baseline |
| bypass, 3D stack rendered at 1147x480 | 95.5 | 10.47 ms | +0.16 ms |
| full chain with FSR | 92.5 | 10.81 ms | +0.50 ms |

Rendering 89 % fewer pixels made the frame longer by what the redirect costs. The pixel
shading of the 3D scene costs under 0.2 ms. FSR's twelve passes cost 0.34 ms, about what
AMD quotes. In flight the GPU sits at 90-97 % and the CPU at 30-40 %. They are saturated by
work that does not scale with output resolution: Scatterer's shadow cascades at 8192, EVE's
fixed-resolution volumes and raymarching, reflection probes. Lowering KSP's own resolution
without the mod changed nothing either. Hence *AA only* in every profile. **[open]** On
hardware limited by pixel fill rate the smaller modes would pay, but that is not measured.

## When a texture cannot be made

**[meas] 2026-09-20.** `RenderTexture.Create()` answers true where Direct3D could not
make the texture: Unity writes `D3D11: Failed to create RenderTexture ... 0x8007000e`
into the log and carries on. A rig built on such textures redirects the cameras into
nothing, and the player sees a black screen. The rig therefore asks for the native
handle (`GetNativeTexturePtr`), which is null where the texture is not there, and its
set-up fails instead. While it runs, the same handle is asked for about once a second,
since asking synchronises with the render thread, and a texture lost since then has the
rig rebuilt. The add-on then tries again after a second, after five, and three
times in all, so the upscaler comes back by itself once the memory does
([reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md)).

## Camera cuts

The history is reset whenever the flight camera's target, its parent or the kerbal of the
IVA view differs from the last look. That look is taken before the frame's first scene
camera culls, and again when the rig dispatches. It is also reset whenever a mod reports a
cut (`Frame.RequestHistoryReset`, [shared-foundation.md](shared-foundation.md)). A change of
camera mode rebuilds the rig, since entering IVA enables `InternalCamera`. KSP's camera
events are not used: they arrive after the rig's update, twice, when nothing cut, or not at
all. No reset follows a floating origin shift, since KSP shifts every frame above a speed
threshold, and a reset every frame is an upscaler without history. Each reset and rebuild
is logged with its reason. Mod by mod:
[reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md).

## Instruments

In the diagnostics window's *Debug* tab
([player/settings-window.md](../player/settings-window.md)):

* **Camera and origin**, about every ten seconds: frames with an origin shift, the largest
  re-centring and Krakensbane step, the largest turn and move in ordinary frames, and turn
  and jump at detected cuts.
* **Motion vectors against the true motion**, about every ten seconds in flight
  (`MotionVectorAudit`): every fifth frame the capture reads back the finished motion
  vectors and depth at the centres of three of the vessel's parts and at four points low
  in the picture. The pixel's world position, taken back from its depth, is moved back
  by the part's change of transform or, for the ground, not at all, and projected with
  this and the previous frame's view-projection. The line gives, for vessel and ground,
  the samples, the mean and largest difference from what was read in pixels, the mean
  motion, and the worst sample. Frames with an origin shift or a reset are left out.
  `MotionVectorCheck` in the Unity project checks the pixel addressing it relies on.
* **Vessel motion vectors per pixel**, about every ten seconds in flight
  (`VesselMotionVectors`): pass 0 of the same shader, after the hooks, compares the motion
  vector computed for each vessel pixel with the one the upscalers read; per part, a
  buffer counts pixels, pixels off by more than a pixel and the largest error. The line
  says whether the rig wrote them, and gives the share off, the frames with more than
  0.5% off, apart for frames with and without a physics step, and the parts most off.
* **Record the screen**: five seconds after the click, the proxy records half a second of
  what the monitor shows through DXGI desktop duplication, frame generation's frames
  included, as PNG files of a 1600x900 cut-out around the middle of the game window, with
  `frames.txt` giving each frame's present time. The harness records 40 frames while DLSS
  frame generation runs.
* **Motion vector check on fast turns**, with frame generation: a fast turn asks the proxy
  for its motion vector check at once, and a line says what the camera did.
* **Write diagnostics to log**: what the cameras, the inputs, the masks, the proxy's
  textures and the projection per camera hold, with every switch position.
* **The preview**: the image at render size, the result, motion vectors, depth, the masks.
* **FSR's debug view**, in development builds only (`DEVELOPMENT_BUILD`).
* **The bypass**: the redirect without the upscaler, which separates camera wiring from
  the upscaler.

## Open

| Question | What settles it |
|---|---|
| Whether the distant planets' motion vectors take out the trails behind the planet and its clouds under time warp | a look from orbit at 100x and above, with the motion vector preview |
| Whether the transparency and reactive masks take out ghosting on clouds, ocean, plumes and re-entry | a flight with engines burning over the ocean and under clouds, masks off and on, with the preview |
| What a floating origin shift does to Unity's motion vectors in that frame | the camera and origin line in orbit and low and slow, next to the proxy's motion vector check |
| Camera cuts that keep target and parent: Hullcam between two cameras on one part, CameraTools switching mode or vessel, jumps inside a CameraTools mode | the mods' own report (`Frame.RequestHistoryReset`), or their state read by reflection (Hullcam's `sCurrentCamera`, CameraTools' `toolMode` and `vessel`), and the turn and jump numbers for the jumps |
| World-anchored UI in the smaller modes | it positions itself from `Camera.pixelWidth`, the render size then; a fix means cameras rendered by hand, as KerbalVR does |
