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

DLSS, XeSS, frame generation other than FSR 3 and AMD's newer upscaler run from DLLs by
NVIDIA, Intel and AMD. They go next to `KSP_x64.exe`, or into the folders the proxy's ini
names ([reference/proxy-settings.md](../reference/proxy-settings.md#dll-folders)).

| For | Files | Where from |
|---|---|---|
| DLSS | `nvngx_dlss.dll` | *Download ...* beside *NVIDIA DLSS files*, under *Display* |
| DLSS Ray Reconstruction, with raytracing | `sl.interposer.dll`, `sl.common.dll`, `sl.pcl.dll`, `sl.dlss_d.dll`, `nvngx_dlssd.dll`, from NVIDIA Streamline 2.14.16 | *Download ...* beside *NVIDIA DLSS files*, under *Display* |
| DLSS frame generation | the files above, and `sl.dlss_g.dll`, `sl.reflex.dll`, `nvngx_dlssg.dll` | the same download, on RTX 40 and newer |
| DLSS frame generation on RTX 30 and RTX 20 | the Streamline files above, and `version.dll`, `dlssg_sm86.ini` and the `dlssg_sm86` folder of dlssg_for_sm86 | the same download, and *Download manually ...* beside *DLSS-G on RTX 30/20*; the steps are in [upscaler-and-frame-generation.md](upscaler-and-frame-generation.md#dlss-frame-generation-on-rtx-30-and-rtx-20) |
| AMD FSR as a DLL, FSR 4 where the GPU has it | `amd_fidelityfx_upscaler_dx12.dll` | a game that ships it |
| XeSS | `libxess.dll` | *Download ...* beside *Intel XeSS files*, under *Display*, or Intel's XeSS SDK by hand |
| XeSS frame generation | `libxess_fg.dll` and `libxell.dll`, from XeSS SDK 2.1 or newer | the same download; what it needs is in [upscaler-and-frame-generation.md](upscaler-and-frame-generation.md#xess-frame-generation) |

*NVIDIA DLSS files* is under *Display* in the settings window. It is shown on an
NVIDIA GPU that can use DLSS, which is RTX 20 and newer, or DLSS frame generation,
which is RTX 40 and newer. It is there while those files are missing, and until you
have read how the download went.

The row names the files, their size and NVIDIA's licences. Its *Download ...* button
fetches them from NVIDIA's release of the Streamline SDK 2.14.16 on GitHub, once you
accept the licences. NVIDIA's licence texts are placed beside the DLLs.

Every file is checked against that release before any is placed, and either all are
placed or none. A different file already there is renamed to end in `.old`. DLSS uses
`nvngx_dlss.dll` at once. DLSS frame generation starts with the next start of KSP.

The proxy loads Streamline 2.14.16 only. Streamline files of another version count as
missing, and the row offers the download again.

You can also copy the same files by hand: `nvngx_dlss.dll` from a game that has it, and
the Streamline DLLs from `bin\x64` of NVIDIA's Streamline SDK 2.14.16.

*Intel XeSS files* is under *Display* too. It is shown when the proxy runs XeSS, and it
is there while Intel's files are missing. Once they are in place it names them and their
versions, such as *libxess.dll 2.0.2.68, libxess_fg.dll 1.3.1.78, libxell.dll 1.3.2.10*.

The *Download ...* button fetches `libxess.dll`, `libxess_fg.dll` and `libxell.dll` from
Intel's release of the XeSS SDK 3.0.2 on GitHub, about 73 MB, of which `libxess.dll` is
60 MB. First a dialog names the files, their sizes and the folders they go to. It links
Intel's licence, the *Intel Simplified Software License (Version October 2022)*. If you
accept it, the download starts. The licence text is placed beside the DLLs as
`xess.license.txt`. If you need only one of the two uses, the dialog also has a button for
XeSS super resolution alone and one for XeSS frame generation alone.

The files go where the proxy loads them from: `libxess.dll` into the folder named by
`xessDirectory`, `libxess_fg.dll` and `libxell.dll` into the folder named by
`xessFgDirectory`, and next to `KSP_x64.exe` where those are empty. A file you already
have there stays and is not offered again. The exception is a `libxess_fg.dll` older than
1.2.2, which the proxy refuses. It is offered again, and the old one is renamed to end in
`.old`.

Every file is checked against Intel's release before any is placed, and either all are
placed or none. If the connection fails, the row says which file failed and nothing is
changed. XeSS super resolution uses `libxess.dll` at once. XeSS frame generation starts
with the next start of KSP.

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
