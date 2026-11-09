# Packaging

What the build puts where, what it leaves out, which licences decide that, and the CKAN
metadata. To build the package itself: [building-and-testing.md](building-and-testing.md).

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## How comparable mods ship

**What installed mods ship [src]**, from their folders in this installation's `GameData`.
Named here is what bears on a package's layout, not every file they hold:

| Mod | In its folder |
|---|---|
| TUFX | `Plugins/`, `Shaders/`, `Profiles/`, `TUFX.cfg`, `TUFX.version`, a changelog |
| KSPCommunityFixes | `Plugins/`, `KSPCommunityFixes.version`, `README.md`, `CHANGELOG.md`, `Settings.cfg`, `Localization/`, `MMPatches/` |
| HUDReplacer | the DLL, `HUDReplacer.version`, `LICENSE`, `README.md`, `Settings.cfg` |
| ClickThroughBlocker, in `000_ClickThroughBlocker` | `Plugins/`, `ClickThroughBlocker.version`, `LICENSE.md`, `README.md`, a changelog |
| Trajectories | `Plugins/`, `Trajectories.version`, `LICENSE.md`, `COPYRIGHTS.md`, `README.md`, `CHANGELOG.md` |

**The KSP-AVC version file [src]**, from TUFX's and KSPCommunityFixes' own, is JSON with
`NAME`, `VERSION`, `KSP_VERSION`, `KSP_VERSION_MIN` and `KSP_VERSION_MAX`. A mod with a
public home adds more keys. Both files carry `URL` and `DOWNLOAD`, and TUFX's also carries
`CHANGE_LOG_URL`:

| Key | What it holds |
|---|---|
| `URL` | the remote copy of the version file, which the update check compares with the installed one |
| `DOWNLOAD` | where the player gets the mod |
| `CHANGE_LOG_URL` | the changelog |

**How KSPCommunityFixes builds its package [src]** (`KSPCommunityFixes.csproj`): static
files in a `GameData/KSPCommunityFixes` folder of the repository, the version file
generated from the project's version with KSPBuildTools, a target that copies everything
into the game after each build, and on Release a zip of `GameData/<Mod>` with README and
changelog inside.

**The forum's add-on posting rules [doc]**, via a search result, because the forum refuses
automated reads: add-ons are to be accompanied by their source code, where applicable,
and a licence, in both the post and the download file.

**CKAN [doc]**, from comparable plugins' metadata: KSPCommunityFixes and HUDReplacer
depend on `Harmony2`, ZTheme depends on HUDReplacer, and the version file is read through
`$vref: '#/ckan/ksp-avc'`.

## What every build does

Every `dotnet build` generates `ReDefinition.version` in `GameData/ReDefinition/Main` of the
build output from the project's version [src]. It generates `ReDefinitionPlanner.version`
in `Planner` and `ReDefinitionCommon.version` in `Common` beside it. It copies the version
files, the three DLLs, `ReDefinition.xml` and the iCUE widget `ksp-dashboard.icuewidget`
into the game's `GameData/ReDefinition`. It copies the repository's static `GameData`
files too: the profiles, the registrations, the patches, the icons and the dashboard's pages.

The version file carries all three keys above. `URL` is the copy on the repository's
`main` branch, `DOWNLOAD` is the latest GitHub release, and `CHANGE_LOG_URL` is
`CHANGELOG.md` on `main`.

It also carries the KSP versions. `KSP_VERSION` is 1.12.5, the version ReDefinition is
built and tested against. `KSP_VERSION_MIN` is 1.12.0 and `KSP_VERSION_MAX` is 1.12.99,
because 1.12.0 and up run the same KSP interfaces this mod uses. That is the range EVE and
Kopernicus give.

The build writes the same three version files into the repository root as well. The release
package refuses a working tree that differs from `HEAD`, so a version goes out only with
its copy committed.

## What the release build puts in the zip

The player package is built on request:

```
dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true
```

It is built without `DEVELOPMENT_BUILD`, so FSR's debug checks and its debug view pass are
compiled out. The developer builds keep them.

It assembles `build/release/ReDefinition_<version>.zip` [src]. All three mods share one
folder, `GameData/ReDefinition`, with a part for each:

```
GameData/ReDefinition/Main/
    Plugins/ReDefinition.dll
    Plugins/ReDefinition.xml  the interface for mods, documented for an IDE
    Shaders/redefinition.shaders
    Profiles/ReDefinition-Profiles.cfg
    Mods/*.cfg                the registrations of the bundled mods
    Patches/*.cfg             ModuleManager patches: landing leg grip, Parallax grass room
    Icons/*.png               the toolbar button and the HUD's pictures
    Dashboard/                the dashboard's pages
    ksp-dashboard.icuewidget  the same pages as an iCUE widget
    ReDefinition.version
    LICENSE, EXCEPTIONS.md    this project's licence
    CHANGELOG.md              what each release changed
    LICENSE-FSR3Unity.txt     FSR3Unity, MIT, compiled into the DLL
    LICENSE-FidelityFX.txt    AMD's FSR 3 shaders, MIT, compiled into the bundle
    LICENSE-Telemachus.txt    Telemachus, MIT, whose key names the dashboard shares where a key exists in both
    LICENSE-QR-Code-generator.txt  Project Nayuki's QR code generator, MIT, which the
                              dashboard's QR code adapts (a C# translation of the Java version)
    LICENSE-Bruneton.txt      Bruneton's atmosphere model, BSD-3-Clause
    LICENSE-Unity-shaders.txt Unity's built-in shader source, MIT
    LICENSE-Unity-Companion.txt  the Unity Companion License of the SRP macros in the FSR shader wrapper
    SOURCE.md                 where the source is
    CREDITS.md                docs/credits-and-licences.md
    README.md                 the part's own note, GameData/ReDefinition/Main/README.md
    ReDefinitionProxy.ini     template for the proxy's settings, see below
GameData/ReDefinition/Common/ the shared DLL, see below
GameData/ReDefinition/Planner/ the planner, see below
dxgi.dll                      the proxy
dxgi_LICENSE-NVIDIA.txt       the MIT notices of NVIDIA's Streamline and NVAPI
                              headers compiled into dxgi.dll
dxgi_LICENSE-FidelityFX.txt   the MIT notice of AMD's FidelityFX API headers
                              compiled into dxgi.dll
dxgi_LICENSE-AntiLag2.txt     the MIT notice of AMD's Anti-Lag 2 headers
                              compiled into dxgi.dll
dxgi_LICENSE-Capsaicin.txt    the MIT notice of AMD's Capsaicin code, adapted in the
                              radiance cache and compiled into dxgi.dll
dxgi_LICENSE-XeSS.txt         the MIT notice of Intel's XeSS and XeLL headers
                              compiled into dxgi.dll
amd_fidelityfx_framegeneration_dx12.dll
                              AMD's frame generation runtime, 4.0.1 from FSR SDK v2.3.0
amd_fidelityfx_framegeneration_dx12_LICENSE.md
                              AMD's licence for it, verbatim
amd_fidelityfx_denoiser_dx12.dll
                              AMD FSR Ray Regeneration for the raytracing, 1.2.0 from
                              FSR SDK v2.3.0
amd_fidelityfx_denoiser_dx12_LICENSE.md
                              AMD's licence for it, verbatim
```

The zip's layout is the KSP folder's, so extracting it there merges `GameData` and puts
the rest next to `KSP_x64.exe` ([player/installing.md](../player/installing.md)).

Next to the zip goes `ReDefinition_<version>_source.zip`, the complete source of the
commit it was built from, made with `git archive`. GPL-3.0 section 6(d) asks for the
source to be offered in the same place as the binary.

## The planner's and Common's packages

The same build makes two more zips beside the full one [src]. The full zip carries both
parts too, so that a player who installs ReDefinition needs nothing else.

`build/release/ReDefinitionPlanner_<PlannerVersion>.zip` holds the planner and Common:

```
GameData/ReDefinition/Planner/
    Plugins/ReDefinition.Planner.dll
    Icons/*.png               the planner's tiles, symbols and the Live button's pictures
    ReDefinitionPlanner.version
    LICENSE, EXCEPTIONS.md, CHANGELOG.md
    README.md                 the part's own note, src/Planner/README.md
GameData/ReDefinition/Common/
    Plugins/ReDefinition.Common.dll
    ReDefinitionCommon.version
```

`build/release/ReDefinitionCommon_<CommonVersion>.zip` holds `GameData/ReDefinition/Common`
alone, with its own KSP-AVC file, so that CKAN can install Common as a module of its own.
Common is the one copy of `ReDefinition.Common.dll`, and both mods use it.

Extracting any of the three zips into the KSP folder merges into the same
`GameData/ReDefinition`. The zips carry no `PluginData`. Without ReDefinition, the planner
writes its two switches to `Planner/PluginData/settings.cfg` itself
(`src/Planner/PlannerSettings.cs`) [src].

| Version | Where it is set | What it is for |
|---|---|---|
| ReDefinition's | `<Version>` in `src/ReDefinition.csproj` | `ReDefinition.version` and the zip's name |
| the planner's | `PlannerVersion` in `Directory.Build.props` | `ReDefinitionPlanner.version`, the zip's name, the DLL's assembly version |
| Common's | `CommonVersion` in `Directory.Build.props` | `ReDefinitionCommon.version`, its zip's name, the DLL's assembly version |

With one copy of `ReDefinition.Common.dll` in the game there is no duplicate for KSP to
resolve [src]. The caveat is the zips' order. A planner zip of an older release extracted
over a newer full zip replaces the one Common DLL with an older one, so keep the releases
together. Raise `CommonVersion` whenever `src/Common/` changes. `src/Common/version.lock`
and `CommonVersionTests` make the build refuse a change without it.

The deploy of a developer build keeps one copy of the DLL too. It deletes a
`ReDefinition.Common.dll` or `ReDefinition.Planner.dll` left in `Main/Plugins` and a Common
copy in `Planner/Plugins`, and copies Common to `Common/Plugins` [src].

It cleans up the layout of earlier builds as well. It moves the old `Shaders` and
`PluginData` into `Main`, and the old planner folder's `PluginData` into `Planner`, unless
those exist there already. It then removes the old top-level `Plugins`, `Profiles`, `Mods`,
`Patches`, `Icons`, `Dashboard` and `GameData/ReDefinitionPlanner`, because KSP would read
their textures, configs and DLLs a second time [src]. ReDefinition does not read the old
`GameData/ReDefinition/PluginData` any more, and nothing from the settings of earlier
builds is carried over by the game itself.

## When the release build refuses

The command needs `-c Release`, or the build stops at once. In these cases there is no zip,
and one left from an earlier build is removed:

* the proxy, `build/DxgiProxy/Release/dxgi.dll`, is missing or older than any native
  source;
* one of AMD's runtimes is missing, or does not match its pinned SHA-256 in
  `third_party/amd`, which `python tools/fetch_amd_runtime.py` fetches;
* the shader bundle is missing;
* any shader source under `unity/Assets/ReDefinition/Shaders`, include under
  `unity/Assets/ReDefinition/Include` or editor script under `unity/Assets/ReDefinition/Editor`
  is newer than the bundle. The Unity project builds the bundle directly into the game's
  `GameData` (`unity/Assets/ReDefinition/Editor/BundleBuilder.cs`), and the package takes
  it from there;
* git is not on the path, so the source that goes with the package cannot be made;
* the working tree has changes or untracked files, so `HEAD`'s source would not match the
  binary.

## What is not in the zip

* The player's `Main/PluginData/settings.cfg` and `bundled.cfg`. They are the player's.
* A `ReDefinitionProxy.ini` in the game folder. One there holds the player's settings,
  which an update must not overwrite, and CKAN would refuse to install over it. The proxy
  writes the file itself, as the [settings file](#the-proxys-settings-file) describes. The
  copy in `GameData/ReDefinition/Main` is the same template, for reading. The same goes for
  `ReDefinitionProxy.defaults`, which the proxy keeps beside the ini.
* **NVIDIA's DLLs.** These are `nvngx_dlss.dll` for DLSS, and `sl.interposer.dll`,
  `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll` and `nvngx_dlssg.dll`
  for DLSS frame generation, and `sl.dlss_d.dll` and `nvngx_dlssd.dll` for DLSS Ray
  Reconstruction. The player downloads them from NVIDIA's release of the Streamline SDK
  2.14.16 on GitHub. The settings window offers them in its *NVIDIA's DLSS files* dialog,
  where the player accepts NVIDIA's licences (`src/Vendor/NvidiaFiles.cs` and
  `src/Vendor/NvidiaDownloader.cs`). Every file's SHA-256 is pinned.
* **AMD's upscaler DLL**, `amd_fidelityfx_upscaler_dx12.dll`. The player brings their own
  copy.
* **Intel's DLLs**, `libxess.dll` for XeSS, and `libxess_fg.dll` and `libxell.dll` for
  XeSS frame generation. The player downloads them from Intel's release of the XeSS SDK
  3.0.2 on GitHub. The settings window offers them in its *Intel's XeSS files* dialog, where
  the player accepts Intel's licence (`src/Vendor/IntelFiles.cs` and
  `src/Vendor/IntelDownloader.cs`). Every file's SHA-256 is pinned. `NvidiaFiles` does the
  checking and placing. Only the MIT headers of the SDK are compiled into the proxy.
* **dlssg_for_sm86**, which runs DLSS frame generation on RTX 30 and RTX 20. ReDefinition
  neither ships nor downloads it. The player gets the release zip from the project's page and
  puts it into the game folder, and the settings window installs the files from the zip
  (`src/Vendor/AmperePackage.cs`).

## Licences and hosts behind the game-folder files

* **AMD's licence [src]** is the release's `Kits/FidelityFX/docs/license.md`, verbatim in
  `licenses/AMD-FidelityFX-SDK-license.md`. It allows redistribution "in binary form only",
  reproducing AMD's copyright notice, the permission notice and the disclaimers. It forbids
  reverse engineering, decompilation and disassembly. The package puts the licence next to
  each DLL.
* **AMD's runtimes [src]** come from FSR SDK v2.3.0 of 2026-06-24. Its frame generation
  header says 4.0.1, the version the proxy is built against. The package takes the release's
  `signedbin` DLL, SHA-256 `02297bee...`. Only this one AMD DLL is needed for FSR frame
  generation: the proxy harness passes with nothing else beside it [open: not run again for
  this page]. The raytracing's AMD FSR Ray Regeneration is the same release's signed
  `amd_fidelityfx_denoiser_dx12.dll`, file version 1.2.0.2740, built against its 1.2.0
  header. The pins, the source and the signatures are in `third_party/amd/README.md`.
* **The headers' notices [src]:** `licenses/NVIDIA-MIT.txt` holds, verbatim, the MIT notices
  of the Streamline 2.14.16 headers in `src/DxgiProxy/extern/Streamline/include`, the
  signature check in `sl_security.h` among them. It also holds the notice of the NVAPI
  headers whose function ids and structures `src/DxgiProxy/Upscalers/NvidiaGpu.cpp`
  declares.
* **The other notices [src]:** `licenses/AMD-FidelityFX-API-MIT.txt` holds the MIT notice of
  AMD's FidelityFX API headers in `src/DxgiProxy/extern/FidelityFX`, the loader
  `ffx_api_loader.h` among them. `licenses/AMD-AntiLag2-MIT.txt` holds that of AMD's Anti-Lag 2
  headers in `src/DxgiProxy/extern/AntiLag2`, and `licenses/Intel-XeSS-MIT.txt` that of
  Intel's headers in `src/DxgiProxy/extern/XeSS`. `licenses/AMD-Capsaicin-MIT.txt` holds that
  of the code adapted in `src/DxgiProxy/Raytracing/Shaders/RadianceCache.hlsli`. The package
  puts all of them next to `dxgi.dll`.
* **CKAN [src]:** its spec allows `install_to: GameRoot` for KSP 1, which it says "should be
  used sparingly, if at all". Advanced Fly-By-Wire's NetKAN installs `SDL2.dll` and
  `XInputInterface.dll` with their licences there. CKAN never overwrites a file it did not
  install (`Core/IO/ModuleInstaller.cs`: "We don't allow for the overwriting of files.").
  A player who already has another tool's `dxgi.dll` has to move it away first, ReShade's for
  instance. The same holds for an AMD runtime from OptiScaler next to `KSP_x64.exe`. That is
  also why the ini is not installed there. `license` takes a list, and for mixed assets the
  spec asks for the most restrictive of the licences the package carries. AMD's allows
  redistribution in binary form only, which is CKAN's `unrestricted`.
* **CurseForge [src]:** its app is dropping KSP. Its App Release Notes 1.277 say "CurseForge
  App support for KSP is going to be deprecated. Existing KSP mods will remain accessible on
  the website." Players download the zip there and extract it by hand, which the layout above
  is made for. Its moderation policy asks for a licence or permission, with credit, for
  third-party content. AMD's licence and notices, and NVIDIA's notices, are in the zip.

Sources: https://github.com/KSP-CKAN/CKAN/blob/master/Spec.md,
https://github.com/KSP-CKAN/NetKAN/blob/master/NetKAN/AdvancedFlyByWire.netkan,
https://github.com/KSP-CKAN/CKAN/blob/master/Core/IO/ModuleInstaller.cs,
https://blog.curseforge.com/app-release-notes-1-277/,
https://support.curseforge.com/support/solutions/articles/9000197279-moderation-policies

## The proxy's settings file

The template is `src/DxgiProxy/ReDefinitionProxy.ini`. CMake builds it into `dxgi.dll` as a
resource (`src/DxgiProxy/ReDefinitionProxy.rc.in`), and the release build copies it into
`GameData/ReDefinition/Main` as well. At every start the proxy completes the file next to
`KSP_x64.exe` from its own copy (`CompleteIni` in `src/DxgiProxy/Common/Config.cpp`):

* Without a file, it writes the template as it is.
* A file from an earlier version gets the keys added since, each with its default and
  the comment above it in the template. Values already there stay.
* Where another section than `[Proxy]` ends the file, or the file is UTF-16, the keys go
  into `[Proxy]` through Windows' profile functions, without their comments.
* A file that exists but cannot be read is left alone, and the log says so.

Beside the ini the proxy keeps `ReDefinitionProxy.defaults`, whose `[Written]` section holds
the values it wrote into the player's file itself. Where the ini still holds such a value
and this version's template has another default, the proxy sets the new default and logs
"Config: defaults the proxy had written raised to this version's in ReDefinitionProxy.ini".
A value the player changed stays. A key without a record counts as the proxy's where it
holds today's default, and as the player's otherwise.

## The CKAN modules

CKAN gets three modules, one file each in https://github.com/KSP-CKAN/NetKAN under
`NetKAN/` [open: not yet submitted]. Each reads its own zip of the same GitHub release and
its own KSP-AVC file:

| Module | Zip | Installs | Depends on | Recommends |
|---|---|---|---|---|
| `ReDefinition` | `ReDefinition_<version>.zip` | `Main` and the files for the game folder | `Harmony2`, `ReDefinitionCommon` | `ReDefinitionPlanner` |
| `ReDefinitionPlanner` | `ReDefinitionPlanner_<PlannerVersion>.zip` | `Planner` | `ReDefinitionCommon` | `MechJeb2`, `ReDefinition` |
| `ReDefinitionCommon` | `ReDefinitionCommon_<CommonVersion>.zip` | `Common` | nothing | nothing |

CKAN installs a `file` that is a directory under its last path component. To keep each part
below `GameData/ReDefinition`, every entry names the part and installs it to
`GameData/ReDefinition`, which the spec allows as a sub-folder of `GameData` [doc]. No two
modules own the same file, so CKAN never refuses an install. The full zip carries all three
parts, and each module takes only its own.

ReDefinition works without the planner. The planner works without ReDefinition. Both need
Common, and that is why Common is a module of its own.

## The CKAN metadata of ReDefinition

The local drafts are `build/ckan/ReDefinition.netkan`, `ReDefinitionPlanner.netkan` and
`ReDefinitionCommon.netkan`. Git does not track them. The YAML on this page is the current
one. Every `file` is in its release zip [src]. The identifiers in `depends`, `recommends`
and `supports` are not checked against the NetKAN repository [open].

| Key | What it does |
|---|---|
| `$kref` | takes the GitHub release; `asset_match` picks the player zip, not `ReDefinition_<version>_source.zip` or the planner's zip beside it |
| `$vref` | reads `ReDefinition.version` in the zip for the KSP versions |
| `x_netkan_trust_version_file` | takes the mod's version from that file too, so it reads the project's version and not the tag's `v` in front of it |
| `license` | `GPL-3.0` for ReDefinition, `unrestricted` for AMD's frame generation and denoiser runtimes |
| `depends` | `Harmony2`, which ReDefinition requires to load, and `ReDefinitionCommon` |
| `recommends` | `ReDefinitionPlanner`, which the full zip carries but CKAN installs from its own module. ReDefinition works without it |
| `install` | `GameData/ReDefinition/Main` into `GameData/ReDefinition`; `dxgi.dll`, AMD's two runtimes and the licences into `GameRoot` |

CKAN has no identifier for the Modding and Linking Exceptions. They are in `LICENSE` and
`EXCEPTIONS.md` in the zip.

A new GitHub release reaches CKAN without a change to the file.

```yaml
identifier: ReDefinition
name: ReDefinition
abstract: >-
  Brings KSP's mods together in one concept: their settings in one window, shared
  profiles and compatible settings. Adds FSR and DLSS upscaling, and frame generation
  through Direct3D 12.
author: CaptainKeyboard
$kref: '#/ckan/github/CaptainKeyboard/ReDefinition/asset_match/^ReDefinition_[0-9.]+\.zip$'
$vref: '#/ckan/ksp-avc'
x_netkan_trust_version_file: true
license:
  - GPL-3.0
  - unrestricted
tags:
  - plugin
  - graphics
  - library
depends:
  - name: Harmony2
  - name: ReDefinitionCommon
recommends:
  - name: ReDefinitionPlanner
supports:
  - name: Scatterer
  - name: EnvironmentalVisualEnhancements
  - name: TUFX
  - name: Deferred
  - name: ParallaxContinued
  - name: Waterfall
  - name: Firefly
  - name: DistantObject
  - name: Kopernicus
  - name: Trajectories
  - name: Singularity
  - name: HUDReplacer
  - name: ZTheme
install:
  - file: GameData/ReDefinition/Main
    install_to: GameData/ReDefinition
  - file: dxgi.dll
    install_to: GameRoot
  - file: dxgi_LICENSE-NVIDIA.txt
    install_to: GameRoot
  - file: dxgi_LICENSE-FidelityFX.txt
    install_to: GameRoot
  - file: dxgi_LICENSE-AntiLag2.txt
    install_to: GameRoot
  - file: dxgi_LICENSE-Capsaicin.txt
    install_to: GameRoot
  - file: dxgi_LICENSE-XeSS.txt
    install_to: GameRoot
  - file: amd_fidelityfx_framegeneration_dx12.dll
    install_to: GameRoot
  - file: amd_fidelityfx_framegeneration_dx12_LICENSE.md
    install_to: GameRoot
  - file: amd_fidelityfx_denoiser_dx12.dll
    install_to: GameRoot
  - file: amd_fidelityfx_denoiser_dx12_LICENSE.md
    install_to: GameRoot
```

## The CKAN metadata of the planner

`NetKAN/ReDefinitionPlanner.netkan` reads `ReDefinitionPlanner.version` in its zip. It
depends on `ReDefinitionCommon`, recommends MechJeb2 and ReDefinition, and installs
`Planner`. It carries `GPL-3.0` only, because AMD's runtimes belong to ReDefinition's module.

```yaml
identifier: ReDefinitionPlanner
name: ReDefinition Planner
abstract: >-
  Plans a route for a vessel (launch, transfers, landing, rendezvous) in the map and the editor,
  and flies it with the autopilot, with MechJeb where it is installed.
author: CaptainKeyboard
$kref: '#/ckan/github/CaptainKeyboard/ReDefinition/asset_match/^ReDefinitionPlanner_[0-9.]+\.zip$'
$vref: '#/ckan/ksp-avc'
x_netkan_trust_version_file: true
license:
  - GPL-3.0
tags:
  - plugin
  - control
  - convenience
depends:
  - name: ReDefinitionCommon
recommends:
  - name: MechJeb2
  - name: ReDefinition
supports:
  - name: Parsek
  - name: ZTheme
install:
  - file: GameData/ReDefinition/Planner
    install_to: GameData/ReDefinition
```

## The CKAN metadata of Common

`NetKAN/ReDefinitionCommon.netkan` is the module of the shared library. It reads
`ReDefinitionCommon.version` in `ReDefinitionCommon_<CommonVersion>.zip`, and its tag is
`library`.

```yaml
identifier: ReDefinitionCommon
name: ReDefinition Common
abstract: >-
  A shared library of ReDefinition and ReDefinition Planner.
author: CaptainKeyboard
$kref: '#/ckan/github/CaptainKeyboard/ReDefinition/asset_match/^ReDefinitionCommon_[0-9.]+\.zip$'
$vref: '#/ckan/ksp-avc'
x_netkan_trust_version_file: true
license: GPL-3.0
tags:
  - library
install:
  - file: GameData/ReDefinition/Common
    install_to: GameData/ReDefinition
```
