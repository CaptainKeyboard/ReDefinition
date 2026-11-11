# Credits and licences

This page lists every work that ReDefinition ships, compiles in, adapts, has the player
download or talks to, with its author, its licence and how ReDefinition uses it.

The entries were checked on 2026-11-09 against ReDefinition's code and against the works
themselves. ReDefinition's source was compared with the sources of the works below by
matching lines, token sequences and constants; code that was rewritten beyond recognition
would not show in such a comparison. Authors and licences were read from the licence,
notice or README of each work, from its repository or from its distribution page, as the
entry says. A work that ReDefinition only mentions and does nothing with is not listed.

**This project's licence:** GPL-3.0 (version 3 only) WITH Modding Exception AND GPL-3.0 Linking
Exception (see `LICENSE` and `EXCEPTIONS.md`). It is "only" because the planner assembly
contains code adapted from MechJeb 2, and the main module contains a shader adapted from
Deferred, and both state GPL version 3 without "or later".

**The Use column** says how a work is in the project:

| Use | Meaning |
|---|---|
| In the package | It ships in a release zip, or it is compiled into a file that ships |
| Code taken | Code copied, translated or adapted into ReDefinition's files, which are named |
| Text taken | A licence or notice text that ReDefinition's files carry |
| Technique | A technique or an interface that ReDefinition's code follows |
| Run time | Another mod that ReDefinition talks to while the game runs |
| Player's download | Not shipped: the player fetches it from its makers or brings it |
| Reference | A document or source that ReDefinition's code or comments cite |
| Tool | Used to build or check ReDefinition, not shipped |

## Graphics vendors

### NVIDIA

| Work | Licence | Use | What ReDefinition does with it |
|---|---|---|---|
| Streamline SDK headers, 2.14.16 | MIT | In the package | 15 headers in `src/DxgiProxy/extern/Streamline`, compiled into `dxgi.dll`, to call Streamline's DLSS frame generation, Reflex and latency markers. Notice: `dxgi_LICENSE-NVIDIA.txt` |
| NVAPI interface | MIT (github.com/NVIDIA/nvapi) | In the package | Function ids and structs are declared by hand in `NvidiaGpu.cpp`, `LowLatency.cpp`, `RaytracingOmm.h` and `RaytracingMegaGeometry.h`: the GPU class, Reflex markers, opacity micromaps and cluster acceleration structures (RTX Mega Geometry) |
| Streamline's DLSS Ray Reconstruction plugin (`sl.dlss_d`), 2.14.16 | MIT | Code taken | `DlssRayReconstruction.cpp` adapts its logic (order of calls, values); the file's header names the source file and version |
| DLSS Ray Reconstruction programming guide | as NVIDIA publishes it | Code taken | One function, `EnvBRDFApprox2` in `RtCommon.hlsli`, is reproduced from the guide (itself after *Ray Tracing Gems*, chapter 32) and named as such |
| DLSS, DLSS frame generation, DLSS Ray Reconstruction and Reflex files (`nvngx_dlss.dll`, `nvngx_dlssd.dll`, `nvngx_dlssg.dll`, `sl.interposer.dll`, `sl.common.dll`, `sl.dlss_g.dll`, `sl.dlss_d.dll`, `sl.reflex.dll`, `sl.pcl.dll`) | NVIDIA's SDK licences, shown to the player before the download | Player's download | With one click and NVIDIA's licences accepted, ReDefinition downloads them from NVIDIA's Streamline release v2.14.16 on GitHub, checks each against a pinned SHA-256 and places NVIDIA's licence texts beside them. They run DLSS, DLSS frame generation, Ray Reconstruction and Reflex. ReDefinition does not ship them |
| DLSS and Streamline programming guides | as NVIDIA publishes them | Reference | The source for the calls, the tagged resources and the limits of DLSS and DLSS frame generation. Comments in `Config.h` and `FrameGeneration.h` cite them |

### AMD

| Work | Licence | Use | What ReDefinition does with it |
|---|---|---|---|
| FidelityFX FSR 3 shader headers and passes, as FSR3Unity carries them (FidelityFX SDK 1.1.x) | MIT (AMD, 2024; two files 2022-2023) | Code taken | 35 header and pass files in `unity/Assets/ReDefinition/Shaders/shaders`, compiled into `redefinition.shaders`. FSR 3 in the game runs on them. Notice: `LICENSE-FidelityFX.txt` |
| FidelityFX API headers (frame generation 4.0.1, denoiser 1.2.0) | MIT | In the package | 13 headers in `src/DxgiProxy/extern/FidelityFX`, compiled into `dxgi.dll`, to call AMD's DLLs. Notice: `dxgi_LICENSE-FidelityFX.txt` |
| `amd_fidelityfx_framegeneration_dx12.dll` 4.0.1 and `amd_fidelityfx_denoiser_dx12.dll` 1.2.0 (FSR SDK v2.3.0) | AMD's SDK licence: binaries only, notices reproduced, no reverse engineering | In the package | Placed next to `KSP_x64.exe` with their licence texts. They run FSR frame generation and, on a Radeon RX 9000, FSR Ray Regeneration |
| `amd_fidelityfx_upscaler_dx12.dll` | AMD's binary-only licence in the FidelityFX SDK (`docs/license.md`): binary form only, notices reproduced, no reverse engineering | Player's download | ReDefinition does not ship it. The player takes it from the SDK's `Kits/FidelityFX/signedbin` folder, as `TechniqueFiles.cs` tells him. `dxgi.dll` loads it to run AMD's FSR upscaler |
| Anti-Lag 2 SDK headers | MIT | In the package | Two headers in `src/DxgiProxy/extern/AntiLag2`, compiled into `dxgi.dll`. `LowLatency.cpp` calls `Update` and `MarkEndOfFrameRendering` and puts its context into the swap chain's private data for AMD's frame generation. Notice: `dxgi_LICENSE-AntiLag2.txt` |
| Capsaicin, the hash-grid radiance cache (`hash_grid_cache.hlsl`, `hash.hlsl`) | MIT | Code taken | `Raytracing/Shaders/RadianceCache.hlsli` adapts the cell size, the hashes (the body of xxHash32 among them), the insert and the lookup, with the adapted parts marked. Notice: `dxgi_LICENSE-Capsaicin.txt` |
| FidelityFX documentation (denoising, super resolution, frame interpolation) and the Anti-Lag 2 README | as AMD publishes it | Reference | The source for FSR's upscaler, frame generation and denoiser, and for the calls of Anti-Lag 2 |

### Intel

| Work | Licence | Use | What ReDefinition does with it |
|---|---|---|---|
| XeSS and XeLL headers (`xess.h`, `xess_d3d12.h`, `xell.h`, `xell_d3d12.h`) | MIT | In the package | In `src/DxgiProxy/extern/XeSS`, compiled into `dxgi.dll`. The XeSS frame generation interface is declared by hand from Intel's headers, which carry a proprietary notice and are not in the repository. Notice: `dxgi_LICENSE-XeSS.txt` |
| `libxess.dll` 2.0.2.68, `libxess_fg.dll` 1.3.1.78, `libxell.dll` 1.3.2.10 (XeSS SDK 3.0.2) | Intel Simplified Software License (October 2022), shown to the player before the download | Player's download | With one click, ReDefinition downloads them from Intel's XeSS release on GitHub, checks each against a pinned SHA-256 and saves Intel's licence beside them. They run XeSS upscaling, XeSS frame generation and XeLL low latency. ReDefinition does not ship them |
| XeSS and XeLL developer guides | as Intel publishes them | Reference | The source for initialisation, tagged resources, present ids and latency markers |

## Other projects whose code or interface is in ReDefinition

| Work | By | Licence | Use | What ReDefinition does with it |
|---|---|---|---|---|
| **FSR3Unity** | Nico de Poel | MIT | Code taken | The FSR 3 upscaler in `src/Fsr3`: seven files copied or adapted for Unity 2019.4 and KSP; in `unity/Assets/ReDefinition/Shaders` the 22 compute files with their keywords baked in and `ffx_fsr_unity_common.cginc` (the headers they include are AMD's, above). Notice: `LICENSE-FSR3Unity.txt` |
| **MechJeb 2** | the MechJeb contributors, among them sarbian and lamont-granquist | GPL-3.0 | Code taken, Run time | The descent simulation in `src/Planner/Landing`: `ReentrySimulation.cs`, `SimCurves.cs`, `SimulatedParachute.cs`, `SimulatedPart.cs` and `SimulatedVessel.cs` are MechJeb's, with heat flux and dynamic pressure added; `Compat.cs` holds reduced helpers; `DescentPolicies.cs` adapts its descent speed policies. At run time the planner's autopilot calls MechJeb |
| **Deferred** | Ghassen Lahmar (LGhassen) | GPL-3.0 | Code taken, Run time | `ReDefinitionDeferredReflections.shader` is adapted from Deferred's reflections shader (Deferred 1.3.5); the raytraced ambient and reflection block is ReDefinition's own. At run time its settings are bundled |
| **Unity's built-in shaders** (Unity 2019.4.18f1) | Unity Technologies | MIT | Code taken | `ReDefinitionScreenSpaceShadows.shader` is adapted from `Internal-ScreenSpaceShadows.shader` (Copyright 2016 Unity Technologies), the changes marked. Deferred's reflections shader, which ReDefinition's adapts, itself derives from Unity's. `ReDefinitionAtmospheresStormVolume.shader` includes `UnityCG.cginc` and uses its macros. The seasons' storm particles look up Unity's `Legacy Shaders/Particles/Alpha Blended`, `Particles/Alpha Blended` and `Sprites/Default` by name at run time. Notice: `LICENSE-Unity-shaders.txt` |
| **Unity Graphics** (Scriptable Render Pipeline packages) | Unity Technologies | Unity Companion License | Code taken | `ffx_fsr_unity_common.cginc` adapts macros from the SRP package's `TextureXR.hlsl` (line 54). Notice: `LICENSE-Unity-Companion.txt` |
| **Precomputed Atmospheric Scattering** | Eric Bruneton (INRIA); modified and ported to Unity by Justin Hawkins, as Scatterer carries it | BSD-3-Clause | Code taken | The analytic optical depth in the cloud moonlight shader `ReDefinitionCloudMoonlight.shader`. Notice: `LICENSE-Bruneton.txt` |
| **QR Code generator library** | Project Nayuki | MIT | Code taken | `src/Dashboard/QrCode.cs` is a C# translation of the Java implementation, cut down to byte mode, level M, versions 1 to 10 and one mask. Notice: `LICENSE-QR-Code-generator.txt` |
| **Telemachus Reborn** | TeleIO; maintained by TeaGuild | MIT | Technique | The dashboard's key names, the bracket syntax of its commands (`f.setThrottle[0.5]`) and its WebSocket message keys follow Telemachus' API: where a key exists in both, its name and bracket syntax are Telemachus'. Notice: `LICENSE-Telemachus.txt` |
| **Massively Parallel Path Space Filtering** (Binder, Fricke, Keller) and **GI-1.0** (Boissé et al.) | the authors | papers | Technique | `RadianceCache.hlsli` follows the hash table of jittered, quantised keys and the world-space hash-grid cache they describe (arXiv 1902.05942, arXiv 2310.19855) |
| **Interleaved gradient noise** ("Next Generation Post Processing in Call of Duty: Advanced Warfare", SIGGRAPH 2014) | Jorge Jimenez | presentation | Technique | `ReDefinitionAtmospheresStormVolume.shader`: `Dither` offsets each pixel's ray start with the noise function and constants of the presentation. The shader is ReDefinition's own |
| **Vulkan specification** (opacity micromaps) | The Khronos Group | CC-BY-4.0 for the specification text (Vulkan-Docs); its code is Apache-2.0 | Reference | The order of the micro-triangles in the section *Ray Opacity Micromap* (`chapters/raytraversal.adoc`), which `RaytracingOmmBake.cpp` quotes and builds |
| **OptiScaler** | the OptiScaler team | GPL-3.0 | Reference | Comments in `Config.h`, `XessFg.cpp`, `SwapChainProxyXess.cpp`, `Dlss.cpp`, `DlssRayReconstruction.cpp` and `FrameGenerationXess.cpp` cite how its XeSS frame generation integration behaves |
| **Skyrim Community Shaders** and **DynamicShaderFrameGen** | doodlum, Pentalimb and contributors (Community Shaders); jatelop8 (DynamicShaderFrameGen) | GPL-3.0-or-later WITH Modding Exception AND GPL-3.0 Linking Exception | Text taken | `EXCEPTIONS.md` is the text of the Modding Exception and the GPL-3.0 Linking Exception that both projects carry, unchanged. ReDefinition's licence line follows theirs, with GPL version 3 only |
| **dlssg_for_sm86** | sdli1995; SM75 kernels by Coldwood1026; a method by dashdogy, as its README names them | its README says GPL-3.0 for the source, its notices say its own C++ is MIT | Player's download | DLSS frame generation on RTX 30 and RTX 20. ReDefinition neither ships nor downloads it: the player downloads the release from its page, and ReDefinition installs the files from that zip next to `KSP_x64.exe` |

MechJeb 2 and Deferred state GPL version 3 without "or later" in their licence files
(`LICENSE.md` and `License.md`). The files taken from them are under that licence.

## Mods ReDefinition works with at run time

The defaults that ReDefinition's registrations take from these mods are listed in
[reference/mod-defaults.md](reference/mod-defaults.md).

| Mod | By | Licence | What ReDefinition does with it |
|---|---|---|---|
| **Scatterer** | Ghassen Lahmar (LGhassen); among others JonnyOThan, blowfishpro, soulsource, linuxgurugamer, tarsolya | GPL-3.0 | Its TAA and SMAA are switched off while the upscaler runs; its settings are bundled; its cloud godray set-up is corrected by a Harmony patch |
| **TUFX** | shadowmage45; KSPModStewards, among them JonnyOThan, al2me6, HebaruSan, LGhassen, NathanKell | GPL-3.0-or-later; its Unity Post Process folder under the Unity Companion License | Its antialiasing is set to none while the upscaler runs; its scene profiles are bundled; its toolbar button can be hidden; the atmospheres lower the post exposure of its colour grading in heavy precipitation, reaching its members by reflection |
| **EVE Redux** and its volumetric clouds | rbray89, LGhassen, WazWaz, R-T-B (LGhassen's fork of WazWaz's fork of rbray89's EnvironmentalVisualEnhancements; the raymarched clouds are in the same repository) | MIT, with a GPL DDS loader (its README); the raymarched branches have no licence file of their own | Its settings are bundled; its cloud jitter is corrected by a Harmony patch; the moons light its clouds. The seasons reflect into its members at run time to raise the cloud coverage (`ParticleField.currentCoverage`), the cloud type densities, the raymarched layer fields and the ambient sound volume, and to switch the rain and snow particles. They add a layer of their own through a config, and replace its coverage map (`TextureWrapper.textureValue`) with one painted from the storm cells; the equirectangular convention of that map is EVE's `GetEquirectangularUV`, written out again in `EveCoverageMath` |
| **StockVolumetricClouds** (config pack for EVE's volumetric clouds) | Blackrack | No licence file in its folder | The numbers of the phase functions, the worley noise and the scaled fade in `GameData/ReDefinition/Atmospheres/Clouds/AtmospheresStormLayer.cfg` are config values as read from the installed pack. The values of the snow layer (the fx-only raymarching settings, the cloud type's particle density and ambient volume) and of the particle field `ReDefinition-Atmospheres-snow` (the keys of the pack's `snow-Kerbin` with a larger count, size and fall speed), the 2D layer's `_DetailDist` and `_Lambertian`, and the references to the pack's `SnowFlakesSheet` texture and `SnowStormLoopedFreeAudio` sound are read from the installed pack; the pack's textures and sound are referenced by their paths. The rest of that file, the layer's cloud type and its height curves, is written for the storm cell |
| **Deferred** | see above | GPL-3.0 | Its screen-space reflections are set from the settings window at every start, as it has no save routine of its own |
| **Parallax Continued** | Gameslinx, Phantomical, R-T-B, BurgerKerman, ballisticfox, Linx-RW, 0x00ASTRA | All Rights Reserved, excluding one texture (its README) | Its settings are bundled; a patch doubles the render buffers of Kerbin's grass scatter (`KerbinGrass`). The seasons read and call its members by reflection at run time: the terrain quad data of `PQSMod_Parallax` (`OnQuadNormalUpdate`) and the scatter data (`ScatterData.Cleanup` and `Start`), to refresh the ground after the snow changes it |
| **Waterfall** | ChrisAdderley, JonnyOThan, zorg2044, KnightofStJohn, DRVeyl, ArXen42, al2me6 | CC BY-NC-SA 4.0 | Its plume lights and heat distortion are set from the settings window |
| **Firefly** | M1rageDev, JonnyOThan, SPACEMAN9813, drewcassidy, giuliodondi | code GPL-3.0, models and textures All Rights Reserved | Its re-entry effect settings are set from the settings window; ReDefinition corrects the flicker that its camera caused |
| **Distant Object Enhancement /L** | Rubber Ducky, MOARdV, TheDarkBadger, LisiasT (its NOTICE) | SKL 1.0 or GPL-2.0 | Its flare, distant-vessel and sky settings are bundled |
| **Kopernicus** | R-T-B, StollD, Sigma88, Phantomical, teknoman117, NathanKell | LGPL-3.0 | The current star is read for the sky and the light; its shader-quality settings are read |
| **Persistent Rotation (Upgraded)** | Markus Appel (markusa380); maintained by linuxgurugamer | GPL-3.0-or-later | Its settings are bundled and its toolbar button can be hidden; the autopilot brings the vessel to rest after a burn before it warps, because Persistent Rotation keeps a spin |
| **MechJeb 2** | see above | GPL-3.0 | The planner's autopilot calls its node executor, ascent and landing autopilots; it is kept off KSP's loading screen |
| **Parsek** | vl3c | MIT | The planner reads its recorder and ghost state through reflection, starts a recording when a route flight starts and commits a plan recording to it when the vessel is left |
| **Better Time Warp (Continued)** | HappyFaceIndustries (original); linuxgurugamer, yalov (Continued) | GPL-3.0 | ReDefinition's time warp button shows the mod's drop-down button in KSP's time panel and hides the mod's own buttons |
| **4kSP Expanded** | Tom Hörberg (tom300z, original), linuxgurugamer, Rjoande | MIT, Copyright (c) 2019 Tom Hörberg (Expanded) | Its own UI scaling is skipped so that ReDefinition's scaling applies alone |
| **Maneuver Node Evolved** | DMagic (DMagic1/KSP_Better_Maneuvering) | MIT | Its gizmo sizing is rerouted so that ReDefinition's scaling applies |
| **Kerbal Frame Generator** | MangoTech (MangoTechKSP) | not readable: its SpaceDock page and its GitHub repository are gone (checked 2026-11-09) | Its frame blend is switched off while the upscaler runs |
| **ToolbarControl** | linuxgurugamer, averageksp, PiezPiedPy, R-T-B | LGPL-3.0 | Its list of buttons is read to hide a bundled mod's button |
| **Toolbar** (Blizzy's Toolbar) | Maik Schreiber (blizzy78); continued by linuxgurugamer | BSD-2-Clause (its `LICENSE.txt`, Copyright 2013-2014 Maik Schreiber) | Its buttons are kept off KSP's loading screen |
| **Hullcam VDS Continued** and **CameraTools** | Albert VDS (Hullcam, original), linuxgurugamer, DavidCrichton (Continued); BrettRyland, jrodrigv, josuenos, Halbann, BahamutoD (CameraTools) | GPL-3.0 (Hullcam, `License.txt`); GPL-3.0 (CameraTools, `License.txt` in its distribution folder) | The upscaler resets its history when the flight camera's target or parent changes, as these mods change it; a cut that keeps both is not detected |
| **ModuleManager** | ialdabaoth (original), Sarbian, blowfishpro | Creative Commons share-alike per its README; the repository and the 4.2.3 release carry no licence file | ReDefinition's patches (landing-leg grip, Parallax grass room) need it, and a pack can patch the registrations with it |
| **Harmony** (HarmonyKSP) | Andreas Pardeike; packaged for KSP by gotmachine, DasSkelett, HebaruSan | MIT | ReDefinition's patches of the game and of other mods run on it; it is a requirement |

## Mods whose technique ReDefinition adapted or builds around

| Mod | By | Licence | What ReDefinition took from it |
|---|---|---|---|
| **KSPCommunityFixes** | gotmachine, NathanKell, JonnyOThan, Phantomical, siimav, tobiasnmf | MIT (its README) | How a section is added to KSP's own settings dialog (`KspSettingsSection.cs`) |
| **ClickThroughBlocker** | linuxgurugamer, chambm, HebaruSan, SteveBenz, among others | LGPL-3.0 | How a window keeps clicks from what lies behind it: `ALLBUTCAMERAS` locked while the cursor is over it, the editor's own lock in the editors, and a window's rectangle taken from the GUI matrix |
| **4kSP Expanded** | see above | MIT | The design of its window scaling: a prefix on `GUI.Window` that skips an identity matrix, shifts the window onto the screen, and a postfix that restores it |
| **Astrogator** | HebaruSan | GPL-3.0 | How a button gets KSP's own tooltip: a `TooltipController_Text` with the game's `Tooltip_Text` prefab |
| **KerbalVR** | Vivero | MIT | The list of cameras that make up KSP's 3D image, per scene |
| **ZTheme** | zapSNH, OnlyLightMatters, Phantomical | GPL-3.0 | The settings are built from KSP's own elements so that ZTheme themes them; one of its sprites is read for the layout |
| **HUDReplacer** | UltraJohn, Phantomical, Aeurias, zapSNH | GPL-3.0 | ReDefinition works around its skin patch and its texture replacement, which reach the windows it draws |
| **FreeIva** | JonnyOThan, pizzaoverhead, Icecovery | GPL-2.0 | Its hatches take their clicks through Unity's mouse events, for which the upscaler lends its cameras' screen |
| **Trajectories** | Youen Toupin (neuoy), A. Korsunsky (fat-lobyte), S. Gray (PiezPiedPy), linuxgurugamer (current repository: linuxgurugamer/KSPTrajectories) | GPL-3.0-or-later | The worked example of a registration, `docs/modders/examples/Trajectories.cfg`, and its approach to a search |
| **SPVE** (SpacePotato's Volumetric Enhancements) | TheSpacePotato | CC-BY-NC 4.0 (its SpaceDock page) | Its auroras are told from the other cloud layers and left to shine by themselves |

## Used to build or check, not shipped

| Work | By | Licence | Use |
|---|---|---|---|
| **Kerbal Space Program** and **Unity** | Squad, Private Division; Unity Technologies | proprietary | Tool: ReDefinition's assemblies reference the game's and Unity's, and nothing of them is shipped |
| **Windows SDK and DirectX** | Microsoft | the SDK's licence | Tool: the headers and libraries `dxgi.dll` is built against, and the shader compiler |
| **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) | Microsoft and the .NET Foundation | MIT | Tool: `tools/DocLock`, `tools/Refactor` and `tools/Unused` parse the C# sources with it |
| **MSTest**, **Microsoft.NET.Test.Sdk**, **.NET Framework reference assemblies** | Microsoft | MIT (the reference assemblies: the .NET licence) | Tool: the tests and the build |
| **icuewidget-cli** | Corsair | Apache-2.0 (its lock file) | Tool: validates the iCUE widget |
| **Pillow** and **numpy** | the two projects | MIT-CMU (Pillow); BSD-3-Clause and further licences (numpy) | Tool: the scripts that draw the icons and evaluate the probe's output |
| **UnityPy** | Rudolf Kolbe | MIT | Tool: the scripts that read the game's assets |

## Where the licence texts are

The full release zip carries the notices below. The planner's zip and the Common zip carry
`LICENSE`, `EXCEPTIONS.md` and `CHANGELOG.md`.

| File | Where | Covers |
|---|---|---|
| `LICENSE`, `EXCEPTIONS.md` | `GameData/ReDefinition/Main` | This project |
| `LICENSE-FSR3Unity.txt` | `GameData/ReDefinition/Main` | FSR3Unity |
| `LICENSE-FidelityFX.txt` | `GameData/ReDefinition/Main` | AMD's FSR 3 shader headers and passes |
| `LICENSE-Bruneton.txt`, `LICENSE-Unity-shaders.txt` | `GameData/ReDefinition/Main` | Bruneton, Unity's built-in shaders |
| `LICENSE-Telemachus.txt`, `LICENSE-QR-Code-generator.txt` | `GameData/ReDefinition/Dashboard` | Telemachus, Nayuki |
| `LICENSE-Unity-Companion.txt` | `GameData/ReDefinition/Main` | The Unity Companion License of the SRP macros in `ffx_fsr_unity_common.cginc` |
| `SOURCE.md` | `GameData/ReDefinition/Main` | The source of this build and the notices |
| `dxgi_LICENSE-NVIDIA.txt`, `dxgi_LICENSE-FidelityFX.txt`, `dxgi_LICENSE-AntiLag2.txt`, `dxgi_LICENSE-Capsaicin.txt`, `dxgi_LICENSE-XeSS.txt` | next to `KSP_x64.exe` | The headers and adapted code compiled into `dxgi.dll` |
| `amd_fidelityfx_framegeneration_dx12_LICENSE.md`, `amd_fidelityfx_denoiser_dx12_LICENSE.md` | next to `KSP_x64.exe` | AMD's two runtime DLLs |

NVIDIA's and Intel's licence texts for the files the player downloads are placed beside those
files when they are downloaded.
