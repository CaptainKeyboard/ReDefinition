# Upscaler

The integration that puts an upscaler on the scene's camera stack, with the motion vectors it reads, the
fixes for other mods' filters, and the pieces frame generation needs from the game.

## Start here

| File | Read it for |
|---|---|
| `src/Upscaler/Integration/UpscalerIntegration.cs` | The integration: `UpscalerIntegration.Setup`, the frame, `UpscalerIntegration.Present`, `UpscalerIntegration.Teardown`. |
| `src/Upscaler/Integration/CameraRedirect.cs` | How the 3D stack renders into one texture, with the jitter. |
| `src/Upscaler/Integration/UpscalerCameras.cs` | Which cameras are redirected, and the presenter behind them. |
| `src/Upscaler/Integration/UpscalerInputs.cs` | The capture of depth, motion vectors and masks. |
| `src/Upscaler/MotionVectors/UnityMotionRepair.cs` | The repair of Unity's motion vectors on the flight camera. |
| `src/Upscaler/Compatibility/HostStack.cs` | Other mods' temporal filters: read, switched off, restored. |
| `src/Upscaler/Compatibility/IHostFilter.cs` | What each filter's control does for `HostStack`: apply, reassert, restore, report. |
| `src/Upscaler/FrameGeneration/VesselShadowLayer.cs` | What frame generation gets that the plain frame lacks. |

## What it does

- `Integration` holds the integration, split over `UpscalerIntegration.*.cs` by job: the proxy's upscalers, DLSS Ray
  Reconstruction, the hooks other mods register, the preview and the diagnostics.
- `Integration/HudLessCapture.cs` holds frame generation's HUD-less copy of the backbuffer,
  `Integration/CameraMotionMeter.cs` the instrument for camera turns, cuts and floating origin shifts, and
  `Integration/IntegrationHooks.cs` the calls of the hooks other mods register.
- `Integration` also holds the integration's parts: cameras, textures, masks, the shader bundle, the mipmap bias
  and the quality overrides.
- `Integration/IntegrationReport.cs`, `Integration/IntegrationReadback.cs` and `Integration/IntegrationPreview.cs` hold the text and statistics of the
  diagnostics, the CPU readback they use, and the tinted preview of the diagnostics window.
- `MotionVectors` writes what Unity does not: distant planets, EVE's clouds, skinned meshes and
  the active vessel.
- `MotionVectors/UnityMotionRepair.cs` fills Unity's motion vectors where they were not written.
- `MotionVectors/MotionVectorAudit.cs` checks the vectors on the vessel and the ground while the
  diagnostic measurements are on.
- `Compatibility` sizes EVE's clouds, Scatterer's godrays and Deferred's reflections to the render
  size, and keeps Scatterer's, TUFX's and Deferred's own antialiasing from running twice.
- `Compatibility/TufxPostProcessing.cs` splits TUFX's profile around the upscaler.
- `Compatibility` holds one control per filter that `HostStack` coordinates: `ScattererTaaControl`,
  `TufxAaControl`, `SmaaControl` and `KfgControl`. `DuplicateAaControl` keeps a second antialiasing off a
  camera. `ForeignReflection` and `HostCameras` are what they share.
- `FrameGeneration` draws the vessel between its physics steps, and takes its shadow out of the
  motion that frame generation follows.

The area is a stop on [A frame in the mod](../../docs/development/flow-frame.md). The integration is made
and torn down as [Startup and scenes](../../docs/development/flow-startup.md) describes.

## What it uses and what uses it

It uses Api, Core, Probe, Proxy, Raytracing and Settings.
Api, Diagnostics, Fsr3, Menu, Modules, Probe, Raytracing, Scenery and Ui name it.

## Rules of the area

- `UpscalerIntegration.Backend`, `UpscalerIntegration.Sharpening`, `UpscalerIntegration.AutoExposure` and
  `UpscalerIntegration.PassThrough` stay fixed for the life of the integration. If `PassThrough` or the quality mode
  must differ, `src/ReDefinitionAddon.cs` detaches the integration and attaches a new one.
- `UpscalerIntegration.Teardown` runs once and undoes everything `UpscalerIntegration.Setup` did
  (`UpscalerIntegration.TornDown`). Anything the integration changes in the game needs its undo there.
- `src/Upscaler/Integration/QualityOverrides.cs` and `src/Upscaler/Integration/CameraRedirect.cs` remember what
  they replace. `QualityOverrides` puts a value back only if it is still the one written.
- `src/Upscaler/Compatibility` reaches other mods through reflection only, so ReDefinition loads
  without them. `tools/checks/check_foreign_code.ps1` checks what it relies on.
- The `AfterRenderSizeChange` of `EveCompatibility`, `ScattererCompatibility` and
  `DeferredCompatibility` run at the end of the frame, after every camera has rendered
  (`ReDefinitionAddon.FollowRenderSizeAtEndOfFrame`). Each runs on its own, so one failure keeps
  the others' resize.
- `src/Upscaler/Integration/ShaderBundle.cs` loads compute shaders built in `unity/` for Unity 2019.4.18f1.
  The game cannot compile shaders at run time.
