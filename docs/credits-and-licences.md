# Credits and licences

**For:** anyone asking whose work is in ReDefinition, and under which licence.
**You need:** nothing.
**You get:** everyone whose work ReDefinition builds on, talks to at run time, or
learnt from, with the licence of that work and what ReDefinition does with it.

Authors are taken from the work itself where it names them (a licence, a
NOTICE, a README), otherwise from the repository owner and its leading
contributors as GitHub lists them (read 2026-09-10).

**This project's licence:** GPL-3.0-or-later WITH Modding Exception AND GPL-3.0
Linking Exception (see `LICENSE`).

---

## Part of this project

| Work | By | Licence | How it is used |
|---|---|---|---|
| **FSR3Unity** -- AMD FidelityFX FSR 3 upscaler reimplemented for Unity, with AMD's FidelityFX shaders | Nico de Poel (ndepoel) | MIT | Source in `src/Fsr3` and `unity/Assets/ReDefinition/Shaders`, adapted in ReDefinition for Unity 2019.4 and KSP ([development/upscaler.md](development/upscaler.md), "FSR3Unity on Unity 2019.4"); its licence unchanged in `src/Fsr3/LICENSE.txt` |
| **AMD FidelityFX SDK** -- API headers | Advanced Micro Devices | MIT | The FidelityFX API, loader, frame generation and upscaler headers in `src/DxgiProxy/extern/FidelityFX`, compiled into `dxgi.dll`. Their notice is in `licenses/AMD-FidelityFX-API-MIT.txt`, which the package puts next to `dxgi.dll` as `dxgi_LICENSE-FidelityFX.txt` |
| **AMD FidelityFX SDK** -- FSR 3.1 frame generation runtime | Advanced Micro Devices | AMD's SDK licence (`docs/license.md` of the SDK): binaries may be redistributed with AMD's notice reproduced; no reverse engineering | The player package ships `amd_fidelityfx_framegeneration_dx12.dll` 4.0.1 from FSR SDK v2.3.0, signed by AMD, next to `KSP_x64.exe` with AMD's licence beside it (`third_party/amd/README.md`). |
| **NVIDIA Streamline SDK 2.14.1** -- headers, and the NVAPI headers it carries | NVIDIA Corporation | MIT | The Streamline headers in `src/DxgiProxy/extern/Streamline/include`, its signature check `sl_security.h` among them, compiled into `dxgi.dll`; function ids and structures from NVAPI's `nvapi.h` and `nvapi_interface.h` declared in `NvidiaGpu.cpp`. Their notices are in `licenses/NVIDIA-MIT.txt`, which the package puts next to `dxgi.dll` as `dxgi_LICENSE-NVIDIA.txt` |

## Used to build or run, not in the package

| Work | By | Licence | How it is used |
|---|---|---|---|
| **Harmony** | Andreas Pardeike (pardeike); packaged for KSP as HarmonyKSP by gotmachine, DasSkelett, HebaruSan | MIT | Patches KSP's settings dialog, the bundled mods' own settings windows (a close button), Scatterer's godray set-up (`RaymarchedGodraysRenderer.Init`), EVE's cloud renderer (the jittered projection), and PostProcessing's `UpdateSettings` (TUFX's effects split around the upscaler); follows a change made in TUFX's and Distant Object's own windows into the settings window, and sets the settings window's choices into each Distant Object settings file as Distant Object reads it; **required** (`KSPAssemblyDependency`), referenced from `000_Harmony` |
| **NVIDIA DLSS** (`nvngx_dlss.dll`) and **DLSS frame generation** through **Streamline** (`sl.interposer.dll`, `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll`, `nvngx_dlssg.dll`) | NVIDIA Corporation | NVIDIA's licences, shown to the player before the download | The player downloads them from NVIDIA's release of the Streamline SDK 2.14.1 on GitHub with *NVIDIA DLSS files* in the settings window, after accepting NVIDIA's licences; the proxy loads them from next to `KSP_x64.exe` |
| **AMD FidelityFX upscaler** (`amd_fidelityfx_upscaler_dx12.dll`) | Advanced Micro Devices | AMD's SDK licence | The player's own copy, loaded by the proxy |
| **Kerbal Space Program** | Squad; published by Private Division | proprietary | The game; its assemblies are referenced |
| **Unity** | Unity Technologies | proprietary | The engine |
| **ILSpy** | the ICSharpCode team | MIT | Tool: decompiling KSP and installed mods for research |
| **CKAN** | the KSP-CKAN team | -- | Tool: its download cache held the release archives of the installed mods, whose shipped settings were read for the defaults, and its registry ModuleManager's metadata |

## Mods ReDefinition talks to at run time

Their defaults ([reference/mod-defaults.md](reference/mod-defaults.md)) were read from
each release as shipped and from their code, and are the values in ReDefinition's
registrations.

| Mod | By | Licence | What this project does with it |
|---|---|---|---|
| **Scatterer** | Ghassen Lahmar (blackrack); with JonnyOThan, blowfishpro, soulsource, linuxgurugamer, tarsolya | GPL-3.0 | While a graphics profile is chosen, its TAA and SMAA are switched off for the upscaler and what its TAA leaves behind is cleaned up; its cloud reconstruction's motion vectors are read for the upscaler. Its main settings are bundled in the settings window: set in its running settings object and its GameDatabase node, and its config file saved -- only those it marks `[Persistent]`, the only ones its own save keeps. Its public release (0.878) and Volumetric Clouds' build (0.908) are told apart by their members, for the defaults of each |
| **TUFX** | shadowmage45; maintained by the KSPModStewards, with JonnyOThan, al2me6, HebaruSan, LGhassen, NathanKell | GPL-3.0 | While a graphics profile is chosen, its antialiasing mode is set to none; its effects that belong after the upscaler are drawn over the upscaler's output from its own volumes. The post-processing profile for each scene is bundled in the settings window, set in its configuration and the loaded save as its own window sets it, applied through its `ApplyProfile`, and followed from its window through a Harmony postfix on `ChangeProfileForScene`; its stock button hidden through ToolbarControl. Whether a flight profile has ambient occlusion -- what Volumetric Clouds asks for -- is read from the effects of the profiles it has loaded (`TUFXProfile.Settings`) |
| **EVE Redux** and its **raymarched volumetric clouds** | rbray89, LGhassen, WazWaz, R-T-B, al2me6, BiozTech | EVE core MIT (Ryan Bray); the volumetric branch "All rights reserved" | Compatibility: where the clouds render and which projection they reproject with; the clouds get the upscaler's jitter through a Harmony postfix, and their motion vectors go into the upscaler's. Its cloud quality -- and for the reset its whole quality object with its light volume -- is set from the settings window in its statics and its config node, the clouds rebuilt with its own `ReinitAll` and the config saved with its own `SaveConfig` |
| **Deferred** | LGhassen, drewcassidy, JonnyOThan, baroneight, nohimazin | GPL-3.0 | Compatibility; its screen-space reflections set on its running settings from the settings window at every start -- it has no save routine of its own -- and the rest of its settings for the run by *Reset to defaults*. Its two caps on KSP's reflection probe are left to it: they make Deferred write KSP's own settings at every scene load. They are read from its config node, as its loader reads them, so that KSP's reflection rows offer only what Deferred lets stand |
| **Parallax Continued** | Gameslinx, Phantomical, BurgerKerman, ballisticfox, Linx-RW, 0x00ASTRA | "All Rights Reserved" | Compatibility; its global settings set from the settings window through reflection, applied through its own public normalisation calls and saved with its own `SaveSettings`. Its `CheckSettings` is the source of two requirements kept while it is installed -- KSP's highest terrain detail, reflections at 256 at most -- and its installation instructions of a third, reflections not off |
| **Waterfall** | ChrisAdderley, JonnyOThan, zorg2044, KnightofStJohn, DRVeyl, ArXen42 (KSPModStewards) | CC BY-NC-SA 4.0 | Compatibility; its plume lights and heat distortion set from the settings window, and the rest of its settings for *Reset to defaults*, through reflection |
| **Firefly** | M1rageDev, JonnyOThan, SPACEMAN9813, drewcassidy, giuliodondi | code GPL-3.0, assets "All Rights Reserved" | Compatibility; its re-entry effect settings set on its running settings from the settings window and saved with its own `SaveModSettings`; its HDR override for the reset only |
| **Distant Object Enhancement /L** | Lisias, MOARdV, duckytopia, TheDarkBadger, Kerbas-ad-astra, Clayell; its NOTICE also names Rubber Ducky | SKL 1.0 or GPL-2.0 | Its flare, distant-vessel and sky settings bundled in the settings window, set on its running settings object and saved with its own `Save`, per save; Harmony postfixes on its `Load` and its window's `ApplySettings` keep its window and the settings window alike; its debug and toolbar switches for the reset only |
| **ToolbarControl** | linuxgurugamer | LGPL-3.0 | Its instance list is read to find and hide the stock toolbar button of a mod bundled here (TUFX's) |

## Mods ReDefinition learnt from or is made compatible with

| Mod | By | Licence | What this project learnt from it, or does with it |
|---|---|---|---|
| **KSPCommunityFixes** | gotmachine, NathanKell, JonnyOThan, Phantomical, siimav, tobiasnmf (KSPModdingLibs) | MIT | The way to add a section to KSP's own settings dialog; what KSP's floating origin moves, and by how much (`FloatingOriginPerf`) |
| **KerbalVR** | Vivero, jrbudda | MIT | The list of cameras that make up KSP's 3D image |
| **ClickThroughBlocker** | linuxgurugamer, chambm, HebaruSan, SteveBenz | LGPL-3.0 | How a per-save setting is made global (its `Global.cfg`); how a window keeps clicks from what lies behind it -- `ALLBUTCAMERAS` locked while the cursor is over one (its `FocusLock`), which this mod's windows do the same way |
| **RemoteTech** | Peppie84, KSP-TaxiService, Starstrider42, neitsa, d4rksh4de, tomekpiotrowski (RemoteTechnologiesGroup) | GPL-2.0 | An options window of its own in the space centre |
| **ZTheme** | zapSNH, OnlyLightMatters, Phantomical | GPL-3.0 | The UI baseline: the settings are built from KSP's own elements so that it themes them |
| **HUDReplacer** | UltraJohn, Phantomical, Aeurias, zapSNH (KSPModStewards) | GPL-3.0 | How ZTheme reaches KSP's UI; it also patches KSP's IMGUI skin, which the diagnostics window draws with |
| **FreeIva** | pizzaoverhead, JonnyOThan, Icecovery | GPL-2.0 | Compatibility: its hatches take their clicks through Unity's mouse events, for which the rig lends its cameras' screen |
| **Trajectories** | Youen Toupin (neuoy), A. Korsunsky (fat-lobyte), S. Gray (PiezPiedPy), sawyerap, mic-e, Baleine82 | GPL-3.0-or-later | Compatibility; the worked example of a registration, `docs/modders/examples/Trajectories.cfg`: its settings, the ranges of its own sliders and its save routine, checked against the installed 2.4.5.4. Not bundled by this mod |
| **Kopernicus** | R-T-B, StollD, Sigma88, Phantomical, teknoman117, NathanKell | LGPL-3.0 | Compatibility: sun flares and rings, and its more precise floating origin. Its `EnforceShaders` and `WarnShaders` are read at run time, so that KSP's terrain shader quality is left to it where it holds a level |
| **Shabby** | drewcassidy, JonnyOThan, taniwha, al2me6, gotmachine (KSPModdingLibs) | GPL-3.0 | Installed alongside |
| **KSPTextureLoader** | Phantomical | MIT | Installed alongside |
| **Singularity** | LGhassen, JonnyOThan, prustic; portions by Pim Schreurs (sirxemic/Interstellar) | MIT | Compatibility; its three global settings would be worth bundling, but it is not installed here, so no registration was written |
| **Hullcam VDS Continued** | linuxgurugamer, Aahz88, 4x4cheesecake, Kerbas-ad-astra, Lisias, TedThompson | GPL-3.0 | Compatibility |
| **CameraTools** | BrettRyland (this fork), jrodrigv, josuenos, Halbann, BahamutoD, illectro | no licence file in the repository | Compatibility |
| **SmokeScreen** | sarbian, eggrobin, Felger, PatPL, r4m0n, Scialytic | BSD-2-Clause | Compatibility |
| **Kerbal Frame Generator** (formerly KSR) | MangoTechKSP (its licence names "MicrosoftFlightSimulator") | MIT | A frame blending mod; while a graphics profile is chosen, its blend is switched off for the run |
| **KSPSS** | bingus108 | MIT | A research project for DLSS, XeSS and FSR in KSP |
| **Volumetric Clouds** (the raymarched volumetrics preview package) | Ghassen Lahmar (blackrack) | "All rights reserved" (its `License.txt`, which also names its STBN noise as licensed to NVIDIA and its lightning sounds as licensed from Epidemic sounds) | Its Readme's step 3 -- a TUFX profile with ambient occlusion in flight, the author's own or another -- is the requirement R5, and the settings it ships are the defaults where it is installed |
| **ModuleManager** | ialdabaoth, Sarbian, Blowfish | CC-BY-SA (its CKAN metadata) | The registrations and profiles are config nodes a pack can patch with it; its `CheckConstraints` (4.2.3) shows how a patch picks a block for one build |

## Beyond KSP

| Work | By | Licence | What this project took from it |
|---|---|---|---|
| **DynamicShaderFrameGen** (Skyrim SE) | jatelop8 | GPL-3.0 | The approach the `dxgi.dll` proxy follows: a shared Direct3D 11 backbuffer presented through a Direct3D 12 swapchain with frame generation, its settings in an INI next to the game |
| **Skyrim Community Shaders** | doodlum / Pentalimb and contributors | GPL-3.0 | The Direct3D 12 swapchain for a Direct3D 11 game that DynamicShaderFrameGen builds on; features as modules, each with its own settings, in one menu |
| **ENBFrameGeneration** | doodlum / Pentalimb | GPL-3.0 | FSR 3.1 frame generation in a Direct3D 11 game, which DynamicShaderFrameGen builds on |
| **VRisingPerfMod** (V Rising) | PureDark | -- | The first known DLSS/FSR 2 injection into a Unity game without an upscaler of its own |
| **Unturned's FSR 3.1** | Smartly Dressed Games, built on FSR3Unity | -- | Evidence that FSR 3 works with Unity's built-in pipeline and post-processing stack v2 on DX11 |
| **OptiScaler** | the OptiScaler team | GPL-3.0 | How a generic injector finds the HUD-less texture among a game's; ReDefinition copies the backbuffer at the right camera instead |
| **FidelityFX documentation** | AMD | as the SDK | The primary source for FSR's upscaler and frame generation |
| **Unity's built-in shader source** (`Internal-MotionVectors.shader`, `Internal-Colored.shader`, `UnityShaderVariables.cginc`) | Unity Technologies | MIT | How Unity encodes and computes motion vectors, for the motion vector hook and `ReDefinition.cginc`; what `_ProjectionParams` and `_ScreenParams` hold; the debug line shader the example mod draws with |
| **DLSS Programming Guide**, **Streamline documentation** (ProgrammingGuideDLSS_G.md, ProgrammingGuideReflex.md, ProgrammingGuideManualHooking.md) | NVIDIA | as NVIDIA publishes them | The primary source for DLSS and DLSS frame generation: NGX's calls, which `Dlss.cpp` declares from the guide, and Streamline's hooking, tags, fences and V-Sync rules |
