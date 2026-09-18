# Architecture

ReDefinition is four things in one repository:

1. **The settings framework** -- the other mods' settings and key bindings in one
   window, graphics profiles, defaults, requirements, all driven by registrations
   (`src/Settings`, `src/Window`, `GameData/ReDefinition`).
2. **The upscaler** -- on KSP's camera stack, in C# (`src/Upscaler`, `src/Fsr3`): FSR 3
   in Unity, with its compute shaders built into an AssetBundle by a Unity project
   (`unity/`); DLSS and AMD's upscaler DLL run in the proxy on the same inputs
   (`src/Bridges`).
3. **The proxy** -- a native `dxgi.dll` that presents KSP's frames through Direct3D 12:
   frame generation with FSR 3 or DLSS (Streamline), and the DLSS and AMD upscalers
   (`src/DxgiProxy`).
4. **The interface for mods** -- the frame's state, history resets, hooks, key
   bindings, the chosen profile and Direct3D 12 compute passes (`src/Api`,
   `src/Shared`, [shared-foundation.md](shared-foundation.md)).

The C# mod targets .NET Framework 4.8 with C# 7.3, the profile KSP 1.12.5's Mono runs.
Harmony is a requirement (`src/KspAssemblyInfo.cs`).

## Layers

Each folder is one namespace. A layer uses only the layers below it:

| Layer | Folder | Namespace | What |
|---|---|---|---|
| 1 | `src/Core` | `ReDefinition.Core` | the log tag, warnings said a few times at most, types and members looked up by name, the `PluginData` folder |
| 2 | `src/Settings` | `ReDefinition.Settings` | registrations, the bundled mods' settings, profiles, defaults, requirements, key combinations, the settings window's edit model |
| 3 | `src/Upscaler`, `src/Fsr3` | `ReDefinition.Upscaler`, `FidelityFX.FSR3` | the upscaler on KSP's camera stack |
| 3 | `src/Bridges` | `ReDefinition.Bridges` | the managed side of the proxy |
| 3 | `src/Shared` | `ReDefinition.Shared` | the frame's state and hooks behind the interface for mods |
| 4 | `src/` | `ReDefinition` | the add-on, ReDefinition's own settings and modules |
| 4 | `src/Window` | `ReDefinition.Window` | the settings window, the Keys tab, the toolbar, the other mods' windows |
| 4 | `src/Api` | `ReDefinition.Api` | the public interface for mods |

Where a lower layer has to reach a higher one, it offers a hook and the add-on sets it
in its `Awake`: `KspBehaviour.SettingsApplied` (the settings window follows KSP's
settings screen), `KspBehaviour.DlssFrameGenerationRuns` and `DlssFrameGenerationVsync`
(KSP's V-Sync row), `TufxBehaviour.ProfileApplied` (the camera stack put back in order
over a TUFX profile). `UpscalerRig.Current` is the rig in place, set as the add-on
attaches and detaches one.

## The add-on, `src/`

| File | Role |
|---|---|
| `ReDefinitionAddon.cs` | the add-on's lifecycle: builds and tears down the rig per scene and camera mode, the hotkeys, HostStack while a profile is chosen, the hooks of the lower layers |
| `ReDefinitionAddon.Settings.cs` | the settings: what each change does, loading, saving, applying from a settings view |
| `ReDefinitionAddon.Diagnostics.cs` | the diagnostics window, *General* and *Debug* |
| `ReDefinitionAddon.FrameRates.cs` | frame rates with and without the upscaler, rendered and presented, with the load behind them |
| `OwnSettings.cs` | the player's choices in `PluginData/settings.cfg` |
| `GraphicsModule.cs`, `OurModules.cs` | `IGraphicsModule`, `ModuleSetting`: the upscaler and frame generation as modules, in the same model as a mod's settings |
| `ModuleProfiles.cs` | a profile's `MODULE` nodes onto the modules; a profile applied from the main menu |
| `KspAssemblyInfo.cs` | the version KSP sees, Harmony as a dependency |

## Core, `src/Core`

| File | Role |
|---|---|
| `Log.cs` | the tag `[ReDefinition]` every log line begins with |
| `CompatibilityLog.cs` | warnings about other mods, said a few times at most |
| `TypeLookup.cs` | a type by name across loaded assemblies; the binding flags every lookup uses |
| `PluginData.cs` | files in `GameData/ReDefinition/PluginData` |

## The settings framework, `src/Settings`

### Registrations

A mod is bundled from a `MOD_SETTINGS` config node
([modders/registering-a-mod.md](../modders/registering-a-mod.md)).

| File | Role |
|---|---|
| `ModRegistration.cs` | reads a node into a registration: the mod, its settings and key bindings, builds, defaults, profile values, requirements; every problem reported |
| `ModRegistry.cs` | reads all registrations from the GameDatabase once ModuleManager has patched them (`PartLoader` ready) -- one outside ReDefinition's folder over ReDefinition's own of the same name -- and builds a mod for each, by title |
| `RegisteredMod.cs` | an `IBundledMod` built from a registration: detect, version, build, `needs`, `save`, `ready`, member paths, controls, follow-ups, own window; what is left out, with the reason; rows and saved settings the installed build no longer matches |
| `MemberPath.cs` | a C# path from a type to a field, property, indexer or method, resolved once in the mod's own folder (`ModFolder`) |
| `ModBehaviour.cs`, `Behaviours/` | code for what a path cannot say -- KSP's follow-ups and checks, Scatterer's node, EVE's rebuild, TUFX's scenes, Distant Object's hooks, Firefly's text, Parallax's renormalisation; named from a registration with `behaviour` |
| `BundledSetting.cs`, `ApplyWindow.cs` | the model of one setting and of a bundled mod (`IBundledMod`); when a change takes effect |
| `SettingValues.cs` | values as invariant text: conversion and comparison |
| `HarmonyHooks.cs` | Harmony patches installed all or none |
| `KeyCombination.cs`, `KeptBindings.cs` | a binding as up to two modifiers and one key, read and written as text -- free of Unity but for the keys it reads, tested; the bindings ReDefinition keeps for a mod that has none of its own |

### Values: defaults, profiles, requirements

A setting's value is worked out in layers, the same for the window, the profiles and
the reset:

1. **Defaults** of the installed build -- `ModDefaults.cs`, from each registration's
   `default` and `DEFAULTS` blocks.
2. **The chosen profile** over them, quality settings only -- `ModProfiles.cs`, from
   each registration's `PROFILE` blocks; the profile's own name, title and
   ReDefinition's module values from `GRAPHICS_PROFILE` (`GraphicsProfile.cs`,
   `ProfileLibrary.cs`).
3. **What every profile sets**, any kind -- `ModProfiles.SelectAll`, from each
   registration's `ALL_PROFILES` blocks.
4. **Requirements** of the loaded mods over all of it -- `Requirements.cs`, from each
   registration's `REQUIRES`.

The reset takes the defaults and the requirements only. `ProfileApplier.cs` combines
the layers for the registered mods, `ModuleProfiles.cs` (in `src/`) for ReDefinition's
own modules; `ProfileReport.cs` lists the profiles in the log at the main menu.

### The store: what reaches the mods, and when

| File | Role |
|---|---|
| `BundledSettings.cs` | the facade the rest of the mod calls; the game as the store's host |
| `BundledStore.cs` | the hand-over: set, release, reset, correct, restore, reapply; saving through each mod's own routine |
| `BundledLedger.cs` | what is kept: ReDefinition's values, the values from before ReDefinition with their marks, what waits for a mod's save |
| `BundledFile.cs` | `PluginData/bundled.cfg` |
| `BundledSettingsAddon.cs` | when: before a scene is requested, when it has loaded and when it is ready, when KSP's settings are applied, at camera changes, on a two-second tick, at the end of a frame |

The rules: [settings-store.md](settings-store.md).

### The edit model

| File | Role |
|---|---|
| `SettingsEdit.cs` | the settings window's edit model: rows, where their values came from, filling from a profile or the reset, the status line, *Apply* as steps -- free of Unity, tested |
| `WindowLayout.cs` | which settings show in which tab, in what order; the *Advanced* buttons |

## The upscaler, `src/Upscaler` and `src/Fsr3`

### The rig

| File | Role |
|---|---|
| `UpscalerRig.cs` | one upscaler on one scene: render targets, jitter, the captures of colour, depth and motion vectors, the dispatch, setup and teardown |
| `UpscalerRig.CameraMotion.cs` | the camera motion and floating origin instrument; the fast-turn switch |
| `UpscalerRig.Hooks.cs`, `UpscalerOverlay.cs` | the hooks other mods register: motion vectors into the capture, the upscaled image, the overlay camera |
| `UpscalerRig.HudLess.cs` | frame generation's HUD-less copy of the backbuffer |
| `UpscalerRig.Native.cs` | DLSS and AMD's DLL in the proxy: textures handed over, packets, the proxy's state |
| `UpscalerRig.Diagnostics.cs`, `.Preview.cs`, `CameraSurvey.cs` | *Write diagnostics to log*, the input preview, the cameras in order |
| `CameraRedirect.cs` | redirects KSP's 3D camera stack into one render-size target, jitters it, and puts back each camera's target and projection |
| `UpscalerPresenter.cs` | the presenter camera that draws the result into the frame buffer before the UI cameras |
| `FrameRateMeter.cs` | frame times with and without the upscaler |

### Inputs and the game's state

| File | Role |
|---|---|
| `QualityOverrides.cs` | while the upscaler runs: MSAA off, LOD bias compensated, anisotropic filtering forced on; taken back when it stops |
| `KspMipmapBias.cs` | the negative mipmap bias, on textures of renderers the redirected cameras see |
| `SkinnedMotionVectors.cs` | kerbals and flags drawing their own motion vectors |
| `UpscalerMasks.cs` | FSR 3's transparency and reactive masks |
| `EveCloudMotion.cs`, `CloudMotionVectors.cs` | EVE's clouds jittered, and their motion vectors blended into the captured ones |
| `TufxPostProcessing.cs` | TUFX's effects split around the upscaler |
| `HostStack.cs` | other mods' temporal and spatial antialiasing switched off, and what Scatterer's TAA leaves behind |
| `ScattererCompatibility.cs`, `EveCompatibility.cs` | Scatterer's godrays and EVE's clouds sized for redirected cameras |

### Techniques

| File | Role |
|---|---|
| `src/Fsr3/` | FSR3Unity (MIT), adapted for Unity 2019.4 and KSP ([upscaler.md](upscaler.md), "FSR3Unity on Unity 2019.4") |
| `FsrShaderBundle.cs` | loads the compute shaders from `Shaders/redefinition.shaders` |
| `RcasSharpener.cs` | FSR 3's RCAS pass on its own, for the sharpness slider with DLSS |

## The proxy's managed side, `src/Bridges`

| File | Role |
|---|---|
| `NativeUpscalerLink.cs`, `DlssBridge.cs`, `AmdUpscalerBridge.cs` | the managed side of the upscalers in the proxy |
| `FrameGenerationBridge.cs` | the managed side of frame generation: inputs registered, a frame packet per frame, state and counters |
| `D3d12Bridge.cs` | the managed side of Direct3D 12 for mods: packets, render events, status |
| `PacketRing.cs` | the packet slots render events read on Unity's render thread |
| `StreamlineCamera.cs` | the camera matrices DLSS frame generation takes, in Streamline's form |
| `NvidiaFiles.cs` | which of NVIDIA's DLLs the GPU can use, and where they come from |

## The interface for mods, `src/Api` and `src/Shared`

| File | Role |
|---|---|
| `Api/ApiInfo.cs`, `Frame.cs`, `Hooks.cs`, `Keys.cs`, `Profiles.cs`, `D3D12.cs` | `ReDefinition.Api`, public: the interface's version, the frame's state and resets, the hooks, the key bindings, the chosen profile, Direct3D 12 |
| `Shared/SharedFrame.cs` | the frame's state, decided before its first scene camera culls, as properties and shader globals; the hooks' lists; the profile's changes |
| `Shared/CameraCuts.cs`, `HistoryResets.cs` | KSP's camera cuts; which frame resets for whom -- free of Unity, tested |
| `Shared/HookList.cs` | handlers run each on its own, one that throws removed |
| `docs/modders/examples/ReDefinitionApi.cs` | the wrapper mods copy, binding the interface once as typed delegates |
| `docs/modders/examples/ReDefinitionExample` | the example mod, built with the solution |
| `unity/Assets/ReDefinition/Include` | `ReDefinition.cginc`, the shader include for mods, and the shader that checks it (`Editor/IncludeCheck.cs`) |

The reference for mod authors: [modders/shared-foundation.md](../modders/shared-foundation.md).

How a frame goes through it: [upscaler.md](upscaler.md).

## The windows, `src/Window`

| File | Role |
|---|---|
| `SettingsWindow.cs`, `TabScrollList.cs` | the settings window's view, from KSP's dialog elements |
| `SettingsWindow.Keys.cs`, `KeyCapture.cs`, `Conflicts.cs`, `KspKeyBindings.cs` | the Keys tab: the rows that take a combination, the capture with KSP's controls locked, the shared combinations shown in yellow, and KSP's own bindings read from `GameSettings` |
| `KspSettingsSection.cs` | the section in KSP's settings dialog, through Harmony postfixes on `VideoSettings` |
| `ToolbarButton.cs` | the toolbar button |
| `ToolbarTakeover.cs`, `BundleNotice.cs` | hiding the bundled mods' toolbar buttons where their window is reachable; the main menu's first question |
| `ModWindowClose.cs` | the close button on a mod's own settings window, and knowing whether that window is open |
| `ModWindowsAddon.cs` | when: the close buttons installed, the toolbar looked at, the open settings window following a change made in another window |
| `UnityMouseEvents.cs` | Unity's mouse events on redirected cameras, and clicks on ReDefinition's windows kept from the scene |
| `NvidiaDownloader.cs` | the download of NVIDIA's DLLs from NVIDIA's Streamline release after licence consent |

## The proxy, `src/DxgiProxy`

| File | Role |
|---|---|
| `DxgiExports.cpp` | `dxgi.dll`'s exports, forwarded to Windows' own; the hook on `CreateSwapChainForHwnd` |
| `SwapChainProxy.h`, `SwapChainProxy.cpp` | the swapchain Unity gets: made through Streamline, FidelityFX or DXGI, with a shared Direct3D 11 texture as Unity's backbuffer; resize and teardown |
| `SwapChainProxyPresent.cpp` | the present path: Unity's frame copied into the Direct3D 12 backbuffer, frame generation prepared under the same lock, the fences between Direct3D 11, Direct3D 12 and DLSS-G's queue |
| `SwapChainProxyPacing.cpp` | FSR's pacing settings, the monitor's refresh rate, the frame limits, the frame time report |
| `SwapChainProxyForwarding.cpp` | COM identity and the calls the real swapchain answers |
| `SwapChainProxyState.h` | what the proxy's swapchains share process-wide |
| `FrameGeneration.h`, `FrameGeneration.cpp` | frame generation's owner and FSR's context: made, retried, configured, destroyed, prepared per present |
| `FrameGenerationInputs.cpp` | the textures shared between Unity's Direct3D 11 and the proxy's Direct3D 12 device, copied at present and flipped where Unity renders upside down |
| `FrameGenerationCheck.cpp` | the HUD-less and motion vector checks, read back and logged |
| `FrameGenerationDlss.cpp` | the same inputs handed to DLSS frame generation through Streamline |
| `Streamline.h`, `Streamline.cpp` | Streamline 2.14.1, loaded from the player's files, with NVIDIA's signature check |
| `Dlss.h`, `Dlss.cpp` | DLSS through NGX on Unity's Direct3D 11 device |
| `AmdUpscaler.h`, `AmdUpscaler.cpp` | AMD's upscaler DLL on the proxy's Direct3D 12 device |
| `D3d12Compute.h`, `D3d12Compute.cpp` | Direct3D 12 for mods: capabilities, HLSL compiled with Windows' `d3dcompiler_47.dll`, compute passes against one root signature, textures shared with Direct3D 11 |
| `FidelityFx.h`, `FidelityFx.cpp` | AMD's frame generation runtime, loaded at run time through a function table |
| `NvidiaGpu.h`, `NvidiaGpu.cpp` | the GPU's architecture through NVAPI, for what DLSS and DLSS-G need |
| `ProjectIdentity.h` | what ReDefinition tells NVIDIA's libraries it is |
| `ManagedBridge.cpp` | the exports the C# bridges call |
| `Config.h`, `Config.cpp` | `ReDefinitionProxy.ini`, re-read while the game runs |
| `Log.h`, `Log.cpp`, `LoadMonitor.h`, `LoadMonitor.cpp` | `ReDefinitionProxy.log`; CPU and GPU load |
| `FileUtil.h`, `FileUtil.cpp`, `D3d12Util.h` | paths next to the executable, file facts; Direct3D 12 resource transitions |
| `ProxyHarness.cpp`, `HarnessPass.hlsl` | a test driver that does what Unity does, without KSP; its compute pass for Direct3D 12 for mods |
| `extern/` | AMD's FidelityFX headers and NVIDIA's Streamline headers (MIT) |

Design and measurements: [frame-generation.md](frame-generation.md).

## Around the code

| Where | What |
|---|---|
| `GameData/ReDefinition/Mods` | the registrations of KSP and the eight bundled mods |
| `GameData/ReDefinition/Profiles` | the five profiles |
| `unity/Assets/ReDefinition` | the ported FSR 3 compute shaders and `Editor/BundleBuilder.cs`; the shader include for mods and `Editor/IncludeCheck.cs` |
| `tests/ReDefinition.Tests` | MSTest on .NET Framework 4.8: the store, the file, the edit model, the modules, registrations, frame packet layout, Streamline's camera matrices, EVE's cloud motion, TUFX's split, NVIDIA's files, the interface for mods and its wrapper |
| `tools/check_bundled_mods.ps1`, `tools/check_profile_parser.ps1` | checks against the installed mods and KSP's own `ConfigNode` |
| `tools/port_fsr3_shaders.py`, `tools/audit_ffx_fields.py`, `tools/fetch_amd_runtime.py` | the FSR shaders from FSR3Unity's, the frame generation field audit, AMD's runtime for the package |

How to build and run all of it: [building-and-testing.md](building-and-testing.md).

## Conventions

* No other mod is a reference: every mod is reached through reflection, by names
  checked against the installed build (`tools/check_bundled_mods.ps1`).
* A setting's key, `<mod>.<setting>`, never changes: `bundled.cfg`, profiles and
  packs' patches name it.
* A member a build does not have drops its setting with the reason in the log.
* Nothing is kept for earlier builds of ReDefinition itself until the first release:
  a format changes in place.
* Code comments say what the code does and why; longer reasons and research live in
  these pages, and comments point here.
