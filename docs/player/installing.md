# Installing ReDefinition

## What you need

| | |
|---|---|
| Kerbal Space Program | 1.12, built and tested on 1.12.5 |
| Harmony | **required**: HarmonyKSP (on CKAN: *Harmony2*), in `GameData/000_Harmony`. KSP does not load ReDefinition without it |
| Operating system | Windows |
| For DLSS, AMD's upscaler DLL and frame generation | ReDefinition's `dxgi.dll` next to `KSP_x64.exe`, from the release zip, and a GPU with Direct3D 12 |

FSR 3 and the settings window need nothing beyond KSP and Harmony. The graphics
mods ReDefinition works with -- Scatterer, EVE, Parallax, Deferred, TUFX, Firefly,
Waterfall, Distant Object -- are optional: a mod that is not installed has no rows.

## Installing the release zip

Extract the zip into the KSP folder, the one with `KSP_x64.exe`:

```
GameData/ReDefinition/                          merges into your GameData
dxgi.dll                                        ReDefinition's proxy: DLSS, AMD's upscaler DLL, frame generation
dxgi_LICENSE-NVIDIA.txt                         the licences of NVIDIA's Streamline and NVAPI headers the proxy is built with
dxgi_LICENSE-FidelityFX.txt                     the licence of AMD's FidelityFX API headers the proxy is built with
amd_fidelityfx_framegeneration_dx12.dll         AMD's frame generation runtime
amd_fidelityfx_framegeneration_dx12_LICENSE.md  AMD's licence for it
```

KSP has no `dxgi.dll` of its own. Windows loads the one next to `KSP_x64.exe` before its
own, and ReDefinition's passes every call on to Windows' `dxgi.dll`.

**Another `dxgi.dll`** -- ReShade's, for example -- cannot sit next to ReDefinition's:
Windows loads the proxy because of that file name. Move the other one away first.

## Files from NVIDIA and AMD

DLSS, DLSS frame generation and AMD's newer upscaler run from DLLs of NVIDIA and
AMD, next to `KSP_x64.exe` or in the folders the proxy's ini names. ReDefinition
downloads NVIDIA's; AMD's upscaler DLL is copied from a game that has it.

| For | Files | Where from |
|---|---|---|
| DLSS | `nvngx_dlss.dll` | *NVIDIA DLSS files* in the settings window |
| DLSS frame generation | `sl.interposer.dll`, `sl.common.dll`, `sl.dlss_g.dll`, `sl.reflex.dll`, `sl.pcl.dll`, `nvngx_dlssg.dll` -- NVIDIA Streamline 2.14.1 | *NVIDIA DLSS files* in the settings window |
| AMD FSR (DLL), FSR 4 where the GPU has it | `amd_fidelityfx_upscaler_dx12.dll` | a game that has it |

**NVIDIA DLSS files**, under *General* in the settings window, is shown on an NVIDIA
GPU that can use DLSS (RTX 20 and newer) or DLSS frame generation (RTX 40 and newer)
while its files are missing. It names the files, their size and NVIDIA's licences,
and downloads them from NVIDIA's release of the Streamline SDK 2.14.1 on GitHub once
the licences are accepted. Every file is checked against that release before any is
placed, and all are placed or none. A different file already there is renamed to end
in `.old`. DLSS uses `nvngx_dlss.dll` at once; DLSS frame generation starts with the
next start of KSP. The same files copied by hand -- `nvngx_dlss.dll` from a game, the
Streamline DLLs from `bin\x64` of NVIDIA's Streamline SDK 2.14.1 -- work as well.

DLSS frame generation also needs Windows 10 20H1 or newer and *Hardware-accelerated
GPU scheduling* switched on in Windows' graphics settings.

## The proxy's settings

The proxy runs on its defaults without any file. To change them, copy
`GameData/ReDefinition/ReDefinitionProxy.ini` next to `KSP_x64.exe` and edit it there;
each setting is described in the file, and
[upscaler-and-frame-generation.md](upscaler-and-frame-generation.md) lists the ones a
player changes. An update does not overwrite that copy.

**If the game does not start** with the proxy, set `enabled=0` in that file: the proxy
then passes everything through to Windows' own `dxgi.dll`. The proxy writes
`ReDefinitionProxy.log` next to the executable, with the reason where it knows one.

## Where ReDefinition keeps its own files

| File | Holds |
|---|---|
| `GameData/ReDefinition/PluginData/settings.cfg` | the upscaler's and frame generation's settings |
| `GameData/ReDefinition/PluginData/bundled.cfg` | what the settings window keeps for the other mods: the chosen profile, values set at every start, choices for every save, and each setting's value from before ReDefinition first changed it |

An update does not touch them.

## Taking ReDefinition out again

A setting changed in ReDefinition's window is saved in that mod's own files, as the
mod's own window saves it, and stays when ReDefinition goes. To put the mods back as
they were before ReDefinition:

1. Open the settings window, *Mods and toolbar*, and press *Restore settings from
   before ReDefinition*. Mods that keep settings per save (TUFX, Distant Object) get
   theirs back in each save the next time it loads: load the saves you play once.
2. Quit KSP and remove `GameData/ReDefinition`, `dxgi.dll` with
   `dxgi_LICENSE-NVIDIA.txt` and `dxgi_LICENSE-FidelityFX.txt`,
   `amd_fidelityfx_framegeneration_dx12.dll` with its
   licence, and `ReDefinitionProxy.ini` and `ReDefinitionProxy.log` where they are.
   NVIDIA's and AMD's DLLs listed above can stay or go; without the proxy nothing
   loads them.

Deferred's and Waterfall's settings are set by ReDefinition at every start only;
their own configs apply again once ReDefinition is gone.
