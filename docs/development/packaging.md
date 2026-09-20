# Packaging

**For:** anyone building the release package, or asking why a file is in it.
**You need:** nothing to read this. To build the package itself:
[building-and-testing.md](building-and-testing.md).
**You get:** what the build puts where, what it leaves out, which licences decide that,
and the CKAN metadata.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## How comparable mods ship

**What installed mods ship [src]**, from their folders in this installation's `GameData`:

| Mod | In its folder |
|---|---|
| TUFX | `Plugins/`, `Shaders/`, `Profiles/`, `TUFX.cfg`, `TUFX.version`, a changelog |
| KSPCommunityFixes | `Plugins/`, `KSPCommunityFixes.version`, `README.md`, `CHANGELOG.md`, `Settings.cfg`, `Localization/`, `MMPatches/` |
| HUDReplacer | the DLL, `HUDReplacer.version`, `LICENSE`, `README.md`, `Settings.cfg` |
| ClickThroughBlocker | `Plugins/`, `ClickThroughBlocker.version`, `LICENSE.md`, `README.md`, a changelog |
| Trajectories | `Plugins/`, `Trajectories.version`, `LICENSE.md`, `COPYRIGHTS.md`, `README.md`, `CHANGELOG.md` |

**The KSP-AVC version file [src]**, from TUFX's and KSPCommunityFixes' own, is JSON with
`NAME`, `VERSION`, `KSP_VERSION`, `KSP_VERSION_MIN` and `KSP_VERSION_MAX`. A mod with a
public home adds three more keys:

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
automated reads: *"All addons, plugins and similar works ... must be accompanied by the
source code (if applicable) and a license in both the post and the download file."*

**CKAN [doc]**, from comparable plugins' metadata: KSPCommunityFixes and HUDReplacer
depend on `Harmony2`, ZTheme depends on HUDReplacer, and the version file is read through
`$vref: '#/ckan/ksp-avc'`.

## What every build does

Every `dotnet build` generates `build/GameData/ReDefinition/ReDefinition.version` from the
project's version. It copies that file, the DLL and the repository's static `GameData`
files into the game. The static files are the profiles and the registrations.

The version file carries the three keys above. `URL` is the copy on the repository's
`main` branch, `DOWNLOAD` is the latest GitHub release, and `CHANGE_LOG_URL` is
`CHANGELOG.md` on `main`.

It also carries the KSP versions. `KSP_VERSION` is 1.12.5, the version ReDefinition is
built and tested against. `KSP_VERSION_MIN` is 1.12.0 and `KSP_VERSION_MAX` is 1.12.99,
because 1.12.0 and up run the same KSP interfaces this mod uses. That is the range EVE and
Kopernicus give.

The build writes the same version file into the repository root as well. The release
package refuses a working tree that differs from `HEAD`, so a version goes out only with
its copy committed.

## What the release build puts in the zip

The player package is built on request:

```
dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true
```

It is built without `DEVELOPMENT_BUILD`, so FSR's debug checks and its debug view pass are
compiled out. The developer builds keep them.

It assembles `build/release/ReDefinition_<version>.zip`:

```
GameData/ReDefinition/
    Plugins/ReDefinition.dll
    Plugins/ReDefinition.xml  the interface for mods, documented for an IDE
    Shaders/redefinition.shaders
    Profiles/ReDefinition-Profiles.cfg
    Mods/*.cfg                the registrations of the bundled mods
    ReDefinition.version
    LICENSE, EXCEPTIONS.md    this project's licence
    CHANGELOG.md              what each release changed
    LICENSE-FSR3Unity.txt     FSR3Unity, MIT, compiled into the DLL
    LICENSE-FidelityFX.txt    AMD's FSR 3 shaders, MIT, compiled into the bundle
    SOURCE.md                 where the source is
    CREDITS.md                docs/credits-and-licences.md
    README.md
    ReDefinitionProxy.ini     template for the proxy's settings, see below
dxgi.dll                      the proxy
dxgi_LICENSE-NVIDIA.txt       the MIT notices of NVIDIA's Streamline and NVAPI
                              headers compiled into dxgi.dll
dxgi_LICENSE-FidelityFX.txt   the MIT notice of AMD's FidelityFX API headers
                              compiled into dxgi.dll
amd_fidelityfx_framegeneration_dx12.dll
                              AMD's frame generation runtime, 4.0.1 from FSR SDK v2.3.0
amd_fidelityfx_framegeneration_dx12_LICENSE.md
                              AMD's licence for it, verbatim
```

The zip's layout is the KSP folder's, so extracting it there merges `GameData` and puts
the rest next to `KSP_x64.exe` ([player/installing.md](../player/installing.md)).

Next to the zip goes `ReDefinition_<version>_source.zip`, the complete source of the
commit it was built from, made with `git archive`. GPL-3.0 section 6(d) asks for the
source to be offered in the same place as the binary.

## When the release build refuses

In these cases there is no zip, and one left from an earlier build is removed:

* the proxy is missing, or older than any native source;
* AMD's runtime is missing, or does not match its pinned SHA-256 in `third_party/amd`,
  which `python tools/fetch_amd_runtime.py` fetches;
* the shader bundle is missing;
* any shader source is newer than the bundle. The Unity project builds the bundle
  directly into the game's `GameData` (`BundleBuilder.cs`), and the package takes it from
  there;
* the working tree differs from `HEAD`, whose source would not match the binary.

## What is not in the zip

* The player's `PluginData/settings.cfg` and `bundled.cfg`. They are the player's.
* A `ReDefinitionProxy.ini` in the game folder. Without one the proxy runs on its
  defaults, frame generation included. One there holds the player's settings, which an
  update must not overwrite, and CKAN would refuse to install over it. The template in
  `GameData/ReDefinition` says how to use it.
* **NVIDIA's DLLs.** These are `nvngx_dlss.dll` for DLSS, and `sl.interposer.dll`,
  `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll` and `nvngx_dlssg.dll`
  for DLSS frame generation. The player downloads them from NVIDIA's release of the
  Streamline SDK 2.14.1 on GitHub, with *NVIDIA DLSS files* in the settings window, after
  accepting NVIDIA's licences there (`NvidiaFiles`, `NvidiaDownloader`). Every file's
  SHA-256 is pinned.
* **AMD's upscaler DLL**, `amd_fidelityfx_upscaler_dx12.dll`. The player brings their own
  copy.

## The files for the game folder

* **AMD's licence [src]**, from `Kits/FidelityFX/docs/license.md` of the release, verbatim
  in `licenses/AMD-FidelityFX-SDK-license.md`. It allows redistribution "in binary form
  only", reproducing AMD's copyright notice, the permission notice and the disclaimers,
  and forbids reverse engineering, decompilation and disassembly. The package puts the
  licence next to the DLL.
* **AMD's runtime [src]** comes from FSR SDK v2.3.0 of 2026-06-24. Its frame generation
  header says 4.0.1, the version the proxy is built against. The package takes the
  release's `signedbin` DLL, SHA-256 `02297bee...`. Only this one AMD DLL is needed for
  FSR frame generation: the proxy harness passes with nothing else beside it. The pin, the
  source and the signature are in `third_party/amd/README.md`.
* **The headers' notices [src]:** `licenses/NVIDIA-MIT.txt` holds, verbatim, the MIT
  notices of the Streamline 2.14.1 headers in `src/DxgiProxy/extern/Streamline/include`,
  the signature check in `sl_security.h` among them. It also holds the notice of the NVAPI
  headers whose function ids and structures `NvidiaGpu.cpp` declares.
  `licenses/AMD-FidelityFX-API-MIT.txt` holds the MIT notice of AMD's FidelityFX API
  headers in `src/DxgiProxy/extern/FidelityFX`, the loader `ffx_api_loader.h` among them.
  The package puts both next to `dxgi.dll`.
* **CKAN [src]:** its spec allows `install_to: GameRoot` for KSP 1, which it says "should
  be used sparingly, if at all". Advanced Fly-By-Wire's NetKAN installs `SDL2.dll` and
  `XInputInterface.dll` with their licences there. CKAN never overwrites a file it did not
  install (`Core/IO/ModuleInstaller.cs`: "We don't allow for the overwriting of files.").
  A player who already has another tool's `dxgi.dll`, ReShade's for instance, or an AMD
  runtime from OptiScaler next to `KSP_x64.exe`, has to move it away first. That is also
  why the ini is not installed there. `license` takes a list, and for mixed assets the
  spec asks for the most restrictive of the licences the package carries. AMD's allows
  redistribution in binary form only,
  which is CKAN's `unrestricted`.
* **CurseForge [src]:** its app is dropping KSP. Its App Release Notes 1.277 say
  "CurseForge App support for KSP is going to be deprecated. Existing KSP mods will remain
  accessible on the website." Players download the zip there and extract it by hand, which
  the layout above is made for. Its moderation policy asks for a licence or permission,
  with credit, for third-party content. AMD's licence and notices, and NVIDIA's notices,
  are in the zip.

Sources: https://github.com/KSP-CKAN/CKAN/blob/master/Spec.md,
https://github.com/KSP-CKAN/NetKAN/blob/master/NetKAN/AdvancedFlyByWire.netkan,
https://github.com/KSP-CKAN/CKAN/blob/master/Core/IO/ModuleInstaller.cs,
https://blog.curseforge.com/app-release-notes-1-277/,
https://support.curseforge.com/support/solutions/articles/9000197279-moderation-policies

## The CKAN metadata

The file is `NetKAN/ReDefinition.netkan` in https://github.com/KSP-CKAN/NetKAN. Every
identifier was checked against the NetKAN repository, and every `file` against the release
zip.

| Key | What it does |
|---|---|
| `$kref` | takes the GitHub release; `asset_match` picks the player zip, not `ReDefinition_<version>_source.zip` beside it |
| `$vref` | reads `ReDefinition.version` in the zip for the KSP versions |
| `x_netkan_trust_version_file` | takes the mod's version from that file too, so it reads the project's version and not the tag's `v` in front of it |
| `license` | `GPL-3.0` for ReDefinition, `unrestricted` for AMD's frame generation runtime |
| `depends` | `Harmony2`, which ReDefinition requires to load |
| `install` | `GameData/ReDefinition` into `GameData`; `dxgi.dll`, AMD's runtime and the licences into `GameRoot` |

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
  - file: GameData/ReDefinition
    install_to: GameData
  - file: dxgi.dll
    install_to: GameRoot
  - file: dxgi_LICENSE-NVIDIA.txt
    install_to: GameRoot
  - file: dxgi_LICENSE-FidelityFX.txt
    install_to: GameRoot
  - file: amd_fidelityfx_framegeneration_dx12.dll
    install_to: GameRoot
  - file: amd_fidelityfx_framegeneration_dx12_LICENSE.md
    install_to: GameRoot
```
