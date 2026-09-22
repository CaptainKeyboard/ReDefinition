# The upscaler and frame generation

**For:** players setting up upscaling or frame generation.
**You need:** ReDefinition installed, a graphics profile chosen, and for DLSS or AMD's
upscaler DLL the proxy and those files ([installing.md](installing.md)).
**You get:** what each setting does, which technique runs on your GPU, what it costs,
and where it falls short.

Both are active only while a graphics profile is chosen
([graphics-profiles.md](graphics-profiles.md)). Their settings are under *Graphics*,
*Upscaling* in the settings window, in the diagnostics window, and in KSP's own
settings dialog.

The diagnostics window's status line names what is running, and its frame rate line
shows the rendered rate with the presented rate in brackets.

## The upscaler

The upscaler renders KSP's 3D scene smaller and reconstructs the full-resolution image
from it. The interface is drawn on top at full resolution and stays sharp.

| Setting | Does |
|---|---|
| Upscaler | on or off |
| Technique | *FSR 3*, *DLSS* or *AMD FSR (DLL)*. If the chosen one cannot run, FSR 3 runs in its place, and the diagnostics window says why |
| Mode | *AA only*, at the right end, renders at full resolution and uses the upscaler as temporal antialiasing. This is what the profiles choose. Further left, the scene renders smaller by the factor shown and is reconstructed. DLSS renders at the size it asks for in each mode, and *1.2x*, which DLSS does not have, runs as DLSS's *Quality* |
| DLSS preset | which of NVIDIA's DLSS models runs. *Default* lets the DLSS library choose per mode; *J*, *K*, *L* and *M* where the library has them |
| Sharpness | how hard the image is sharpened after the upscaler, for every technique. 0 switches it off. Above 1.0 goes past what the sharpening was made for, and AMD's DLL stops at 1.0 |

The six modes, and how much smaller the scene renders in each:

| Mode | The scene renders at |
|---|---|
| *AA only* | the full resolution |
| *1.2x* | 1/1.2 of it in each direction |
| *1.5x* | 1/1.5 |
| *1.7x* | 1/1.7 |
| *2.0x* | half |
| *3.0x* | a third |

The transparency and reactive masks, which decide how transparent effects are treated,
are off at first. Both are on the diagnostics window's *Debug* tab.


### The three techniques

| Technique | Runs on | Needs |
|---|---|---|
| FSR 3 | every GPU | nothing: it is part of ReDefinition |
| DLSS | NVIDIA RTX 20 and newer | the proxy, and `nvngx_dlss.dll` |
| AMD FSR (DLL) | GPUs with Direct3D 12. FSR 4 where the DLL and the GPU have it, otherwise the FSR 3.1 in the DLL | the proxy, and `amd_fidelityfx_upscaler_dx12.dll` from a game that ships it |

DLSS and AMD's DLL run inside the proxy.

If the chosen technique cannot run, FSR 3 runs in its place and the diagnostics
window's *General* tab says why. The reason is one of these: no proxy, no DLL, a GPU
without it, or a failure while it ran. After a failure, a button there tries the
technique again.

### What ReDefinition changes for it

While a graphics profile is chosen, ReDefinition switches off what other mods bring for
antialiasing. A second temporal filter in front of the upscaler would average the image
twice:

* Scatterer's TAA and SMAA;
* TUFX's antialiasing;
* Deferred's SMAA in the editors;
* Kerbal Frame Generator's frame blend, where it is installed.

Choosing no profile gives each of them back what it had, unless it was changed since.

While the upscaler runs, ReDefinition also keeps EVE's clouds, kerbals and flags from
smearing, draws TUFX's bloom, colour grading, depth of field, motion blur and grain
after the upscaler so they stay crisp, holds KSP's MSAA off, forces anisotropic
filtering on, and keeps textures and level of detail at what they are at full
resolution.

Each of these has a switch on the diagnostics window's *Debug* tab, for finding out
which one causes a problem.

### What it costs

The smaller modes gain frame rate where the number of pixels is what limits it. Where
something else limits it, they gain nothing, and the upscaler is worth having as
antialiasing in *AA only*.

The measurements behind that, for anyone who wants the numbers:
[development/upscaler.md](../development/upscaler.md).

### Where the upscaler falls short

| Case | What you see |
|---|---|
| Vertex-animated geometry, such as Parallax's swaying grass | it has no motion vectors and can smear |
| Interface anchored in the world, in the smaller modes | it can sit slightly off |
| Transparent effects: EVE's clouds, Scatterer's ocean, plumes, re-entry | they are treated as solid unless the transparency and reactive masks on the *Debug* tab are switched on |
| Camera cuts a mod makes itself | the history resets when the camera's target, its parent or the IVA seat changes. Not detected: two cameras on one Hullcam part that have no transforms of their own, CameraTools switching its mode or vessel, and jumps inside a CameraTools mode |

## Frame generation

Frame generation puts generated frames between the rendered ones. Both techniques need
Direct3D 12, which KSP does not use: `dxgi.dll` next to `KSP_x64.exe` presents the game's
frames through a Direct3D 12 swapchain, and the game goes on rendering in Direct3D 11.
Without that file there is no frame generation.

| Frame generation | Runs on | Frames |
|---|---|---|
| DLSS | NVIDIA RTX 40 and newer, with NVIDIA's Streamline 2.14.1 DLLs and hardware-accelerated GPU scheduling on | as many per rendered frame as the GPU offers, within the V-Sync limit below |
| FSR 3 | GPUs with Direct3D 12, where DLSS frame generation does not run | one between every two rendered frames |

The *Frame generation* row shows which one runs: *FSR 3*, or *DLSS* with the multiplier
it reaches, up to *DLSS 6x*. It needs the
proxy. Without the proxy the row is greyed out, unless frame generation is on, so that
you can always switch it off.

With the upscaler off, frame generation runs on its own. The scene is still captured at
full resolution for it, without the upscaler's antialiasing.

It pays where the rendered frame rate is low: large vessels, many parts, re-entry.
Input responds at the rendered frame rate. AMD recommends at least 60 rendered frames a
second for FSR 3 frame generation. Below that, fast camera turns show artefacts.

The diagnostics window's frame rate line shows the frames rendered and, in brackets,
the frames presented with the generated ones.

### V-Sync with frame generation

Turn V-Sync on in KSP's settings while frame generation runs. This holds on a fixed
refresh rate monitor and with G-Sync or FreeSync alike. Without V-Sync, more frames can
reach the monitor than it shows, and they tear or drop unevenly. That happens with
G-Sync and FreeSync too, once the frame rate is above the monitor's range.

With DLSS frame generation, V-Sync presents every refresh at most. *Every second
refresh* is therefore not offered in ReDefinition's window while it runs, and a value
set elsewhere is corrected. The number of generated frames is limited so the monitor
can show them: the refresh rate divided by 15 gives the multiplier, so 60 Hz reaches 4x
and 75 Hz reaches 5x. A DLSS frame generation build without
V-Sync support presents without V-Sync.

With FSR 3 frame generation, V-Sync holds the game at half the refresh rate, and every
rendered and generated frame is shown for one refresh.

## The proxy's settings

Copy `GameData/ReDefinition/ReDefinitionProxy.ini` next to `KSP_x64.exe` and edit it
there. The file says which settings are read at the start and which are read again
within a second of it being saved.

| Setting | Default | Does |
|---|---|---|
| `enabled` | 1 | 0 passes everything through to Windows' own `dxgi.dll`. Read at the start |
| `frameGeneration` | 1 | 0: no frame generation, the proxy presents only. Read at the start |
| `dlssFrameGeneration` | 1 | 0: FSR 3 frame generation even where DLSS frame generation runs. Read at the start |
| `fgVSync` | 0 | 1: while FSR 3 frame generation is on, the game presents with V-Sync whatever KSP's setting says |
| `fgHalfRefreshLimit` | 0 | 1: with FSR 3 frame generation and without V-Sync, the rendered frame rate is held just below half the refresh rate |
| `fgAsyncWorkloads` | 0 | 1: frame generation runs alongside the game's rendering instead of after it. Worth a try where it costs more frame rate than it gives |
| `dlssDirectory`, `amdUpscalerDirectory` | empty | the folders of NVIDIA's and AMD's DLLs, where they are not next to `KSP_x64.exe` |
| `streamlineDirectory` | empty | the folder of NVIDIA's Streamline DLLs. Read when KSP makes its swapchain, so a change needs a restart |
| `reportSeconds` | 10 | how often frame times go into `ReDefinitionProxy.log` |

The remaining settings in the file are for development. Each one is described in the
file, beside its value.
