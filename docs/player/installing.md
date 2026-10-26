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
dxgi.dll                                        the proxy: DLSS, AMD's upscaler DLL, XeSS, frame generation
dxgi_LICENSE-NVIDIA.txt                         licences of the NVIDIA headers the proxy is built with
dxgi_LICENSE-FidelityFX.txt                     licence of the AMD headers the proxy is built with
dxgi_LICENSE-AntiLag2.txt                       licence of AMD's Anti-Lag 2 headers
dxgi_LICENSE-Capsaicin.txt                      licence of AMD's radiance cache code
dxgi_LICENSE-XeSS.txt                           licence of Intel's XeSS and XeLL headers
amd_fidelityfx_framegeneration_dx12.dll         AMD's frame generation runtime
amd_fidelityfx_framegeneration_dx12_LICENSE.md  AMD's licence for it
amd_fidelityfx_denoiser_dx12.dll                AMD's denoiser for raytracing, Radeon RX 9000 and newer
amd_fidelityfx_denoiser_dx12_LICENSE.md         AMD's licence for it
```

Windows loads the `dxgi.dll` next to `KSP_x64.exe` instead of its own, and
ReDefinition's file hands on to Windows everything it does not need itself.

Only one `dxgi.dll` can sit there. If you already have another one, ReShade's for
example, move it away first.

**3. Start KSP.** ReDefinition is in when the main menu has a *ReDefinition* entry
under *Settings*.

If Windows refused to load the proxy, the main menu says so once, under *ReDefinition's
dxgi.dll was blocked*, with the reason where Windows gives one. Smart App Control blocks
it, as it blocks every DLL without a trusted signature; it is switched off in Windows
Security, under *App & browser control*, *Smart App Control settings*. A code integrity
policy or a virus scanner can block it too, and Windows names a blocked DLL in its event
log, under *Applications and Services Logs*, *Microsoft*, *Windows*, *CodeIntegrity*,
*Operational*. Without the proxy, FSR 3 upscaling runs, and DLSS, AMD's upscaler, XeSS, frame
generation and raytracing are not there.

**4. Choose a graphics profile.** Until you do, ReDefinition changes nothing: no
upscaler, no frame generation, and every mod keeps its own settings. The main menu
offers *Let ReDefinition take control* at the first start, which chooses High, and
the *Graphics* tab has all five
([graphics-profiles.md](graphics-profiles.md)).

## Install with CKAN

Search for *ReDefinition* in CKAN and install it. If CKAN does not list it, its entry
there is not accepted yet, and the release zip above is the way in. CKAN
installs Harmony with it, and puts `dxgi.dll` and AMD's runtime next to `KSP_x64.exe`.
CKAN does not replace a `dxgi.dll` it did not install, so move another one away first.

## Get the files for DLSS, XeSS and AMD's upscaler

DLSS, XeSS, DLSS frame generation and AMD's FSR 4 run from DLLs by NVIDIA, Intel and AMD.
FSR 3 and its frame generation need none of them. The DLLs go next to `KSP_x64.exe`, or into
the folders the proxy's ini names
([reference/proxy-settings.md](../reference/proxy-settings.md#dll-folders)).

You do not fetch them first. Choose the technique under *Display*, in the *Upscaler* or the
*Frame generation* row. If its files are missing, a dialog opens before the choice takes
effect. If you cancel the dialog, the choice stays as it was.

| You choose | Files it needs | The dialog |
|---|---|---|
| *Upscaler*: *DLSS* | `nvngx_dlss.dll`, and for Ray Reconstruction `sl.interposer.dll`, `sl.common.dll`, `sl.pcl.dll`, `sl.dlss_d.dll`, `nvngx_dlssd.dll`, from NVIDIA Streamline 2.14.16 | NVIDIA's download |
| *Frame generation*: *DLSS* on RTX 40 and newer | the files above, and `sl.dlss_g.dll`, `sl.reflex.dll`, `nvngx_dlssg.dll` | the same download |
| *Frame generation*: *DLSS* on RTX 30 and RTX 20 | the files above, and `version.dll`, `dlssg_sm86.ini` and the `dlssg_sm86` folder of dlssg_for_sm86 | first the steps for the package ([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md#dlss-frame-generation-on-rtx-30-and-rtx-20)), then NVIDIA's download |
| *Upscaler*: *FSR 4* | `amd_fidelityfx_upscaler_dx12.dll`, which runs on a Radeon RX 7000 or RX 9000 | where to get it, from the signed binaries of AMD's FidelityFX SDK; you put it there and choose again |
| *Upscaler*: *XeSS* | `libxess.dll` | Intel's download |
| *Frame generation*: *XeSS* | `libxess_fg.dll` and `libxell.dll`, from XeSS SDK 2.1 or newer ([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md#xess-frame-generation)) | Intel's download |

NVIDIA's dialog names the files, their size and the folders they go to. It links NVIDIA's
licences. *Accept and download* fetches the files from NVIDIA's release of the Streamline SDK
2.14.16 on GitHub, and places NVIDIA's licence texts beside the DLLs.

Every file is checked against that release before any is placed, and either all are placed or
none. A different file already there is renamed to end in `.old`. DLSS uses `nvngx_dlss.dll`
at once. DLSS frame generation and Ray Reconstruction start with the next start of KSP.

The proxy loads Streamline 2.14.16 only. Streamline files of another version count as
missing, and the dialog offers the download again.

You can also copy the same files by hand: `nvngx_dlss.dll` from a game that has it, and
the Streamline DLLs from `bin\x64` of NVIDIA's Streamline SDK 2.14.16.

Intel's dialog fetches `libxess.dll`, `libxess_fg.dll` and `libxell.dll` from Intel's release
of the XeSS SDK 3.0.2 on GitHub, about 70 MB, of which `libxess.dll` is 57 MB. It names the
files, their sizes and the folders they go to. It links Intel's licence, the *Intel Simplified
Software License (Version October 2022)*, and the release. *Accept and download* fetches
the files, and places the licence text beside the DLLs as `xess.license.txt`. If you need
only one of the two uses, the dialog also has *Accept, super resolution only* and *Accept,
frame generation only*.

The files go where the proxy loads them from: `libxess.dll` into the folder named by
`xessDirectory`, `libxess_fg.dll` and `libxell.dll` into the folder named by
`xessFgDirectory`, and next to `KSP_x64.exe` where those are empty. A file you already
have there stays and is not offered again. The exception is a `libxess_fg.dll` older than
1.2.2, which the proxy refuses. It is offered again, and the old one is renamed to end in
`.old`.

Every file is checked against Intel's release before any is placed, and either all are
placed or none. If the connection fails, nothing is changed. XeSS super resolution uses
`libxess.dll` at once. XeSS frame generation starts with the next start of KSP.

The status of a download shows in grey under the *Upscaler* or *Frame generation* row, and
in a message at the top of the screen when it ends.

DLSS frame generation also needs Windows 10 20H1 or newer, with *Hardware-accelerated
GPU scheduling* switched on in Windows' graphics settings.

## Change the proxy's settings

At its first start, the proxy writes `ReDefinitionProxy.ini` next to `KSP_x64.exe`, with
every setting at its default. Edit it there. Each setting is described in the file, and
[reference/proxy-settings.md](../reference/proxy-settings.md) lists the ones a player or
a developer changes.

A newer version keeps your changes. At each start the proxy adds the settings that are
new since, with their descriptions. A value you never changed follows the new version's
default; a value you changed stays as you set it. To tell the two apart, the proxy notes
the values it wrote itself in `ReDefinitionProxy.defaults` beside the ini.

**If the game does not start** with the proxy, set `enabled=0` in that file and start
KSP again. The proxy
then passes everything through to Windows' own `dxgi.dll`. It writes
`ReDefinitionProxy.log` next to the executable, with the reason where it knows one.

## Where ReDefinition keeps its own files

| File | Holds |
|---|---|
| `GameData/ReDefinition/PluginData/settings.cfg` | ReDefinition's own settings: the upscaler, frame generation and raytracing, the four hotkeys, the flight switches, the main menu's and the space centre's rows, the window's pause, and the navball's place |
| `GameData/ReDefinition/PluginData/bundled.cfg` | the chosen profile, the values ReDefinition keeps for the other mods, and each setting's value from before ReDefinition first changed it |
| `GameData/ReDefinition/PluginData/bindings.cfg` | the key bindings ReDefinition keeps for mods that ask it for their keys |
| `GameData/ReDefinition/PluginData/flightpanel.cfg` | the flight panel's profiles, for every save |
| `GameData/ReDefinition/PluginData/gauge.cfg` | the round gauge's chosen figure |
| `GameData/ReDefinition/PluginData/planner.cfg` | the planner's plans, by the vessel's name |

An update does not touch them.

## Take ReDefinition out again

A setting you changed in ReDefinition's window is saved in that mod's own files, the
way that mod's own window saves it. It stays when ReDefinition goes. To put the mods
back as they were before:

1. Open the settings window, go to *Mods / Toolbar*, and press *Restore settings from
   before ReDefinition*.
2. Load each save you play once. Mods that keep settings per save, TUFX and Distant
   Object, get theirs back as that save loads.
3. Quit KSP. Remove `GameData/ReDefinition`, `dxgi.dll` with its five licence files,
   `amd_fidelityfx_framegeneration_dx12.dll` and `amd_fidelityfx_denoiser_dx12.dll`
   with their licences, and `ReDefinitionProxy.ini`, `ReDefinitionProxy.defaults` and
   `ReDefinitionProxy.log` next to `KSP_x64.exe`. `ReDefinitionCaptures` there holds
   what the diagnostics window recorded, where you recorded anything.

NVIDIA's, Intel's and AMD's DLLs can stay or go, with the licence texts beside them.
Without the proxy, nothing loads them. If you installed dlssg_for_sm86, delete its
`version.dll`, `dlssg_sm86.ini` and `dlssg_sm86` folder next to `KSP_x64.exe` while KSP is
closed.

Deferred's and Waterfall's settings are set by ReDefinition at every start. Their own
configs apply again once ReDefinition is gone.
