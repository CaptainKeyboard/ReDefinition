# Packaging -- how the mod arrives in GameData

How ReDefinition is packaged for players. Marks: **[src]** read from source or from the
installed files, **[doc]** a project's own statement, **[open]** not settled.

---

## 1. How comparable mods ship

**What installed mods ship [src]**, from their folders in this installation's
`GameData`:

| Mod | In its folder |
|---|---|
| TUFX | `Plugins/`, `Shaders/`, `Profiles/`, `TUFX.cfg`, `TUFX.version`, a changelog |
| KSPCommunityFixes | `Plugins/`, `KSPCommunityFixes.version`, `README.md`, `CHANGELOG.md`, `Settings.cfg`, `Localization/`, `MMPatches/` |
| HUDReplacer | the DLL, `HUDReplacer.version`, `LICENSE`, `README.md`, `Settings.cfg` |
| ClickThroughBlocker | `Plugins/`, `ClickThroughBlocker.version`, `LICENSE.md`, `README.md`, a changelog |
| Trajectories | `Plugins/`, `Trajectories.version`, `LICENSE.md`, `COPYRIGHTS.md`, `README.md`, `CHANGELOG.md` |

**The KSP-AVC version file [src]**, from TUFX's and KSPCommunityFixes' own:
JSON with `NAME`, `VERSION`, `KSP_VERSION`, `KSP_VERSION_MIN`,
`KSP_VERSION_MAX`, and -- where the mod has a public home -- `URL` (the remote
copy of the file, for the update check), `DOWNLOAD` and `CHANGE_LOG_URL`.

**How KSPCommunityFixes builds its package [src]** (`KSPCommunityFixes.csproj`):
static files in a `GameData/KSPCommunityFixes` folder of the repository, the
version file generated from the project's version (KSPBuildTools), a target
that copies everything into the game after each build, and on Release a zip of
`GameData/<Mod>` with README and changelog inside.

**The forum's add-on posting rules [doc]** (via a search result; the forum
refuses automated reads): *"All addons, plugins and similar works ... must be
accompanied by the source code (if applicable) and a license in both the post
and the download file."*

**CKAN [doc]**, from comparable plugins' metadata: KSPCommunityFixes and
HUDReplacer depend on `Harmony2`; ZTheme depends on HUDReplacer; the version
file is read through `$vref: '#/ckan/ksp-avc'`.

## 2. What the build does

* **Every build with `dotnet build`** generates
  `build/GameData/ReDefinition/ReDefinition.version` from the project's version
  and copies it, the DLL and the repository's static `GameData` files (the
  profiles and the registrations) into the game. The KSP range is 1.12.5 to 1.12.5.
  The version file carries `URL` -- its copy on the repository's `main` branch, which
  KSP-AVC compares with the installed one -- `DOWNLOAD` (the latest GitHub release) and
  `CHANGE_LOG_URL` (`CHANGELOG.md` on `main`). The build writes that copy into the
  repository root as well; the release package refuses a working tree that differs
  from `HEAD`, so a version goes out only with its copy committed.
* **The player package is built on request:**

  ```
  dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true
  ```

  It is built without `DEVELOPMENT_BUILD`, so FSR's debug checks and its debug
  view pass are compiled out; the developer builds keep them. It assembles
  `build/release/ReDefinition_<version>.zip`:

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

  The files at the top level go next to `KSP_x64.exe`: extracting the zip into the KSP
  folder puts them there and merges `GameData`.

  Next to it goes `ReDefinition_<version>_source.zip`, the complete source of the
  commit it was built from (`git archive`), since GPL-3.0 section 6(d) asks for the
  source to be offered in the same place as the binary.

  It refuses -- no zip, and one left from an earlier build removed -- when the
  proxy is missing or older than any native source, when AMD's runtime is
  missing or does not match its pinned SHA-256 (`third_party/amd/`, fetched by
  `python tools/fetch_amd_runtime.py`), when the shader bundle is missing, when any
  shader source is newer than the bundle (the Unity project builds the bundle
  directly into the game's `GameData`, `BundleBuilder.cs`, and it is taken from
  there), or when the working tree differs from `HEAD`, whose source would not match
  the binary.

**Not in the zip:**

* The player's `PluginData/settings.cfg` and `bundled.cfg` -- they are the player's.
* A `ReDefinitionProxy.ini` in the game folder. Without one the proxy runs on
  its defaults, frame generation included; one there holds the player's
  settings, which an update must not overwrite, and CKAN would refuse to
  install over it. The template in `GameData/ReDefinition` says how to use it.
* **NVIDIA's DLLs** -- `nvngx_dlss.dll` for DLSS, and Streamline's `sl.interposer.dll`,
  `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll` and `nvngx_dlssg.dll`
  for DLSS frame generation. The player downloads them from NVIDIA's release of the
  Streamline SDK 2.14.1 on GitHub with *NVIDIA DLSS files* in the settings window, after
  accepting NVIDIA's licences there (`NvidiaFiles`, `NvidiaDownloader`); every file's
  SHA-256 is pinned.
* **AMD's upscaler DLL**, `amd_fidelityfx_upscaler_dx12.dll` -- the player's own copy.

## 3. The files for the game folder

* **AMD's licence [src]** (`Kits/FidelityFX/docs/license.md` of the release,
  verbatim in `licenses/AMD-FidelityFX-SDK-license.md`): redistribution "in
  binary form only", reproducing AMD's copyright notice, the permission notice
  and the disclaimers; no reverse engineering, decompilation or disassembly.
  The package puts the licence next to the DLL.
* **AMD's runtime [src]:** from FSR SDK v2.3.0 of 2026-06-24, whose frame generation
  header says 4.0.1, the version the proxy is built against; the
  package takes its `signedbin` DLL (git blob `1a06b727...`). Only this one AMD DLL is
  needed for FSR frame generation: the proxy harness passes with nothing else beside
  it. Pin, source and signature: `third_party/amd/README.md`.
* **The headers' notices [src]:** `licenses/NVIDIA-MIT.txt` holds, verbatim, the MIT
  notices of the Streamline 2.14.1 headers (`src/DxgiProxy/extern/Streamline/include`, the
  signature check in `sl_security.h` among them) and of the NVAPI headers whose function
  ids and structures `NvidiaGpu.cpp` declares; `licenses/AMD-FidelityFX-API-MIT.txt` the
  MIT notice of AMD's FidelityFX API headers (`src/DxgiProxy/extern/FidelityFX`, the loader
  `ffx_api_loader.h` among them). The package puts both next to `dxgi.dll`.
* **CKAN [src]:** its spec allows `install_to: GameRoot` for KSP 1 ("should be
  used sparingly, if at all"), and Advanced Fly-By-Wire's NetKAN installs
  `SDL2.dll` and `XInputInterface.dll` with their licences there. CKAN never
  overwrites a file it did not install (`Core/IO/ModuleInstaller.cs`: "We
  don't allow for the overwriting of files."): a player with another tool's
  `dxgi.dll` -- ReShade's -- or an AMD runtime from OptiScaler next to
  `KSP_x64.exe` has to move it away first. That is also why the ini is not
  installed there. `license` takes a list; for mixed assets the spec asks for
  the most restrictive, and AMD's -- redistributable, binary only -- is its
  `unrestricted`.
* **CurseForge [src]:** its app is dropping KSP ("CurseForge App support for
  KSP is going to be deprecated. Existing KSP mods will remain accessible on
  the website.", App Release Notes 1.277); players download the zip there and
  extract it by hand, which the layout above is made for. Its moderation
  policy asks for a licence or permission, with credit, for third-party
  content -- AMD's licence and notices, and NVIDIA's notices, in the zip.

Sources: https://github.com/KSP-CKAN/CKAN/blob/master/Spec.md,
https://github.com/KSP-CKAN/NetKAN/blob/master/NetKAN/AdvancedFlyByWire.netkan,
https://github.com/KSP-CKAN/CKAN/blob/master/Core/IO/ModuleInstaller.cs,
https://blog.curseforge.com/app-release-notes-1-277/,
https://support.curseforge.com/support/solutions/articles/9000197279-moderation-policies

## 4. CKAN metadata

`NetKAN/ReDefinition.netkan` in https://github.com/KSP-CKAN/NetKAN. Every identifier was
checked against the NetKAN repository, and every `file` against the release zip.

* **`$kref`** takes the GitHub release; `asset_match` picks the player zip, not
  `ReDefinition_<version>_source.zip` beside it.
* **`$vref`** reads `ReDefinition.version` in the zip for the KSP versions.
  `x_netkan_trust_version_file` takes the mod's version from there as well, so it reads
  `0.1.0` and not the tag's `v0.1.0`.
* **`license`:** `GPL-3.0` for ReDefinition, `unrestricted` for AMD's frame generation
  runtime (section 3). CKAN has no identifier for the Modding and Linking Exceptions;
  they are in `LICENSE` and `EXCEPTIONS.md` in the zip.
* **`depends`:** `Harmony2`, which ReDefinition requires to load.
* **`install`:** `GameData/ReDefinition` into `GameData`; `dxgi.dll`, AMD's runtime and
  the licences into `GameRoot` (section 3).

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
