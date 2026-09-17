# The upscaler and frame generation

Both are active only while a graphics profile is chosen
([graphics-profiles.md](graphics-profiles.md)). Their settings are under *General* in
the settings window, in the diagnostics window and in KSP's own settings dialog.

## The upscaler

The upscaler reconstructs KSP's 3D scene from the engine's own colour, depth and
motion vectors, with a sub-pixel jitter on the cameras. The UI is drawn on top at
full resolution.

| Setting | Does |
|---|---|
| **Upscaler** | on or off |
| **Technique** | *FSR 3*, *DLSS* or *AMD FSR (DLL)*, see below |
| **Mode** | *AA only* at the right end renders at full resolution and uses the upscaler as temporal antialiasing -- the profiles' choice. Further left, the scene renders smaller by the factor shown and is reconstructed. DLSS renders at the size it asks for in each mode; *1.3x*, which DLSS does not have, runs as DLSS's *Quality* |
| **DLSS preset** | which of NVIDIA's DLSS models runs: *Default* lets the DLSS library choose per mode; *J*, *K*, *L*, *M* where the library has them |
| **Sharpness** | RCAS sharpening after the upscaler, for every technique; 0 switches it off. 1.0 is FidelityFX's maximum, the slider goes to 2.0. AMD's DLL stops at 1.0 |

### Techniques

| Technique | Runs on | Needs |
|---|---|---|
| **FSR 3** | every GPU | nothing: it is part of ReDefinition |
| **DLSS** | NVIDIA RTX 20 and newer | the proxy, and `nvngx_dlss.dll`, downloaded with *NVIDIA DLSS files* ([installing.md](installing.md)) |
| **AMD FSR (DLL)** | GPUs with Direct3D 12; FSR 4 where the DLL and the GPU have it, otherwise the FSR 3.1 in the DLL | the proxy, and `amd_fidelityfx_upscaler_dx12.dll` from a game that has it |

DLSS and AMD's DLL run inside the proxy: DLSS on Unity's Direct3D 11 device, AMD's
DLL on the proxy's Direct3D 12 device. Where the chosen technique cannot run -- no
proxy, no DLL, a GPU without it, or a failure while it runs -- FSR 3 runs in its
place, and the diagnostics window's *General* tab says why; after a failure while it
ran, a button there tries the technique again.

### What runs with it

While a graphics profile is chosen, ReDefinition switches off what other mods bring
for antialiasing, since a second temporal filter in front of the upscaler averages
the image twice:

* Scatterer's TAA and SMAA, and what its TAA leaves behind: its replacement motion
  vector shader, two shader globals, and vessel renderers set to draw no motion;
* TUFX's antialiasing mode, on its profiles and on its live layers;
* Deferred's SMAA in the editors;
* Kerbal Frame Generator's frame blend, where it is installed.

Choosing no profile gives each of them back what it had, unless it was changed since.

While the upscaler runs, ReDefinition also:

* draws TUFX's bloom, colour grading and tonemapping, depth of field, motion blur,
  lens distortion, chromatic aberration, vignette and grain after the upscaler, and
  its ambient occlusion, screen-space reflections, fog and auto exposure before it
  -- where the scene renders in HDR, as it does in flight;
* gives EVE's volumetric clouds the upscaler's jitter and, with Scatterer's cloud
  shaders, blends the clouds' own motion vectors into the scene's;
* has skinned renderers -- kerbals, flags -- draw their motion vectors;
* switches KSP's MSAA off and forces anisotropic filtering on;
* raises KSP's LOD bias by the upscaling factor, so that objects keep the level of
  detail they have at full resolution;
* sets a negative mipmap bias on the textures in the 3D scene.

Each of these has a switch on the diagnostics window's *Debug* tab.

### Frame rate

Measured on an RTX 4090 at 3440x1440 with Scatterer, EVE's volumetric clouds,
Parallax, Deferred and TUFX: the GPU runs at 90-97 % on work that does not depend on
the output resolution -- shadow cascades, cloud volumes, reflection probes -- and
rendering the scene with fewer pixels did not shorten the frame. On such a setup the
upscaler improves the image as antialiasing (*AA only*). The smaller modes gain frame
rate where the number of pixels limits it.

### Known limitations

* **Vertex-animated geometry** -- Parallax's grass swaying, for example -- has no
  motion vectors and can smear.
* **In the smaller modes, UI anchored in the world** can sit slightly off: it
  positions itself from the camera's pixel size, which is the render size then.
* **Transparent effects** -- EVE's clouds, Scatterer's ocean, plumes, re-entry --
  are treated as solid unless the transparency and reactive masks on the *Debug*
  tab are switched on.
* **Camera cuts** reset the upscaler's history when the camera's target, its parent
  or the IVA seat changes. Two cameras on one Hullcam part, CameraTools switching its
  mode or vessel, and jumps inside a CameraTools mode are not detected.

## Frame generation

Frame generation puts generated frames between the rendered ones, from the same
depth and motion vectors the upscaler uses. KSP renders in Direct3D 11; the proxy
presents its frames through a Direct3D 12 swapchain, where frame generation runs.

| Frame generation | Runs on | Frames |
|---|---|---|
| **DLSS** | NVIDIA RTX 40 and newer, with NVIDIA's Streamline 2.14.1 DLLs, downloaded with *NVIDIA DLSS files* ([installing.md](installing.md)), and hardware-accelerated GPU scheduling on | as many per rendered frame as the GPU offers, within the V-Sync limit below |
| **FSR 3** | GPUs with Direct3D 12, where DLSS frame generation does not run | one between every two rendered frames |

The *Frame generation* row shows which one runs, as *DLSS 2x* or *FSR 3*. It needs
the proxy; without it the row is greyed out, unless frame generation is on, so that
it can always be switched off.

With the upscaler off, frame generation runs on its own: the scene is still captured
at full resolution for it, without the upscaler's antialiasing.

**Where it pays.** Where the rendered frame rate is low -- large vessels, many parts,
re-entry. Input responds at the rendered frame rate. AMD recommends at least 60
rendered frames a second for FSR 3 frame generation; below that, fast camera turns
show artefacts.

The diagnostics window's frame rate line shows the frames rendered and, in brackets,
the frames presented with the generated ones.

### V-Sync

Turn V-Sync on in KSP's settings while frame generation runs, on a fixed refresh rate
monitor and with G-Sync or FreeSync alike. Without V-Sync, more frames can reach the
monitor than it shows, and they tear or drop unevenly -- with G-Sync and FreeSync too,
once the frame rate is above the monitor's range.

* With **DLSS frame generation**, V-Sync presents every refresh at most: *Every second
  refresh* is not offered in ReDefinition's window while it runs, and a value set
  elsewhere is put right. The number of generated frames is limited so that the
  monitor can show them: up to 4x at 60 Hz, 5x at 75 Hz. A
  DLSS frame generation build without V-Sync support presents without V-Sync.
* With **FSR 3 frame generation**, V-Sync holds the game at half the refresh rate, and
  every rendered and generated frame is shown for one refresh.

### The proxy's settings

`ReDefinitionProxy.ini` next to `KSP_x64.exe` (copy it from `GameData/ReDefinition`).
The settings below its *live* marker are read again within a second of the file
being saved.

| Setting | Default | Does |
|---|---|---|
| `enabled` | 1 | 0 passes everything through to Windows' own `dxgi.dll` |
| `frameGeneration` | 1 | 0: no frame generation, the proxy presents only |
| `dlssFrameGeneration` | 1 | 0: FSR 3 frame generation even where DLSS frame generation runs |
| `fgVSync` | 0 | 1: FSR 3 frame generation presents with V-Sync while it generates, whatever KSP's setting says |
| `fgHalfRefreshLimit` | 0 | 1: with FSR 3 frame generation and without V-Sync, the rendered frame rate is held just below half the refresh rate |
| `fgAsyncWorkloads` | 0 | 1: FSR 3 frame generation's work runs on a compute queue of its own |
| `fgPacingHybridSpin` | 0 | 1: FSR's pacing thread sleeps instead of spinning -- less precise, less power |
| `dlssDirectory`, `streamlineDirectory`, `amdUpscalerDirectory` | empty | the folders of NVIDIA's and AMD's DLLs, where they are not next to `KSP_x64.exe` |
| `reportSeconds` | 10 | how often frame times go into `ReDefinitionProxy.log` |

The other settings -- depth, motion vector and orientation conventions, the camera
basis, the HUD-less copy, measurement modes -- are for development and are described
in the file.
