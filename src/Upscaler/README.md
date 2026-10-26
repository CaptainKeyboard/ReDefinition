# Upscaler

The rig that puts an upscaler on the scene's camera stack, with the motion vectors it reads, the
fixes for other mods' filters, and the pieces frame generation needs from the game.

## Start here

| File | Read it for |
|---|---|
| `src/Upscaler/Rig/UpscalerRig.cs` | The rig: `UpscalerRig.Setup`, the frame, `UpscalerRig.Present`, `UpscalerRig.Teardown`. |
| `src/Upscaler/Rig/CameraRedirect.cs` | How the 3D stack renders into one texture, with the jitter. |
| `src/Upscaler/Rig/UpscalerCameras.cs` | Which cameras are redirected, and the presenter behind them. |
| `src/Upscaler/Rig/UpscalerInputs.cs` | The capture of depth, motion vectors and masks. |
| `src/Upscaler/MotionVectors/UnityMotionRepair.cs` | The repair of Unity's motion vectors on the flight camera. |
| `src/Upscaler/Compatibility/HostStack.cs` | Other mods' temporal filters: read, switched off, restored. |
| `src/Upscaler/FrameGeneration/VesselShadowLayer.cs` | What frame generation gets that the plain frame lacks. |

## What it does

- `Rig` holds the rig, split over `UpscalerRig.*.cs` by job: the proxy's upscalers, DLSS Ray
  Reconstruction, the HUD-less copy, the hooks other mods register, the preview and the diagnostics.
- `Rig` also holds the rig's parts: cameras, textures, masks, the shader bundle, the mipmap bias
  and the quality overrides.
- `MotionVectors` writes what Unity does not: distant planets, EVE's clouds, skinned meshes and
  the active vessel.
- `MotionVectors/UnityMotionRepair.cs` fills Unity's motion vectors where they were not written.
- `MotionVectors/MotionVectorAudit.cs` checks the vectors on the vessel and the ground while the
  diagnostic measurements are on.
- `Compatibility` sizes EVE's clouds, Scatterer's godrays and Deferred's reflections to the render
  size, and keeps Scatterer's, TUFX's and Deferred's own antialiasing from running twice.
- `Compatibility/TufxPostProcessing.cs` splits TUFX's profile around the upscaler.
- `FrameGeneration` draws the vessel between its physics steps, and takes its shadow out of the
  motion that frame generation follows.

The area is a stop on [A frame in the mod](../../docs/development/flow-frame.md). The rig is made
and torn down as [Startup and scenes](../../docs/development/flow-startup.md) describes.

## What it uses and what uses it

It uses Api, Core, Probe, Proxy, Raytracing and Settings.
Api, Diagnostics, Fsr3, Menu, Modules, Probe, Raytracing, Scenery and Ui name it.

## Rules of the area

- `UpscalerRig.Backend`, `UpscalerRig.Sharpening`, `UpscalerRig.AutoExposure` and
  `UpscalerRig.PassThrough` stay fixed for the life of the rig. If `PassThrough` or the quality mode
  must differ, `src/ReDefinitionAddon.cs` detaches the rig and attaches a new one.
- `UpscalerRig.Teardown` runs once and undoes everything `UpscalerRig.Setup` did
  (`UpscalerRig.TornDown`). Anything the rig changes in the game needs its undo there.
- `src/Upscaler/Rig/QualityOverrides.cs` and `src/Upscaler/Rig/CameraRedirect.cs` remember what
  they replace. `QualityOverrides` puts a value back only if it is still the one written.
- `src/Upscaler/Compatibility` reaches other mods through reflection only, so ReDefinition loads
  without them. `tools/checks/check_foreign_code.ps1` checks what it relies on.
- The `AfterRenderSizeChange` of `EveCompatibility`, `ScattererCompatibility` and
  `DeferredCompatibility` run at the end of the frame, after every camera has rendered
  (`ReDefinitionAddon.FollowRenderSizeAtEndOfFrame`). Each runs on its own, so one failure keeps
  the others' resize.
- `src/Upscaler/Rig/ShaderBundle.cs` loads compute shaders built in `unity/` for Unity 2019.4.18f1.
  The game cannot compile shaders at run time.
