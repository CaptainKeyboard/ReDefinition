# Graphics mod compatibility

**For:** anyone asking how another graphics mod meets the upscaler, and mod authors
looking for their own mod in the list.
**You need:** nothing.
**You get:** what each mod does to the frame, what that means for the upscaler and
frame generation, and what ReDefinition does about it.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([../development/writing-these-pages.md](../development/writing-these-pages.md)).

## What ReDefinition does to the frame

All of this is **[src]**, from ReDefinition's own code:

* The upscaler attaches to `Camera 00` in flight and `Main Camera` in the editors,
  and redirects the 3D camera stack into a render-size target. The list follows
  KerbalVR, which redirects the same stack: `GalaxyCamera`, `Camera ScaledSpace`,
  `Camera 01`, `Camera 00`, `InternalCamera`, `sceneryCam`, `Main Camera`,
  `markerCam`, `Landscape Camera`. UI cameras stay at display size.
* Each redirected camera is jittered in `OnPreRender`, after every `OnPreCull` and
  after Unity has built the shadow cascades. The unjittered matrix is stored in
  `nonJitteredProjectionMatrix` first. Transparent geometry is jittered as well. AMD:
  *"Jitter should be applied to all rendering. This includes opaque, alpha
  transparent, and raytraced objects."* **[doc]**
* Colour, depth and motion vectors are captured at `BeforeImageEffects` on the scene
  camera. The upscaler then runs, and a presenter camera after the stack shows the
  result. Frame generation's HUD-less copy is taken at `AfterEverything` on the
  presenter, and on `FXCamera` and `FXDepthCamera` where they draw after it.
* **ReDefinition switches off** what runs a second temporal or spatial filter in front of
  the upscaler, while a graphics profile is chosen: Scatterer's TAA and SMAA
  components, Deferred's SMAA in the editors, the antialiasing of TUFX's
  post-processing layers, and Kerbal Frame Generator's frame blend.
* It also clears what Scatterer's TAA leaves behind: its motion vector shader,
  two shader globals, and vessel renderers set to `ForceNoMotion`. Every change holds
  for the current run and is handed back once no profile is chosen. What a later scene
  builds anew is taken once a second. The profiles set Scatterer's TAA and SMAA and
  Deferred's editor SMAA off in the mods' own settings as well, through
  `ALL_PROFILES`.
* The history resets on the rig's setup, and whenever the flight camera's target, its
  parent or the kerbal of the IVA view changes. A change of camera mode rebuilds the
  rig.

## Mod by mod

### Installed here

| Mod | Licence, authors | What it does to the frame **[src]** | For the upscaler and frame generation | What ReDefinition does |
|---|---|---|---|---|
| **Scatterer** | GPL-3.0; Ghassen Lahmar (blackrack) | TAA on the near, far and scaled cameras (and the internal camera in IVA) at `AfterForwardAlpha`, with its own projection jitter. SMAA at `AfterForwardAlpha`. A custom motion vector shader installed globally for `BuiltinShaderType.MotionVectors` and never reverted. Command buffers at `BeforeForwardOpaque` (depth pre-pass merge), `AfterForwardOpaque` (depth to distance), `BeforeForwardAlpha` (scattering), `AfterForwardAlpha` (caustics) and `AfterImageEffectsOpaque` (ocean, screen copy). Settings: `Scatterer_config` in `Scatterer/config/config.cfg`, read through the GameDatabase, written back by its own window. | A second temporal filter and a second jitter in front of the upscaler; an image smoothed by SMAA before the capture. | ReDefinition switches TAA and SMAA off and clears the leftovers, without a restart. The diagnostics window says whether Scatterer's TAA runs and which motion vector shader is installed. With FSR 3, the transparency mask covers the ocean (Debug switch, off by default). |
| **EVE Redux with raymarched volumetric clouds** | EVE core MIT, Ryan Bray (rbray89); the volumetric branch by Ghassen Lahmar, distributed on Patreon, "All rights reserved" | Clouds rendered at `AfterForwardOpaque` on the scene camera, with their own temporal reprojection, their own cloud motion vectors and history. Rays are cast through the projection Unity binds; history and motion vectors use the *non-jittered* projection (`VRUtils.GetNonJitteredProjectionMatrixForCamera`). The clouds write no depth and no motion vectors into the camera's buffers. | Without further work the clouds carry no jitter, and the motion vectors over them are those of the sky or the ground behind. | A Harmony postfix hands EVE the jittered projection while it prepares the clouds for a jittered camera. The clouds' own motion vectors (`scattererReconstructedCloudMotionVectors`), with the jitter taken out, are blended over Unity's by the clouds' transmittance at the capture. Both only with Scatterer's cloud reconstruction shader, whose output was read; Debug switch *EVE clouds: jitter and motion vectors*, on at every start. With FSR 3, the transparency mask covers the clouds (off by default). |
| **TUFX** | GPL-3.0; shadowmage45, now the KSPModStewards | Unity's post-processing stack v2: a `PostProcessLayer` on the main, internal and scaled cameras, antialiasing per profile (TAA, SMAA, FXAA; a separate mode for secondary cameras), HDR per profile, MSAA switched off when HDR and bloom are on. Profiles are `TUFX_PROFILE` config nodes (GameDatabase, so patchable by ModuleManager); the profile per scene is stored per save (`CustomParameterNode`). | TAA is a second temporal filter. Effects AMD puts after the upscaler -- bloom, tonemapping, grain, vignette, chromatic aberration, lens distortion, depth of field, motion blur -- would run on the render-size image. | ReDefinition sets the layers' antialiasing to None. While the image the upscaler receives is HDR, the effects that belong after the upscaler are drawn over its output by a layer of ReDefinition's, from the same volumes (Debug switch *TUFX after upscaling*, on by default); ambient occlusion, screen-space reflections, fog and auto exposure stay in front. The HUD-less survey names a camera with a `PostProcessLayer` that draws after the snapshot. |
| **Deferred** | GPL-3.0; Ghassen Lahmar | Switches the cameras to deferred shading with its own deferred shading and reflection shaders. Screen-space reflections at `BeforeImageEffectsOpaque`, which ask for depth and motion vectors; a screen copy at `AfterForwardAlpha`; its own SMAA at `AfterForwardAlpha`; PQS fading at `BeforeGBuffer` and `AfterFinalPass`. Settings: the `Deferred` node in `zzz_Deferred/Deferred.cfg`. | Its SMAA is added only in the editors, to the editor's `Main Camera`, when `useSmaaInEditors` is set -- which the shipped `Deferred.cfg` does (`EditorLighting.HandleSMAA`). In the VAB and SPH the rig sits on that camera: a spatial antialiasing pass before the capture. | ReDefinition switches that SMAA off -- the component, and the command buffer it attached, which outlives the component -- re-checked once a second in the editors, since Deferred adds a fresh copy on every editor scene load. |
| **Parallax Continued** | "All Rights Reserved"; Gameslinx | Tessellated terrain shaders; GPU scatters (grass, rocks) generated by compute shaders and drawn with `Graphics.DrawMeshInstancedIndirect`. None of its 32 shaders has a `MotionVectors` pass. Settings: `ParallaxContinued/Config/ParallaxGlobalSettings.cfg` (tessellation, density and range multipliers, shadows). | Terrain and scatters get camera motion only. Wind animation moves grass without motion vectors. | Nothing. **[open]** whether waving grass smears visibly under the upscaler. |
| **Waterfall** | CC BY-NC-SA 4.0; the KSPModStewards | Engine plumes with additive, transparent shaders (`ZWrite Off`, own render queues); adds depth to the flight camera's `depthTextureMode`. Settings: `WATERFALL_SETTINGS` in `Waterfall/WaterfallSettings.cfg`. | Plumes write neither depth nor motion vectors: animated, transparent content. | With FSR 3, the reactive mask covers transparent renderers (Debug switch, off by default). **[open]** ghosting behind plumes against a moving background. |
| **Firefly** | Code GPL-3.0, assets "All Rights Reserved"; MirageDev (M1rageDev) | A camera per vessel (`cullingMask` 1) that renders the vessel into a texture, and a command buffer on the flight camera at `AfterForwardAlpha` that draws the re-entry effect. Settings: `ATMOFX_SETTINGS` in `GameData/Firefly/ModSettings.cfg`. | Drawn into the scene before the capture, so the upscaler processes it; transparent, without motion vectors. | As for Waterfall. |
| **Distant Object Enhancement /L** | SKL 1.0 or GPL-2.0; LisiasT, earlier Rubber Ducky, MOARdV and TheDarkBadger | Flare meshes for distant bodies and vessels, and a darker skybox near bright bodies. No cameras, no command buffers. Settings: `Settings.cfg` in its `PluginData`. | No temporal interaction found. Flares are small bright points, which temporal filters can make flicker. | Nothing. **[open]** |
| **Trajectories** | GPL-3.0 | `LineRenderer` objects, and GL lines drawn in `OnPostRender` of components added to `FlightCamera.fetch.mainCamera` -- the first of the flight cameras. | Both flight cameras render into the shared render-size target, which is the upscaler's colour input. The lines are in the image the upscaler reconstructs, without depth or motion vectors of their own. | **[open]** whether thin lines survive the reconstruction. |
| **Kopernicus** | LGPL-3.0; R-T-B, StollD, Sigma88, Phantomical and others | Planet systems. Sun flares and rings through `Camera.onPreCull`; ring and atmosphere materials in the transparent queues (3010, 3020); no cameras of its own, no command buffers. With Harmony transpilers it redirects the calls to `FloatingOrigin.SetOffset` in `FlightDriver.Start` and in the scene setups of `PSystemSetup` to a wrapper that places the origin more precisely at scene start (`RuntimeUtility/PreciseFloatingOrigin.cs` in Kopernicus); the shifts during flight are untouched. | Nothing temporal of its own; rings and atmospheres are transparent, without motion vectors. | Nothing. |
| **HUDReplacer, ZTheme** | GPL-3.0; UltraJohn and the KSPModStewards; zapSNH | Replace KSP's UI textures by name. | None: UI. | The settings window is built from KSP's own dialog elements, so it is themed like the rest. |
| **KSPCommunityFixes** | MIT; the KSPModdingLibs | Many patches, none temporal. | None. | The section in KSP's settings dialog follows its pattern. |

Kerbals and flags are skinned renderers. ReDefinition makes them draw their own motion
vectors while the upscaler or frame generation runs. The Debug switch *Skinned motion
vectors* is on by default.

### Not installed here, and read from source

| Mod | Authors | What it does to the frame **[src]** | For the upscaler and frame generation |
|---|---|---|---|
| **Singularity** | MIT; Ghassen Lahmar (LGhassen), JonnyOThan, prustic; portions by Pim Schreurs (sirxemic/Interstellar) | Renders the scaled scene into its own screen-sized textures with extra cameras; gravitational lensing through command buffers on the scaled camera at `AfterForwardOpaque`. | Its buffers stay screen-sized when the redirect makes the scaled camera render smaller, and every exchange with that camera goes through full-screen blits and normalized screen coordinates (`ComputeScreenPos` in its shaders), so the sizes need not match **[src]**. Its copy of the scaled scene is rendered while the scaled camera culls (`OnWillRenderObject`, which Unity's order of event functions puts before `OnPreRender`), before the jitter goes on there: the lensed background carries no jitter while the scene around it does **[src]**. **[open]** how that looks under the upscaler. |
| **Hullcam VDS Continued** | linuxgurugamer | Re-parents the flight camera to part transforms, changes its field of view, and filters the image in `OnRenderImage` on the camera. | Taking the view clears the camera's target (`SetTargetNone`) and giving it back sets it again, so both reset the upscaler's history; a switch to a camera on another part is a new parent, which resets it too **[src]**. Two cameras on one part without transforms of their own -- `hc_booster`, `RoverCam` -- share the part as parent, and a switch between them is missed **[open]**; Hullcam's static `sCurrentCamera` would tell it. Its filter sits on `FlightCamera.fetch.mainCamera` (`MovieTime.cs`), one of the two flight cameras, both of which are redirected: it writes into the render-size target, so the upscaler reconstructs the filtered image **[src]**. Which of the two it is, KSP's prefab decides, not code **[open]**. **[open]** whether its per-frame noise and scanlines, which have no motion, survive that. |
| **CameraTools** | BrettRyland's fork | Moves the flight camera through its own parent object, including jumps. | Its modes take the view through `SetCameraParent` or `SetDeathCam`, both `SetTargetNone`, and `RevertCamera` sets the vessel as target again: entering and leaving reset the history, and so do its switch to the death camera and its taking the camera back, new parents **[src]**. **[open]** switching its mode or its vessel while active, which re-runs its start with the same parent object, and jumps inside a mode -- keyframes, a stationary camera placed anew; its public `toolMode` and `vessel` would tell the first two. |
| **SmokeScreen** | sarbian | KSP's legacy particle emitters and Shuriken particle systems. | Transparent, no motion vectors -- the same class as Waterfall. |
| **KerbalVR** | Vivero | Its own copies of the camera stack, rendering into the headset's textures. | Not supported together: the rig would upscale the desktop view. ReDefinition's camera list follows this mod's. |
| **Kerbal Frame Generator** (formerly KSR) | MIT; MangoTechKSP | Blends the current frame 50/50 with the previous one in `OnRenderImage` (`FrameBlend.shader`). No extra frames, no motion vectors -- not frame generation. | On top of the upscaler it adds ghosting. In flight it sits on the camera KSP's `GalaxyCameraControl` belongs to, elsewhere on `Camera.main` (`KFG_UI_Controller.LateUpdate`) **[src]**; which camera carries the MainCamera tag in the editors, the scene decides, not code **[open]**. ReDefinition switches the blend off for the run -- `KFG_Settings.effectEnabled`, a public static field it reads every frame and never saves -- and shows it in the diagnostics window. |
| **KSPSS** | bingus108, MIT | A research project to bring DLSS, XeSS and FSR to KSP; by its README, validating KSP's pipeline first. | No released runtime to conflict with. |

---

### Other mods in the fetched repositories **[src]**

The sources of 48 mods were searched for what touches the rig: a camera's
`targetTexture`, `OnRenderImage`, command buffers, `Camera.Render`, an assigned
projection, and `ScreenPointToRay`. These are the ones not covered above.

| Mod | What it does | For the rig |
|---|---|---|
| RemoteTech | aims with `ScreenPointToRay` on `FlightCamera.mainCamera` -- Camera 00 -- and on the galaxy camera (`RTUtil`, 572/577) | **[open]** in the upscaling modes the ray misses the cursor by the render ratio, as KSP's own part picking; at AA only it is exact |
| Through The Eyes | aims through the IVA camera with `ScreenPointToRay` (`FirstPersonCameraManager`, 221) | the same **[open]**, since the IVA camera carries the render-size target |
| Kerbal-VR | sets the `targetTexture` of GalaxyCamera, Camera ScaledSpace, Camera 01 and Camera 00 to its own texture (`KVR_ExternalCamera`, 145-148) | takes the very cameras the rig redirects: the two cannot run together; the rig renders no stereo |
| RasterPropMonitor | renders its own cameras into its screens' textures (`FlyingCamera`, `JSIHeadsUpDisplay`) | not the rig's cameras; unaffected |
| SCANsat | its remote view renders its own camera into a texture; aims through the planetarium camera | not redirected; unaffected |
| KSPCommunityFixes | drag cube generation renders its own camera; the vector line fix reads a projection | its own camera; unaffected |
| AtmosphereAutopilot | aims through the planetarium camera | not redirected; unaffected |

How the bundled mods keep their settings:
[how-each-mod-keeps-its-settings.md](how-each-mod-keeps-its-settings.md).

## What the issue trackers say **[doc]**

The KSP forum refuses automated reads with HTTP 403, so this comes from the mods'
GitHub issues and release notes. Every report is about temporal antialiasing, the
class of technique temporal upscalers belong to.

* **Scatterer #252** (open, 2026-04): artifacts on ReStock engine bells with
  Waterfall; they go when Scatterer goes. Ghassen Lahmar's answer: *"Disable TAA
  for now."*
* **Scatterer #231** (2025-02): TUFX's TAA makes planets strobe from a distance.
  The reporter: *"disabling TAA entirely is the only way to fix it."* Ghassen
  Lahmar points to the setting in Scatterer's own TAA that stops jittering
  transparencies. JonnyOThan: *"Waterfall actually has a similar problem in the
  engine bells."* For an upscaler the answer is the opposite. AMD asks for jitter on
  all rendering, transparencies included, and transparencies need a reactive mask
  instead.
* **Parallax Continued #30** (2025-02): the same strobing. Gameslinx: *"I've
  reproduced this without Parallax installed - it's scatterer related."*
* **TUFX #40** (open, 2026-04): *"TAA is still applying to scaled space"*, because
  the logic that skips the scaled camera *"doesn't work properly"*.
* **TUFX #23** (2024-07): reverting to launch from map view with TAA gives
  *"intense flickering and black outlines"*; confirmed with no other mods by
  JonnyOThan. A camera cut the TAA history was not reset for.
* **Deferred #13** (2024-06): a player running SMAA only, because *"TAA's blur
  and destruction of the stars/space in orbit is too distracting"*, asks whether
  DLSS 2 or FSR 2 could use Deferred's depth. Ghassen Lahmar: *"that's not a
  project I'm gonna get into right now."*
* **TUFX release notes:** 1.0.6.0 shipped a ModuleManager patch that disabled
  Scatterer's TAA *"because it interferes with HDR"*; 1.0.8.0 removed it.

Both points apply to ReDefinition. Two temporal filters must not run on top of each
other, which ReDefinition takes care of, and a camera cut must reset the history.

## Camera cuts

AMD: *"In order to indicate to FSR Super Resolution that a jump cut has occurred
with the camera you should set the reset field ... to true for the first frame of
the discontinuous camera transformation."* **[doc]**

* ReDefinition compares the flight camera's target, its parent and the kerbal of the
  IVA view with the last look, and resets the history when one of them changed. It
  looks before the frame's first scene camera culls, and again when the rig
  dispatches. Frame generation gets the same flag in its frame packet, and every mod
  reads it as `Frame.HistoryReset`. Each reset and rebuild is logged with its
  reason.
* A mod reports a cut of its own with `Frame.RequestHistoryReset`
  ([modders/shared-foundation.md](../modders/shared-foundation.md)).
* A change of camera mode rebuilds the rig, since entering IVA enables
  `InternalCamera`, which the rig's camera set did not hold.
* KSP's camera events are not used. They arrive after the rig's update, twice for
  one cut, when nothing cut, or not at all. The IVA portrait buttons move the view to
  another kerbal without one, as `CameraManager.SetCameraIVA` shows, decompiled.
* The history does not reset on a floating origin shift. Above a speed threshold KSP
  shifts it every frame, as `FloatingOrigin.continuous` shows, and a reset every frame
  is an upscaler without history.
* A new parent of the camera is a cut, which is how a camera mod such as Hullcam VDS
  or CameraTools moving the camera is caught. Their rows above say more.

## Open

| Question | Where it stands |
|---|---|
| Whether waving grass, plumes, re-entry and thin trajectory lines ghost under the upscaler | the rows above. The masks are switches in the diagnostics window |
| Singularity in the upscaling modes | not tested here, since it is not installed |
| Kerbal Frame Generator installed alongside | its blend is switched off for the run, and the diagnostics window says so |

What would settle each, with the instruments:
[development/upscaler.md](../development/upscaler.md).

## Sources

Repositories (read locally or through the GitHub API): LGhassen/Scatterer,
LGhassen/EnvironmentalVisualEnhancements, LGhassen/Deferred, LGhassen/Singularity,
KSPModStewards/TUFX (fork of shadowmage45/TUFX), Gameslinx/Parallax-Continued,
KSPModStewards/Waterfall, M1rageDev/Firefly, net-lisias-ksp/DistantObject,
neuoy/KSPTrajectories, linuxgurugamer/HullcamVDSContinued,
BrettRyland/CameraTools, sarbian/SmokeScreen, Vivero/Kerbal-VR,
MangoTechKSP/Kerbal-Frame-Generation-KFG-, bingus108/KSPSS,
KSPModdingLibs/KSPCommunityFixes, zapSNH/ZTheme, KSPModStewards/HUDReplacer,
GPUOpen-LibrariesAndSDKs/FidelityFX-SDK (`super-resolution-upscaler.md`), Unity's
PostProcessing v2 (`Builtins/Uber.shader`, `Builtins/Dithering.hlsl`).
Installed DLLs decompiled with ILSpy: EVE's `Atmosphere.dll`, `Firefly.dll`,
`DistantObject.dll`, `ParallaxContinued.dll`, KSP's `Assembly-CSharp.dll`.
Issues: Scatterer #180, #231, #252; TUFX #23, #40; Parallax-Continued #30;
Deferred #13, #72; TUFX release notes on GitHub.
