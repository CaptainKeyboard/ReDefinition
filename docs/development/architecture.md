# Architecture

**For:** anyone changing ReDefinition's code, or looking for the file that does a thing.
**You need:** the repository. Nothing has to be built.
**You get:** the parts of the mod, the layers its folders form, and what each file is
for.

## The parts

ReDefinition is six things in one repository.

1. **The settings framework.** The other mods' settings and key bindings in one window,
   graphics profiles, defaults and requirements, all driven by registrations. It lives in
   `src/Settings`, `src/Window` and `GameData/ReDefinition`.
2. **The upscaler.** FSR 3 in Unity, on KSP's camera stack, in C# (`src/Upscaler`,
   `src/Fsr3`). Its compute shaders come as an AssetBundle that the Unity project in
   `unity/` builds. DLSS, AMD's upscaler DLL and XeSS run in the proxy on the same inputs
   (`src/Bridges`).
3. **The raytracing.** Sun shadows, diffuse light and reflections traced on the proxy's
   Direct3D 12 device, fed from KSP's scene and written back into Unity's lighting
   (`src/Raytracing`, the proxy's `Raytracing*.cpp`).
4. **The proxy.** A native `dxgi.dll` that presents KSP's frames through Direct3D 12
   (`src/DxgiProxy`). It runs frame generation with FSR 3, with DLSS through Streamline or
   with XeSS, the DLSS, AMD and XeSS upscalers, the latency brake and Reflex, XeLL and
   Anti-Lag 2, the raytracing, and the compute passes other mods ask for.
5. **The interface for mods.** The frame's state, history resets, hooks, key bindings, the
   chosen profile and Direct3D 12 compute passes (`src/Api`, `src/Shared`). Why it exists:
   [shared-foundation.md](shared-foundation.md).
6. **KSP's own interface and scenery.** The flight HUD and the flight panel, the staging
   details, the planner in the editors, the UI scaling of what KSP leaves at a fixed
   size, and the main menu and space centre scenery (`src/Flight`, `src/Engineer`,
   `src/Scaling`, `src/Menu`, `src/Scenery`).

The C# mod targets .NET Framework 4.8 with C# 7.3, the profile KSP 1.12.5's Mono runs.
Harmony is a requirement (`src/KspAssemblyInfo.cs`).

## The layers

Each folder is one namespace, but for `src/Fsr3`, which keeps FSR3Unity's two, and the
subfolders of `src/Flight` and `src/Raytracing`. A folder uses its own layer and the
layers below it. The upscaler alone reaches up: it reads the add-on's debug mode
(`ReDefinitionAddon.DebugMode`, *Measurements into the log*), and takes Parallax's
scatter materials for the mipmap bias from `RtKspMods` and the vessel's parts from
`RtFilter`, both in `src/Raytracing/Ksp`.

| Layer | Folder | Namespace | What |
|---|---|---|---|
| 1 | `src/Core` | `ReDefinition.Core` | the log tag, warnings logged a few times at most, types and members looked up by name, the `PluginData` folder |
| 2 | `src/Settings` | `ReDefinition.Settings` | registrations, the bundled mods' settings, profiles, defaults, requirements, key combinations, the settings window's edit model |
| 3 | `src/Upscaler`, `src/Fsr3` | `ReDefinition.Upscaler`, `FidelityFX.FSR3`, `FidelityFX` | the upscaler on KSP's camera stack |
| 3 | `src/Bridges` | `ReDefinition.Bridges` | the managed side of the proxy |
| 3 | `src/Shared` | `ReDefinition.Shared` | the frame's state and hooks behind the interface for mods |
| 3 | `src/Raytracing` | `ReDefinition.Raytracing` | the raytracing's managed side, free of KSP's types |
| 4 | `src/` | `ReDefinition` | the add-on, ReDefinition's own settings and modules |
| 4 | `src/Window` | `ReDefinition.Window` | the settings window, the *Controls* tab, the toolbar, KSP's settings dialog and pause menu, the other mods' windows |
| 4 | `src/Api` | `ReDefinition.Api` | the public interface for mods |
| 5 | `src/Raytracing/Ksp` | `ReDefinition.Raytracing` | the raytracing fed from KSP and the mods, with the player's settings |
| 5 | `src/Engineer` | `ReDefinition.Engineer` | the planner in the VAB and the SPH |
| 5 | `src/Flight` | `ReDefinition.Flight`, `.Panel`, `.Readouts` | the flight HUD, the flight panel, the staging details, the world buttons, the leg grip |
| 5 | `src/Scaling` | `ReDefinition.Scaling` | the UI scale for what KSP draws at a fixed size, and other mods' windows |
| 5 | `src/Menu`, `src/Scenery` | `ReDefinition.Menu`, `ReDefinition.Scenery` | the main menu's fireworks and the flight to the space centre; the space centre's grass and runway lights, KSP's reflection probe, Firefly at rest |
| 6 | `src/Probe` | `ReDefinition.Probe` | measurements started from KSP's command line |

## How a lower layer reaches a higher one

If a lower layer has to reach a higher one, it offers a hook. The add-on sets that hook
in its `Awake`.

| Hook | What it is for |
|---|---|
| `KspBehaviour.SettingsApplied` | the settings window follows KSP's settings screen |
| `KspBehaviour.DlssFrameGenerationRuns`, `KspBehaviour.DlssFrameGenerationVsync` | KSP's V-Sync row |
| `TufxBehaviour.ProfileApplied` | the camera stack is put back in order over a TUFX profile |

`UpscalerRig.Current` is the rig in place. The add-on sets it as it attaches and detaches
one.

## The add-on, `src/`

| File | Role |
|---|---|
| `ReDefinitionAddon.cs` | the add-on's lifecycle: builds and tears down the rig per scene and camera mode, the hotkeys, HostStack while the upscaler runs, the hooks of the lower layers |
| `ReDefinitionAddon.Settings.cs` | the settings: what each change does, loading, saving, applying from a settings view |
| `ReDefinitionAddon.Diagnostics.cs` | the diagnostics window's shell: the window, the list of pages, the changes that wait for the layout pass, the pins at its foot, the scene probe's commands |
| `ReDefinitionAddon.DiagnosticsLiveStrip.cs` | the live strip: what runs, the frame rates, the frame time graph, the latency and the load; the search, *Changed* and *Reset debug switches* |
| `ReDefinitionAddon.DiagnosticsPages.cs` | the pages: *Overview*, the pages of options with the facts beside them, *Previews*, and the list of search results |
| `ReDefinitionAddon.DiagnosticsOptions.cs` | the window's options as data: label, page, group, default, whether it is saved, why it does not apply, and the setter each one calls |
| `ReDefinitionAddon.DiagnosticsRows.cs` | how an option is drawn by its kind, with its star and marker, and the pinned options at the foot |
| `ReDefinitionAddon.DiagnosticsMods.cs` | *Other mods*: three lists of lines, their state, the registrations and their code |
| `ReDefinitionAddon.FrameRates.cs` | frame rates with and without the upscaler, rendered and presented, with the load behind them |
| `OwnSettings.cs` | the player's choices in `PluginData/settings.cfg` |
| `GraphicsModule.cs`, `OurModules.cs` | `IGraphicsModule`, `ModuleSetting`: the upscaler and frame generation as modules, in the same model as a mod's settings |
| `ModuleProfiles.cs` | a profile's `MODULE` nodes onto the modules; a profile applied from the main menu |
| `KspAssemblyInfo.cs` | the version KSP sees, Harmony as a dependency |

## Core, `src/Core`

| File | Role |
|---|---|
| `Log.cs` | the tag `[ReDefinition]` every log line begins with |
| `CompatibilityLog.cs` | warnings about other mods, logged a few times at most |
| `TypeLookup.cs` | a type by name across loaded assemblies; the binding flags every lookup uses |
| `CompiledGetter.cs` | another mod's field or property read through a compiled getter, without boxing, where it is read every frame |
| `HarmonyHooks.cs` | Harmony patches installed all or none |
| `ForeignAssumptions.cs`, `ForeignAssumption.cs` | the catalogue of what ReDefinition assumes about other mods' code, each with the build it was checked against and what happens where that changed |
| `ForeignCode.cs` | a fingerprint of another mod's method from its instructions, the same in KSP's Mono and outside it |
| `PluginData.cs` | files in `GameData/ReDefinition/PluginData` |
| `CaptureFolder.cs` | `ReDefinitionCaptures` beside `KSP_x64.exe`, where the probes, tests and dumps write |

`src/ForeignAssumptionReport.cs` writes what came of each assumption into `KSP.log` and
the diagnostics window's *Other mods* page; `tools/check_foreign_code.ps1` checks the
catalogue outside the game
([building-and-testing.md](building-and-testing.md#run-the-check-scripts)).

## The settings framework, `src/Settings`

### Registrations

A mod is bundled from a `MOD_SETTINGS` config node
([modders/registering-a-mod.md](../modders/registering-a-mod.md)).

| File | Role |
|---|---|
| `ModRegistration.cs` | reads a node into a registration: the mod, its settings and key bindings, builds, defaults, profile values, requirements; every problem reported |
| `ModRegistry.cs` | reads all registrations from the GameDatabase once ModuleManager has patched them (`PartLoader` ready), and builds a mod for each, by title. A registration outside ReDefinition's folder wins over ReDefinition's own of the same name |
| `RegisteredMod.cs` | an `IBundledMod` built from a registration: detect, version, build, `needs`, `save`, `ready`, member paths, controls, follow-ups, own window; what is left out, with the reason; rows and saved settings the installed build no longer matches |
| `MemberPath.cs` | a C# path from a type to a field, property, indexer or method, resolved once in the mod's own folder (`ModFolder`) |
| `ModBehaviour.cs`, `Behaviours/` | code for what a member path cannot say: KSP's follow-ups and checks, Scatterer's node, EVE's rebuild, TUFX's scenes, Distant Object's hooks, Firefly's text, Parallax's renormalisation. A registration names one with `behaviour`, for the mod or for one setting. `Behaviours/ProvidedSettings.cs` answers for a type the mod itself brings, reached by name and signature |
| `BundledSetting.cs`, `ApplyWindow.cs` | the model of one setting and of a bundled mod (`IBundledMod`); when a change takes effect |
| `SettingValues.cs` | values as invariant text: conversion and comparison |
| `KeyCombination.cs`, `KeptBindings.cs` | a binding as up to two modifiers and one key, read and written as text, free of Unity but for the keys it reads, tested; the bindings ReDefinition keeps for a mod that has none of its own |

### Defaults, profiles and requirements

A setting's value is worked out in layers. The window, the profiles and the reset all use
the same ones.

| Order | What it adds | Where it comes from |
|---|---|---|
| 1 | the defaults of the installed build | each registration's `default` and `DEFAULTS` blocks, read by `ModDefaults.cs` |
| 2 | the chosen profile over them, quality settings only | each registration's `PROFILE` blocks, read by `ModProfiles.cs` |
| 3 | what every profile sets, of any kind | each registration's `ALL_PROFILES` blocks, read by `ModProfiles.SelectAll` |
| 4 | the requirements of the loaded mods, over all of it | each registration's `REQUIRES`, read by `Requirements.cs` |

A profile's own name, its title and ReDefinition's module values come from its
`GRAPHICS_PROFILE` node (`GraphicsProfile.cs`, `ProfileLibrary.cs`).

The reset takes the defaults and the requirements, then chooses High over them
(`SettingsWindow.ChooseDefaults`). While a reset is chosen, a profile hands nothing back,
and *Accept* sets every row the profile filled, since `KspReset` has put KSP's defaults
there first. `ProfileApplier.cs` combines the
layers for the registered mods, and `ModuleProfiles.cs` in `src/` does it for
ReDefinition's own modules. `ProfileReport.cs` lists the profiles in the log at the main
menu.

### The store: what reaches the mods, and when

| File | Role |
|---|---|
| `BundledSettings.cs` | the facade the rest of the mod calls; the game as the store's host |
| `BundledStore.cs` | the hand-over: set, release, reset, correct, restore, reapply; saving through each mod's own routine |
| `BundledLedger.cs` | what is kept: ReDefinition's values, the values from before ReDefinition with their marks, what waits for a mod's save |
| `BundledFile.cs` | `PluginData/bundled.cfg` |
| `BundledSettingsAddon.cs` | when: before a scene is requested, when it has loaded and when it is ready, when KSP's settings are applied, at camera changes, on a two-second tick, at the end of a frame |

A setting whose registration says `bundled = False` never reaches the store: the window
writes it straight into its mod and calls the mod's save, as does every row of a mod
whose registration says `direct` while the bundling is off. KSP's audio, gameplay,
system and input settings are such settings. `KspReset.cs` is KSP's own reset, which
*Reset* runs before the store resets the bundled rows.

The rules the store follows: [settings-store.md](settings-store.md).

### The edit model

| File | Role |
|---|---|
| `SettingsEdit.cs` | the settings window's edit model: rows, where their values came from, filling from a profile or the reset, the status line, *Accept* as steps; free of Unity, tested |
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
| `UpscalerRig.Reconstruction.cs` | DLSS Ray Reconstruction in DLSS Super Resolution's place while the scene camera is raytraced |
| `UpscalerRig.Diagnostics.cs`, `.Preview.cs`, `CameraSurvey.cs` | *Write diagnostics to log*, the input preview, the cameras in order |
| `UpscalerCameras.cs` | the 3D stack redirected and the presenter camera behind it |
| `CameraRedirect.cs` | redirects KSP's 3D camera stack into one render-size target, jitters it, and puts back each camera's target and projection |
| `ScreenCoordinates.cs` | the screen's pixels for the redirected cameras' conversions between screen and world, through Harmony |
| `UpscalerPresenter.cs` | the presenter camera that draws the result into the frame buffer before the UI cameras |
| `UpscalerTextures.cs`, `RenderTextures.cs` | the rig's textures and the native handles the proxy is given; render textures made, checked and released |
| `RenderInterpolation.cs`, `StepInterpolator.cs` | the active vessel drawn between its last two physics steps; the arithmetic of it, free of KSP, tested |
| `VesselShadowLayer.cs` | the active vessel's shadow kept out of frame generation's motion |
| `StageDump.cs` | the frame's stages written with the diagnostics window's screen recording |
| `FrameRateMeter.cs` | frame times with and without the upscaler |

### Inputs and the game's state

| File | Role |
|---|---|
| `QualityOverrides.cs` | while the upscaler runs: MSAA off, LOD bias compensated, anisotropic filtering forced on; taken back when it stops |
| `KspMipmapBias.cs` | the negative mipmap bias, on textures of renderers the redirected cameras see |
| `SkinnedMotionVectors.cs` | kerbals and flags drawing their own motion vectors |
| `UpscalerInputs.cs` | the capture buffer that copies depth and motion vectors on the scene camera, with the distant planets, EVE's clouds and the masks in them |
| `UpscalerMasks.cs` | the transparency and reactive masks for FSR 3 and AMD's upscaler |
| `EveCloudMotion.cs`, `CloudMotionVectors.cs` | EVE's clouds jittered, and their motion vectors blended into the captured ones |
| `ScaledSpaceMotion.cs` | the distant planets' motion vectors, computed from the scaled bodies' transforms where the scene camera draws nothing |
| `UnityMotionRepair.cs` | Unity's motion vectors of the flight camera, written with the camera's own move where Unity left them, for every reader |
| `SceneMotion.cs` | the scene camera's move against the main body as one clip-space matrix, in double precision, for the motion vectors of far ground |
| `VesselMotionVectors.cs` | the active vessel's motion vectors written by the rig, and checked on every pixel |
| `MotionVectorAudit.cs` | an instrument: the captured motion vectors against the true motion at sample points |
| `TufxPostProcessing.cs` | TUFX's effects split around the upscaler |
| `HostStack.cs` | other mods' temporal and spatial antialiasing switched off, and what Scatterer's TAA leaves behind; one partial file per mod: `.ScattererTaa`, `.Smaa`, `.Kfg`, `.Tufx` |
| `ScattererCompatibility.cs`, `EveCompatibility.cs`, `DeferredCompatibility.cs` | Scatterer's godrays, EVE's clouds and Deferred's screen-space reflections sized for redirected cameras |

### Techniques

| File | Role |
|---|---|
| `src/Fsr3/` | FSR3Unity (MIT), adapted for Unity 2019.4 and KSP ([upscaler.md](upscaler.md), "FSR3Unity on Unity 2019.4") |
| `ShaderBundle.cs` | loads the compute shaders and ReDefinition's own shaders from `Shaders/redefinition.shaders` |
| `RcasSharpener.cs` | FSR 3's RCAS pass on its own, for the sharpness slider with DLSS |

How a frame goes through all of it: [upscaler.md](upscaler.md).

## The proxy's managed side, `src/Bridges`

| File | Role |
|---|---|
| `NativeUpscalerLink.cs`, `DlssBridge.cs`, `AmdUpscalerBridge.cs`, `XessUpscalerBridge.cs` | the managed side of the upscalers in the proxy |
| `FrameGenerationBridge.cs` | the managed side of frame generation: inputs registered, a frame packet per frame, state and counters |
| `D3d12Bridge.cs` | the managed side of Direct3D 12 for mods: packets, render events, status |
| `PacketRing.cs` | the packet slots render events read on Unity's render thread |
| `StreamlineCamera.cs` | the camera matrices DLSS frame generation takes, in Streamline's form |
| `ReflexFrames.cs` | the frame's start and simulation markers on the main thread, and the frame index to the render thread; what lowers the latency, and the latency measure ([low-latency.md](low-latency.md)) |
| `FrameGenerationChoice.cs` | the *Frame generation* row's choices, *Off*, 2x to 6x and *Dynamic*, and which of them the technique that runs allows |
| `AmperePackage.cs`, `AmpereOptions.cs` | dlssg_for_sm86: the release zip, what is taken out of it, and the four choices written into its ini |
| `NvidiaFiles.cs` | which of NVIDIA's DLLs the GPU can use, and where they come from |
| `ProxyLoad.cs` | whether the `dxgi.dll` next to `KSP_x64.exe` is the one the game loaded, or Windows refused it |
| `ScreenRecordingBridge.cs` | the proxy's screen recording for the diagnostics window |

## The interface for mods, `src/Api` and `src/Shared`

| File | Role |
|---|---|
| `Api/ApiInfo.cs`, `Frame.cs`, `Hooks.cs`, `Keys.cs`, `Profiles.cs`, `D3D12.cs` | `ReDefinition.Api`, public: the interface's version, the frame's state and resets, the hooks, the key bindings, the chosen profile, Direct3D 12 |
| `Shared/SharedFrame.cs` | the frame's state, decided before its first scene camera culls, as properties and shader globals; the hooks' lists; the profile's changes |
| `Shared/CameraCuts.cs`, `HistoryResets.cs` | KSP's camera cuts; which frame resets for whom; free of Unity, tested |
| `Shared/HookList.cs` | handlers run each on its own, one that throws removed |
| `Shared/IconSprites.cs`, `Shared/TexturePixels.cs` | sprites of whole textures from `GameData`, made once; pixels of textures that are not readable, read back through a render texture |
| `docs/modders/examples/ReDefinitionApi.cs` | the wrapper mods copy, binding the interface once as typed delegates |
| `docs/modders/examples/ReDefinitionExample` | the example mod, built with the solution |
| `unity/Assets/ReDefinition/Include` | `ReDefinition.cginc`, the shader include for mods, and the shader that checks it (`Editor/IncludeCheck.cs`) |

The reference for mod authors: [modders/shared-foundation.md](../modders/shared-foundation.md).

## The windows, `src/Window`

| File | Role |
|---|---|
| `SettingsWindow.cs`, `TabScrollList.cs` | the settings window's view, from KSP's dialog elements |
| `SettingsWindow.Navigation.cs`, `.Folds.cs` | the categories as a tree, the search, and the groups that open and fold in the long tabs |
| `SettingsWindow.BundledRows.cs` | a bundled mod's row over the edit model |
| `SettingsWindow.Profiles.cs` | the *Graphics* tab's profiles, their status line and *Reset* |
| `SettingsWindow.Apply.cs`, `.Waiting.cs` | *Apply* and *Accept* as one ordered pipeline, *Cancel*; the changes that wait for the next scene or a restart, in yellow |
| `SettingsWindow.Interface.cs` | the *Mods / Toolbar* tab |
| `KspShipped.cs` | what KSP ships for its settings, read without touching the player's |
| `ScreenFit.cs` | the settings window and ReDefinition's dialogs kept within the screen at a large UI scale ([ui-scaling.md](ui-scaling.md)) |
| `ConfirmDialog.cs`, `TooltipText.cs` | a question with a button and KSP's *Cancel*; tooltip texts broken into lines |
| `ProxyNotice.cs` | the main menu's notice, once a game, where Windows refused the proxy |
| `KspUiState.cs` | whether KSP's interface shows, as the mods read it |
| `SettingsWindow.Keys.cs`, `KeyCapture.cs`, `Conflicts.cs`, `KspKeyBindings.cs` | the *Controls* tab: the rows that take a combination, the capture with KSP's controls locked, the shared combinations shown in yellow, and KSP's own bindings read from `GameSettings` |
| `KspSettingsSection.cs` | the section in KSP's settings dialog, through Harmony postfixes on `VideoSettings` |
| `PauseMenuEntry.cs` | the *ReDefinition* entry in the menu Escape opens, through Harmony postfixes on `PauseMenu.draw()` in flight and `KSCPauseMenu.draw()` in the space centre |
| `MainMenuEntry.cs` | the *ReDefinition* entry in KSP's main menu: a copy of its *Settings* entry, with Harmony postfixes on `MainMenu.lockEverything` and `unlockEverything` |
| `WindowPause.cs` | the pause button in the settings window's title row, and the flight held while the window stands |
| `ToolbarButton.cs` | the toolbar button, its tooltip, and its place at the front of the row |
| `SettingsWindow.Axes.cs`, `KspAxes.cs`, `AxisCapture.cs` | the *Axes* tab: KSP's joystick and gamepad axes, the copies it edits, and the capture that takes an axis as it moves |
| `SettingsWindow.Layout.cs` | KSP's keyboard layout at the top of the *Controls* tab |
| `WindowBackdrop.cs`, `ScrollThrough.cs` | how see-through the window is drawn and the shadow under its text; the mouse wheel passed from a button to the list under it |
| `ToolbarTakeover.cs`, `BundleNotice.cs` | hiding the bundled mods' toolbar buttons where their window is reachable, and those ReDefinition hides for its own reasons; the main menu's first question |
| `ToolbarHides.cs` | the toolbar buttons hidden for good where the settings window does what they did: Persistent Rotation's |
| `ModWindowClose.cs` | the close button on a mod's own settings window, and knowing whether that window is open |
| `ModWindowsAddon.cs` | when: the close buttons installed, the toolbar looked at, the open settings window following a change made in another window |
| `UnityMouseEvents.cs` | Unity's mouse events on redirected cameras, and clicks on ReDefinition's windows kept from the scene |
| `NvidiaDownloader.cs` | the download of NVIDIA's DLLs from NVIDIA's Streamline release after licence consent |
| `AmpereFrameGeneration.cs`, `SettingsWindow.Ampere.cs` | the *DLSS-G on RTX 30/20* row: the dialog with the steps, the install from the zip, and the package's choices |
| `DebugOptions.cs` | the diagnostics window's options, their search, *Changed* and reset, the pins, and the ring of frame times; free of Unity |
| `DiagnosticsStyles.cs` | the diagnostics window's styles, copied from KSP's skin |

### The model the settings window follows

The settings window is built the way KSP builds its own in-game settings dialog,
`MiniSettings`. That dialog is a `MultiOptionDialog` in the `MiniSettingsSkin`. It holds a
scroll list of its own, section buttons in the skin's first custom style, and
*Apply*, *Accept* and *Close* with KSP's own strings `#autoLOC_149512` to
`#autoLOC_149514`. ReDefinition's window takes *Accept* and *Close* from there and
*Cancel* from `#autoLOC_174783`, and shows *Accept* and *Cancel* only while something
waits for them, *Close* otherwise. `GameRestart.cs` is its *Save and restart*: KSP's
own save, then a hidden PowerShell that waits for KSP to end and starts it again.
`DialogOverWindow.cs` darkens the screen behind a dialog over the window.
`DialogTooltips.cs` gives the elements of a created dialog KSP's own tooltip controller,
which KSP's button prefab does not carry, so that their tooltips show.

`DialogGUIToggle` and `DialogGUISlider` take their state from their getters every frame,
so a row always shows what its setting holds. One scroll list serves every tab, and
shows one at a time.

The window copies `MiniSettings` so that a UI theme such as ZTheme, which replaces
KSP's UI textures by name, themes it with the rest of KSP's dialogs.

## The planner, `src/Engineer`

| File | Role |
|---|---|
| `EditorLaunchButton.cs` | the launch button over the launch pad seen from the VAB and the runway seen from the SPH |
| `EditorEngineer.cs` | the add-on in the editors: the planner's mode button, opening and closing the planner |
| `ModeButtonTiles.cs` | the planner button's tiles, made from the cargo button's, so that a UI theme's reach it |
| `EditorReadout.cs` | the vessel as the planner takes it, read from KSP's delta-v simulation, with every part and each stage's thrust at the surface worked out beside it |
| `PlannerPanel.cs` | the planner's panel, made as KSP's crew panel is |
| `Planner/` | the planner's route, fuel budget, plans and rows ([planner.md](planner.md)) |
| `Planner/Turning.cs` | how fast what flies a step turns by 90°, from its reaction wheels, RCS nozzles and gimbals against its moment of inertia; free of KSP's modules, tested |
| `StageTable.cs`, `EngineerFormat.cs` | a stage's row, the rocket equation and how numbers are written; free of Unity, tested |

How the planner sits in KSP's editor: [engineer.md](engineer.md).

## The flight UI, `src/Flight`

| File | Role |
|---|---|
| `NavballDrag.cs` | the navball moved by dragging its show/hide button, and to the position the settings hold |
| `Panel/` | the flight panel: `FlightPanel` puts the profiles in place of KSP's orbit tabs and widens the maneuver panel, `ProfileTab` lays a profile's readouts out in KSP's blocks, `FlightProfiles` keeps the profiles and picks one by the situation |
| `Readouts/` | the flight panel's values: `ReadoutCatalog` lists each with its title and group, `ReadoutFrame` reads the vessel, its orbit and target once per refresh, `ReadoutMath` holds the sums free of the game, tested |
| `../Window/UiHygiene.cs` | what every control made here has to get right: no keyboard selection, the UI layer, a text field let go after its edit, the greyed state of hidden rows |
| `../Window/EscapeGuard.cs` | an Escape that cancelled a key binding or a text field closes no dialog and opens no pause menu |
| `../Window/LoadingScreenWindows.cs` | skips the Toolbar's and MechJeb's OnGUI while KSP shows its black screen between scenes |
| `AssemblyReturn.cs` | the button on the space centre's VAB or SPH: revert to the editor, or recover and open the vessel there |
| `SpaceCentreButtons.cs` | buttons over the space centre's other buildings while the vessel waits on its launch site |
| `FacilitySpot.cs` | where a building's button stands: the middle of its hall |
| `WorldButton.cs`, `KscButtons.cs` | a button over a point in the world, placed as KSP's vessel labels are; the space centre's facility buttons copied for its look |
| `FlightUiSwitch.cs` | whether a feature of the flight UI is on, as the settings have it |
| `HeldVessel.cs` | a vessel a static cache was worked out for, held without keeping it alive |
| `ReDefinitionLegGrip.cs` | a landing leg's grip, chosen as a grip pad's is |
| `Panel/FlightPanelEditor.cs` | the window the flight panel's pencil opens: a profile's readouts in order, and the rest to add |
| `HudTooltips.cs` | KSP's own tooltip, in KSP's wording, on the staging stack's buttons, the alarm clock's button, the altimeter's mode switch and the delta-v tools' button; the staging's reset button asks first |
| `RoundGauge.cs`, `RoundGauge.Readings.cs` | KSP's vertical speed gauge as a digital gauge with a lit arc and five figures, picked by situation or by click; what it reads for each mode, and the log of what it shows |
| `GaugeReadings.cs` | the gauge's figures from plain numbers: digits, the arc's place on its fixed scale, bands and tick; tested against a simulated flight |
| `Impact.cs` | the time until the vessel's path meets the ground, from the orbit against the ground's height |
| `NavballDockingMarker.cs` | the navball's marker for where to point with a docking port as the target |
| `NavballSpeeds.cs` | the vertical and horizontal speed under the navball's SAS modes, on plates of its own with the heading field's texts |
| `AutoAltimeter.cs` | the altimeter switching between sea level and the ground by itself |
| `FlightStartMode.cs` | a flight starting in KSP's maneuver mode, where the settings say so |
| `CrewSlotButtons.cs` | the crew portraits' buttons that add and remove a slot, in the look of the staging stack's add and delete buttons |
| `TimeWarpButton.cs`, `TimeWarpBar.cs` | Better Time Warp Continued's drop-down button as a square copy of the MET button on an area at the end of the time panel's warp row, at one place on the screen above the warp-to-sunrise button |
| `StageInfoColumns.cs` | the stage details of KSP's staging stack in two columns from five values on, and the stack as wide as its stages, in the editors and in flight |
| `IconColumnsLayout.cs`, `StageGroupFields.cs` | a stage's part icons blended between one and two columns; KSP's private fields of a stage, read without boxing |
| `DeltaVTools.cs` | KSP's delta-v tools' button opening every stage's details, and in the editors the tools' situation panel under the lowest stage |

KSP's navball is one group, `NavballFrame`, in the flight UI: the ball, its show/hide
button, the SAS and RCS switches, the autopilot modes, the gauges and the delta-v gauge
**[src]**. `NavBallToggle.panel`, a `UIPanelTransitionToggle`, shows and hides it by
setting its `panelTransform` to one of two stored positions, and animates from a position
it keeps privately **[src]**. A move shifts that transform, both stored positions and the
kept one by the same amount, so that showing and hiding keep the new place. The drag
handler on the button sets `eligibleForClick` to false as the drag begins. The press and
the drag are on one object, and without that the input module would still click the
button.

## The proxy, `src/DxgiProxy`

| File | Role |
|---|---|
| `DxgiExports.cpp` | `dxgi.dll`'s exports, forwarded to Windows' own; the hook on `CreateSwapChainForHwnd` |
| `SwapChainProxy.h`, `SwapChainProxy.cpp` | the swapchain Unity gets: made through Streamline, FidelityFX or DXGI, with a shared Direct3D 11 texture as Unity's backbuffer; resize and teardown |
| `SwapChainProxyPresent.cpp` | the present path: Unity's frame copied into the Direct3D 12 backbuffer, frame generation prepared under the same lock, the fences between Direct3D 11, Direct3D 12 and DLSS-G's queue |
| `SwapChainProxyPacing.cpp` | FSR's pacing settings, the monitor's refresh rate, the frame limits, the frame time report |
| `SwapChainProxyForwarding.cpp` | COM identity and the calls the real swapchain answers |
| `SwapChainProxyBuffers.cpp` | the shared texture Unity renders into as its backbuffer, made again on a resize |
| `SwapChainProxyStreamline.cpp` | Streamline on the device, its swapchain, DLSS-G's per-frame tokens and frame limit |
| `SwapChainProxyState.h` | what the proxy's swapchains share process-wide |
| `FrameGeneration.h`, `FrameGeneration.cpp` | frame generation's owner |
| `FrameGenerationContext.cpp`, `FrameGenerationPrepare.cpp` | FSR's context made, retried, configured off and destroyed; its preparation for each Present |
| `FrameGenerationRate.cpp` | the rendered frame rate against `fgMinimumFps`, and the V-Sync cap ([frame-generation.md](frame-generation.md#pacing-and-v-sync)) |
| `FrameGenerationDump.cpp` | frame generation's inputs for a few consecutive frames, written for analysis away from the game |
| `FrameGenerationInputs.cpp` | the textures shared between Unity's Direct3D 11 and the proxy's Direct3D 12 device, copied at present and flipped where Unity renders upside down |
| `FrameGenerationCheck.cpp` | the HUD-less and motion vector checks, read back and logged |
| `FrameGenerationDlss.cpp` | the same inputs handed to DLSS frame generation through Streamline |
| `Streamline.h`, `Streamline.cpp` | Streamline 2.14.16, loaded from the player's files, with NVIDIA's signature check |
| `FrameGenerationUi.cpp` | the UI plane for DLSS-G, worked out from the presented frame and the HUD-less copy (`dlssgUiRecomposition`) |
| `XessFg.h`, `XessFg.cpp`, `FrameGenerationXess.cpp`, `SwapChainProxyXess.cpp` | Intel's XeSS frame generation and XeLL: the two DLLs, the swapchain, the inputs, the contexts' order |
| `XessUpscaler.h`, `XessUpscaler.cpp` | Intel's XeSS on the proxy's Direct3D 12 device |
| `LowLatency.h`, `LowLatency.cpp` | Reflex, Anti-Lag 2, XeLL and the latency brake, and the latency measure ([low-latency.md](low-latency.md)) |
| `NgxCore.h`, `NgxCore.cpp` | NVIDIA's NGX as the driver exports it, for DLSS on Direct3D 11 and Ray Reconstruction on Direct3D 12 |
| `Dlss.h`, `Dlss.cpp` | DLSS through NGX on Unity's Direct3D 11 device |
| `AmdUpscaler.h`, `AmdUpscaler.cpp` | AMD's upscaler DLL on the proxy's Direct3D 12 device |
| `D3d12Compute.h`, `D3d12Compute.cpp` | Direct3D 12 for mods: capabilities, HLSL compiled with Windows' `d3dcompiler_47.dll`, compute passes against one root signature, textures shared with Direct3D 11 |
| `Raytracing.h`, `Raytracing.cpp` | raytracing: meshes, materials and instances from the managed side, acceleration structures, the frame's passes on the proxy's Direct3D 12 device |
| `RaytracingDevice.cpp` | the raytracing's Direct3D 12 objects: shaders, pipelines, the slots' fences, lists and heaps, the shared copies of Unity's textures |
| `RaytracingScene.cpp` | meshes and their acceleration structures, material textures, each frame's instances |
| `RaytracingInternal.h` | what the raytracing's sources share: the passes, the timestamps, a mesh's layout |
| `RaytracingDenoise.cpp` | ReDefinition's own denoiser: its textures and the passes of a frame |
| `RaytracingReconstruct.cpp` | DLSS Ray Reconstruction through Streamline: its guides from the raytraced frame or the G-buffer, its tags and constants, the evaluation |
| `DlssRayReconstruction.h`, `DlssRayReconstruction.cpp` | DLSS Ray Reconstruction through NGX, beside FSR's and XeSS's frame generation |
| `RaytracingBuild.h`, `RaytracingBuild.cpp`, `RaytracingBuildFrame.h` | how the meshes' structures are built: batched, pooled, budgeted and compacted |
| `RaytracingCache.cpp` | the radiance cache: its buffers, anchor, constants and passes |
| `RaytracingOmm.h`, `RaytracingOmm.cpp`, `RaytracingOmmBake.h`, `RaytracingOmmBake.cpp` | opacity micromaps through NVAPI, and the baker that makes them from the textures |
| `RaytracingMegaGeometry.h`, `RaytracingMegaGeometry.cpp` | RTX Mega Geometry's cluster structures through NVAPI |
| `RaytracingRegenerate.cpp` | AMD FSR Ray Regeneration where its DLL runs |
| `RaytracingShaders/` | the raytracing and denoising passes in HLSL, compiled to DXIL at build time and embedded in `dxgi.dll` |
| `FidelityFx.h`, `FidelityFx.cpp` | AMD's frame generation runtime, loaded at run time through a function table |
| `NvidiaGpu.h`, `NvidiaGpu.cpp` | the GPU's architecture through NVAPI, for what DLSS and DLSS-G need |
| `ProjectIdentity.h` | what ReDefinition tells NVIDIA's libraries it is |
| `ManagedBridge.cpp` | the exports the C# bridges call |
| `Config.h`, `Config.cpp` | `ReDefinitionProxy.ini`: written from the built-in template and completed with missing keys at start, re-read while the game runs |
| `ReDefinitionProxy.ini` | the settings template, built into `dxgi.dll` |
| `Log.h`, `Log.cpp`, `LoadMonitor.h`, `LoadMonitor.cpp`, `WaitStats.h` | `ReDefinitionProxy.log`; CPU and GPU load; the render thread's waits for the GPU |
| `ScreenRecorder.h`, `ScreenRecorder.cpp` | what the monitor shows, frame generation's frames included, through desktop duplication |
| `GpuProfiler.h`, `GpuProfiler.cpp`, `CpuSampler.h`, `CpuSampler.cpp` | the GPU's time inside Unity's frame by the mod's markers; where the main thread spends its time in native code ([profiling.md](profiling.md)) |
| `FileUtil.h`, `FileUtil.cpp`, `D3d12Util.h` | paths next to the executable, file facts; Direct3D 12 resource transitions |
| `ProxyHarness.cpp`, `HarnessPass.hlsl` | a test driver that does what Unity does, without KSP; its compute pass for Direct3D 12 for mods |
| `RaytracingHarness.cpp`, `RaytracingOmmHarness.cpp`, `OmmProbe.hlsl` | the raytracing's test driver: a G-buffer rasterised in Unity's layout, the passes checked against the CPU; the opacity micromaps checked against the driver |
| `ReDefinitionProxy.def`, `ReDefinitionProxy.rc.in` | the exported names; the version resource, filled from the project's version |
| `extern/` | AMD's FidelityFX and Anti-Lag 2 headers, NVIDIA's Streamline headers and Intel's XeSS and XeLL headers (MIT) |

The proxy's own files are C++17 in the namespace `redefinition`, but for the three that
only export: `DxgiExports.cpp`, `ManagedBridge.cpp` and the harness. CMake builds them into
`dxgi.dll` and the harnesses into `ProxyHarness.exe` and `RaytracingHarness.exe`
(`src/DxgiProxy/CMakeLists.txt`).

Design and measurements: [frame-generation.md](frame-generation.md).

## Raytracing

The managed side of the raytracing, free of KSP's types so that the raytracing test player
compiles it on its own ([raytracing.md](raytracing.md)):

| File | What |
|---|---|
| `src/Raytracing/RtNative.cs` | the proxy's raytracing exports and packets |
| `src/Raytracing/RtScene.cs` | meshes, materials, material textures and each frame's instances handed to the proxy; the sources of the scene |
| `src/Raytracing/RtMaterial.cs` | what a ray takes of a material: albedo, specular colour, smoothness, emission, as Unity's standard shaders and Deferred shade them |
| `src/Raytracing/RtMaterialIntent.cs` | how a material's shine is meant, by its shader's family and values, for the surfaces written back into the G-buffer |
| `src/Raytracing/RtDeferredLook.cs` | what of a surface's colour stays diffuse where Deferred shades it, for the forward light without Deferred |
| `src/Raytracing/RtStockTerrain.cs`, `RtTerrainColour.cs` | the ground's colour for the rays as KSP's terrain shaders and as Parallax's give it |
| `src/Raytracing/RtReflectorLight.cs` | how much of the sun's light a lit body sends to the scene; free of Unity and KSP, tested |
| `src/Raytracing/RtCamera.cs` | the raytracing of one camera: the capture and the trace before the frame's lighting, the denoiser's history across world shifts |
| `src/Raytracing/RtLighting.cs` | ReDefinition's screen-space shadows and deferred reflections shaders in Unity's place, and the ones before them back |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionRtCapture.shader` | the copies of depth, G-buffer, motion and sky the raytracing reads |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionScreenSpaceShadows.shader` | Unity's screen-space shadows with the raytraced sun visibility for the raytraced camera |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionDeferredReflections.shader` | Deferred's reflections pass with the raytraced diffuse light and reflections |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionRtForwardLight.shader` | the raytraced diffuse light and reflections added to the frame where Deferred is not there |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionRtReproject.cginc` | where a point of this frame lay in the previous raytraced frame, shared by the lighting shaders |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionRtScatter.compute` | the GPU's instances, Parallax's scatter, gathered into the texture the proxy reads |
| `unity/Assets/ReDefinition/Shaders/ReDefinitionRtClouds.shader`, `ReDefinitionMoonGlow.shader` | EVE's cloud layer for the rays; the moons' glow in the sky |

KSP's side, which feeds the scene and the camera from the game:

| File | What |
|---|---|
| `src/Raytracing/Ksp/RtKspSources.cs` | the scene's sources: vessel parts, skinned parts baked, terrain quads, KSC buildings and the editors' scenery, Parallax's scatter from the GPU |
| `src/Raytracing/Ksp/RtKspMods.cs` | the installed mods raytracing reaches into where they are there: Kopernicus's current star, Parallax's scatter buffers, PlanetShine's lights, Deferred's screen-space reflections |
| `src/Raytracing/Ksp/RtKsp.cs` | the setting's modes, the scene camera's raytracing and its providers: sun, reflection probe, world shifts, rendered projection, main body; the report in the log |
| `src/Raytracing/Ksp/RtReflectors.cs`, `NightAdaptation.cs`, `MoonGlow.cs` | the planet and its moons as lights; how dark the sky is; the moons' discs made to stand out |
| `src/Raytracing/Ksp/SeaHighlights.cs`, `SeaFrameAnchor.cs` | the sun's and moons' glint on the sea; the sea's frame for the waves |
| `src/Raytracing/Ksp/RtEveClouds.cs` | EVE's cloud layer over the main body, for the rays |
| `src/Raytracing/Ksp/EditorHallShine.cs` | the VAB's and SPH's halls shining as their materials mean while the raytracing runs |

## UI scaling, `src/Scaling`

| File | Role |
|---|---|
| `UiScaling.cs` | the scale for what KSP draws at a fixed number of pixels |
| `ScalingAddon.cs` | installs the hooks once, as the game starts |
| `ScalingHooks.cs`, `MapNodeScale.cs`, `SpaceCentreScaling.cs` | KSP's gizmos and other fixed sizes; the map's icons; the R&D screen's nodes and tiles |
| `ModWindowScale.cs` | other mods' IMGUI windows at KSP's UI scale |
| `OtherScalers.cs` | other mods' scaling of the same things turned off |

What each scales: [ui-scaling.md](ui-scaling.md).

## The main menu and the space centre, `src/Menu` and `src/Scenery`

| File | Role |
|---|---|
| `Menu/MenuFireworks.cs` | fireworks on Kerbin's night side in the main menu |
| `Menu/KscArrival.cs` | the way from the main menu to the space centre as one camera flight |
| `Scenery/KscGrass.cs` | Parallax's grass on the space centre's lawns |
| `Scenery/RunwayLights.cs` | the runway's lights dimmed |
| `Scenery/ReflectionProbeFix.cs` | KSP's flight reflection probe switched on again |
| `Scenery/FireflyIdle.cs` | Firefly's re-entry effects not drawn while they show nothing |

## The probes, `src/Probe`

Each runs with its argument on KSP's command line: the scene probe's flight and shots
(`SceneProbe.cs`, `ShotVariant.cs`, `LightProbe.cs`), the GPU profiler and the mods' cost
(`GpuProfiler.cs`, `ModCost.cs`, `PerfMonitor.cs`), the screen check (`ScreenCheck.cs`),
the vendors' own checks (`VendorProbe.cs`), RenderDoc and FLIP captures
(`RenderDocCapture.cs`, `FlipProbe.cs`), the shader inventory and the grass gap
(`ShaderInventory.cs`, `KscGrassGap.cs`). How to run them: [profiling.md](profiling.md).

## Around the code

| Where | What |
|---|---|
| `GameData/ReDefinition/Mods` | the registrations of KSP and the nine bundled mods |
| `GameData/ReDefinition/Profiles` | the five profiles |
| `unity/Assets/ReDefinition` | the ported FSR 3 compute shaders and ReDefinition's own shaders, among them the masks, EVE's cloud motion, the motion audit, the vessel's shadow, the space centre's grass and the raytracing's, and `Editor/BundleBuilder.cs`; the shader include for mods and `Editor/IncludeCheck.cs` |
| `tests/ReDefinition.Tests` | MSTest on .NET Framework 4.8: the store, the file, the edit model, the modules, registrations, frame packet layouts, Streamline's camera matrices, EVE's cloud motion, TUFX's split, NVIDIA's files, the key bindings and the *Controls* tab, the interface for mods and its wrapper |
| `tools/check_bundled_mods.ps1`, `tools/check_profile_parser.ps1`, `tools/check_key_bindings.ps1`, `tools/check_foreign_code.ps1` | checks against the installed mods and KSP's own `ConfigNode` |
| `tools/check_docs.ps1` | the rules the pages under `docs/` follow |
| `tools/port_fsr3_shaders.py`, `tools/audit_ffx_fields.py`, `tools/fetch_amd_runtime.py` | the FSR shaders from FSR3Unity's, the frame generation field audit, AMD's runtime for the package |
| `tools/export_rt_scene.py` | a scene of KSP's own meshes for the raytracing harness, written outside the repository |
| `tools/build_rt_player.py` | builds the raytracing test player with Unity 2019.4.18f1 and runs it with forward rendering, Unity's deferred lighting and Deferred's |
| `tests/RaytracingPlayer/` | the raytracing test player's Unity project: its build script and its runner, which builds a scene, renders it with and without raytracing and checks the images |
| `tools/craft_stages.py` | the stock crafts' stages worked out offline, for the planner's tests (`StockCrafts.cs`) |

How to build and run all of it: [building-and-testing.md](building-and-testing.md).

## Conventions

* No other mod is a reference: every mod is reached through reflection, by names checked
  against the installed build (`tools/check_bundled_mods.ps1`).
* A setting's key, `<mod>.<setting>`, never changes. `bundled.cfg`, the profiles and
  packs' patches all name it.
* A member a build does not have drops its setting, with the reason in the log.
* ReDefinition keeps no migration for its own stored formats. A format changes in
  place.
* Code comments say what the code does and why. Longer reasons and research live in these
  pages, and the comments point here.
