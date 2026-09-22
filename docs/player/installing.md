# Installing ReDefinition

**For:** players.
**You need:** KSP 1.12 on Windows, and HarmonyKSP in `GameData/000_Harmony`. KSP does
not load ReDefinition without Harmony.
**You get:** ReDefinition installed, NVIDIA's and AMD's files in place where you want
them, and a clean way out again.

FSR 3 and the settings window need nothing beyond KSP and Harmony. The graphics mods
ReDefinition works with are all optional. A mod that is not installed has no rows.

## Install from the release zip

**1. Install HarmonyKSP** from
https://github.com/KSPModdingLibs/HarmonyKSP/releases, extracted into `GameData`. KSP
does not load ReDefinition without it.

**2. Extract ReDefinition's zip** into the KSP folder, the one with `KSP_x64.exe`:

```
GameData/ReDefinition/                          merges into your GameData
dxgi.dll                                        the proxy: DLSS, AMD's upscaler DLL, frame generation
dxgi_LICENSE-NVIDIA.txt                         licences of the NVIDIA headers the proxy is built with
dxgi_LICENSE-FidelityFX.txt                     licence of the AMD headers the proxy is built with
amd_fidelityfx_framegeneration_dx12.dll         AMD's frame generation runtime
amd_fidelityfx_framegeneration_dx12_LICENSE.md  AMD's licence for it
```

Windows loads the `dxgi.dll` next to `KSP_x64.exe` instead of its own, and
ReDefinition's file hands on to Windows everything it does not need itself.

Only one `dxgi.dll` can sit there. If you already have another one, ReShade's for
example, move it away first.

**3. Start KSP.** ReDefinition is in when its button is in the toolbar of the main
menu.

**4. Choose a graphics profile.** Until you do, ReDefinition changes nothing: no
upscaler, no frame generation, and every mod keeps its own settings. The main menu
offers *Use High* at the first start, and the *Graphics* tab has all five
([graphics-profiles.md](graphics-profiles.md)).

## Install with CKAN

Search for *ReDefinition* in CKAN and install it. If CKAN does not list it, its entry
there is not accepted yet, and the release zip above is the way in. CKAN
installs Harmony with it, and puts `dxgi.dll` and AMD's runtime next to `KSP_x64.exe`.
CKAN does not replace a `dxgi.dll` it did not install, so move another one away first.

## Get the files for DLSS and AMD's upscaler

DLSS, DLSS frame generation and AMD's newer upscaler run from DLLs by NVIDIA and AMD.
They go next to `KSP_x64.exe`, or into the folders the proxy's ini names.

| For | Files | Where from |
|---|---|---|
| DLSS | `nvngx_dlss.dll` | *Download ...* beside *NVIDIA DLSS files*, under *Display* |
| DLSS frame generation | `sl.interposer.dll`, `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll`, `nvngx_dlssg.dll`, from NVIDIA Streamline 2.14.1 | *Download ...* beside *NVIDIA DLSS files*, under *Display* |
| AMD FSR as a DLL, FSR 4 where the GPU has it | `amd_fidelityfx_upscaler_dx12.dll` | a game that ships it |

*NVIDIA DLSS files* is under *Display* in the settings window. It is shown on an
NVIDIA GPU that can use DLSS, which is RTX 20 and newer, or DLSS frame generation,
which is RTX 40 and newer. It is there while those files are missing, and until you
have read how the download went.

The row names the files, their size and NVIDIA's licences. Its *Download ...* button
fetches them from NVIDIA's release of the Streamline SDK 2.14.1 on GitHub, once you
accept the licences. NVIDIA's licence texts are placed beside the DLLs.

Every file is checked against that release before any is placed, and either all are
placed or none. A different file already there is renamed to end in `.old`. DLSS uses
`nvngx_dlss.dll` at once. DLSS frame generation starts with the next start of KSP.

You can also copy the same files by hand: `nvngx_dlss.dll` from a game that has it, and
the Streamline DLLs from `bin\x64` of NVIDIA's Streamline SDK 2.14.1.

DLSS frame generation also needs Windows 10 20H1 or newer, with *Hardware-accelerated
GPU scheduling* switched on in Windows' graphics settings.

## Change the proxy's settings

The proxy runs on its defaults without any file. To change them, copy
`GameData/ReDefinition/ReDefinitionProxy.ini` next to `KSP_x64.exe` and edit it there.
Each setting is described in the file, and
[upscaler-and-frame-generation.md](upscaler-and-frame-generation.md) lists the ones a
player changes. An update does not overwrite that copy.

**If the game does not start** with the proxy, set `enabled=0` in that file and start
KSP again. The proxy
then passes everything through to Windows' own `dxgi.dll`. It writes
`ReDefinitionProxy.log` next to the executable, with the reason where it knows one.

## Where ReDefinition keeps its own files

| File | Holds |
|---|---|
| `GameData/ReDefinition/PluginData/settings.cfg` | the upscaler's and frame generation's settings, and the four hotkeys |
| `GameData/ReDefinition/PluginData/bundled.cfg` | the chosen profile, the values ReDefinition keeps for the other mods, and each setting's value from before ReDefinition first changed it |

An update does not touch them.

## Take ReDefinition out again

A setting you changed in ReDefinition's window is saved in that mod's own files, the
way that mod's own window saves it. It stays when ReDefinition goes. To put the mods
back as they were before:

1. Open the settings window, go to *Mods / Toolbar*, and press *Restore settings from
   before ReDefinition*.
2. Load each save you play once. Mods that keep settings per save, TUFX and Distant
   Object, get theirs back as that save loads.
3. Quit KSP. Remove `GameData/ReDefinition`, `dxgi.dll` with its two licence files,
   `amd_fidelityfx_framegeneration_dx12.dll` with its licence, and
   `ReDefinitionProxy.ini` and `ReDefinitionProxy.log` where they are.

NVIDIA's and AMD's DLLs can stay or go, with the licence texts beside them. Without the
proxy, nothing loads them.

Deferred's and Waterfall's settings are set by ReDefinition at every start. Their own
configs apply again once ReDefinition is gone.
