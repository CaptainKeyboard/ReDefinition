# The upscaler and frame generation

**For:** players setting up upscaling or frame generation.
**You need:** ReDefinition installed, a graphics profile chosen, and for DLSS, AMD's
upscaler DLL, XeSS or frame generation other than FSR 3 the proxy and those files
([installing.md](installing.md)).
**You get:** what each setting does, which technique runs on your GPU, what it costs,
and where it falls short.

Both are active only while a graphics profile is chosen
([graphics-profiles.md](graphics-profiles.md)). Their settings are under *Display* in
the settings window, in the diagnostics window, and in KSP's own settings dialog.

The diagnostics window's strip names what is running, and shows the rendered frame
rate with the shown rate after an arrow.

## The upscaler

The upscaler renders KSP's 3D scene smaller and reconstructs the full-resolution image
from it. The interface is drawn on top at full resolution and stays sharp.

| Setting | Does |
|---|---|
| Upscaler | on or off |
| Technique | *FSR 3*, *DLSS*, *AMD FSR (DLL)* or *XeSS (DLL)*. If the chosen one cannot run, FSR 3 runs in its place, and the diagnostics window says why |
| Mode | *AA only*, at the right end, renders at full resolution and uses the upscaler as temporal antialiasing. This is what the profiles choose. Further left, the scene renders smaller by the factor shown and is reconstructed. DLSS renders at the size it asks for in each mode, and *1.2x*, which DLSS does not have, runs as DLSS's *Quality* |
| DLSS model | DLSS only, in every mode. *Default* is the model NVIDIA's DLSS library chooses for each mode. *Preset K* is the transformer model before M and L. *Preset M* is close to L in quality and close to K in speed. *Preset L* gives the sharpest, most stable image with the least ghosting, at the most GPU time. Low, Medium and High take *Default*, Ultra and Max *Preset L* |
| Sharpness | how hard the image is sharpened after the upscaler, for every technique, from 0 to 1.2. 0 switches it off. Above 1.0 goes past what the sharpening was made for, and AMD's DLL stops at 1.0 |

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
are off at first. FSR 3 and AMD's DLL take them. Both are on the *Upscaler inputs* page
of the diagnostics window.

### The four techniques

| Technique | Runs on | Needs |
|---|---|---|
| FSR 3 | every GPU | nothing: it is part of ReDefinition |
| DLSS | NVIDIA RTX 20 and newer | the proxy, and `nvngx_dlss.dll` |
| AMD FSR (DLL) | GPUs with Direct3D 12. FSR 4 on a Radeon RX 7000 or RX 9000 where the DLL has it, otherwise the FSR 3.1 in the DLL | the proxy, and `amd_fidelityfx_upscaler_dx12.dll` from a game that ships it |
| XeSS (DLL) | Intel Arc, and any other GPU with Shader Model 6.4 and DP4a | the proxy, and `libxess.dll` from Intel's XeSS SDK, which *Intel XeSS files* downloads |

DLSS, AMD's DLL and XeSS run inside the proxy.

Once the technique runs, the *Technique* row names what runs. For AMD's DLL that is the
FSR it runs, such as *FSR 4.1.1*. On a GPU without FSR 4, the DLL runs the FSR 3.1 it
carries instead. The row says *FSR 3.1.5 fallback*, and the log gives the reason. For
XeSS the row names its version, such as *XeSS 2.0.2*.

If the chosen technique cannot run, FSR 3 runs in its place and the diagnostics
window's *Overview* page says why. The reason is one of these: no proxy, no DLL, a GPU
without it, or a failure while it ran. After a failure, a button there tries the
technique again.

### What ReDefinition changes for it

While the upscaler runs, ReDefinition switches off what KSP and other mods bring for
antialiasing. A second temporal filter in front of the upscaler would average the image
twice:

* KSP's own MSAA;
* Scatterer's TAA and SMAA;
* TUFX's antialiasing;
* Deferred's SMAA in the editors;
* Kerbal Frame Generator's frame blend, where it is installed.

With the upscaler off, frame generation alone included, each gets back what it had,
unless it was changed since. Their own settings stay as they are.

While the upscaler runs, ReDefinition also keeps EVE's clouds, kerbals and flags from
smearing, draws TUFX's bloom, colour grading, depth of field, motion blur and grain
after the upscaler so they stay crisp, holds KSP's MSAA off, forces anisotropic
filtering on, and keeps textures and level of detail at what they are at full
resolution.

Each of these has a switch on the *Upscaler inputs* or *Game settings* page of the
diagnostics window, for finding out which one causes a problem.

### What it costs

The smaller modes gain frame rate where the number of pixels is what limits it. Where
something else limits it, they gain nothing, and the upscaler is worth having as
antialiasing in *AA only*.

The measurements behind that, for anyone who wants the numbers:
[development/flow-frame.md](../development/flow-frame.md).

### Where the upscaler falls short

| Case | What you see |
|---|---|
| Vertex-animated geometry, such as Parallax's swaying grass | it has no motion vectors and can smear |
| Transparent effects: EVE's clouds, Scatterer's ocean, plumes, re-entry | they are treated as solid unless the transparency and reactive masks on the *Upscaler inputs* page are switched on |
| Camera cuts a mod makes itself | the history resets when the camera's target, its parent or the IVA seat changes. Not detected: two cameras on one Hullcam part that have no transforms of their own, CameraTools switching its mode or vessel, and jumps inside a CameraTools mode |

## Frame generation

Frame generation puts generated frames between the rendered ones. Every technique needs
Direct3D 12, which KSP does not use: `dxgi.dll` next to `KSP_x64.exe` presents the game's
frames through a Direct3D 12 swapchain, and the game goes on rendering in Direct3D 11.
Without that file there is no frame generation.

| Frame generation | Runs on | Needs | Frames |
|---|---|---|---|
| DLSS | NVIDIA RTX 40 and newer, and RTX 30 and RTX 20 with the dlssg_for_sm86 package | NVIDIA's Streamline 2.14.16 DLLs, and hardware-accelerated GPU scheduling on | as many per rendered frame as the GPU offers, up to 6x, within the V-Sync limit below |
| XeSS | Intel Arc and Core Ultra, and any other GPU with Shader Model 6.4 | `libxess_fg.dll` and `libxell.dll` from Intel's XeSS SDK 2.1 or newer, next to `KSP_x64.exe` | one generated frame, 2x. On an Intel GPU `xessFgFrames` allows more |
| FSR 3 | GPUs with Direct3D 12 | `amd_fidelityfx_framegeneration_dx12.dll`, which comes with ReDefinition | one between every two rendered frames, 2x |

Which one runs is the proxy's `frameGenerationTechnique` setting, read when KSP starts.
*auto*, the default, takes DLSS where it runs, XeSS on an Intel GPU where its DLLs are
there, and FSR 3 otherwise. *dlss*, *xess* and *fsr* ask for that one, and FSR 3 takes
over where the one asked for does not run. The log says why.

With the upscaler off, frame generation runs on its own. The scene is still captured at
full resolution for it, without the upscaler's antialiasing.

Frame generation pays where the rendered frame rate is low: large vessels, many parts,
re-entry. Input responds at the rendered frame rate.

### Choose how many frames

The *Frame generation* row under *Display* is *Off*, *2x* to *6x*, or *Dynamic*. A
multiplier is the number of frames shown for each rendered frame. A change applies at
once.

* *Dynamic* is for DLSS. It lets DLSS frame generation choose the multiplier that
  brings the output to the display's refresh rate. It needs an NVIDIA driver 595.41 or
  newer and a GPU that does multi frame generation. If either is missing, the fixed
  multiplier stays and the log says so. `dlssgDynamicTargetFps` sets another target,
  and V-Sync overrides it.
* The default is *Dynamic* where the technique and the driver support it, otherwise
  *2x*.
* A step above what the technique allows runs as the most it allows. FSR 3 generates one frame.
  XeSS generates one frame, and on an Intel GPU as many as `xessFgFrames` asks for and the GPU
  supports. DLSS generates what the GPU, the driver and, on RTX 30 and RTX 20, the package's
  multiplier allow. Raising the package's multiplier needs a restart of KSP.

A grey note under the row names what generates, such as *Running: DLSS 3x*, and says
which steps are not offered and why.

Without the proxy the row is greyed out, unless frame generation is on, so that you can
always switch it off.

### DLSS frame generation needs the window in front

DLSS frame generation generates nothing while KSP's window is not in the foreground.
The rendered frames are shown alone, and the status stays fine. The proxy's log then
says that the game's window is not in the foreground, and when it is back. Keep the
window in front when you test or record frame generation.

### DLSS frame generation on RTX 30 and RTX 20

The *DLSS-G on RTX 30/20* row is under *Display* on an RTX GPU without DLSS frame generation of
its own, and stays once the package is in place. It runs DLSS frame generation through
dlssg_for_sm86, a community project by sdli1995. The project is neither NVIDIA's nor
ReDefinition's, and ReDefinition neither ships nor downloads it.

1. *Download manually ...* opens a window with the steps and the project's pages.
   Download `dlssg-release-<version>-x64.zip`.
2. Put the zip, as it is, into KSP's folder.
3. *Install from zip* takes `version.dll`, `dlssg_sm86.ini` and the `dlssg_sm86` folder
   out of it and places them next to `KSP_x64.exe`. A file already there is kept,
   renamed to end in `.old`. A first install raises the package's multiplier from
   its factory 4X to 6X and sets preset *C*. A reinstall puts your choices from the
   old file back.
4. Restart KSP. If NVIDIA's files for frame generation are missing, *NVIDIA DLSS
   files* offers them.

The install raises the package's multiplier from its factory 4X to 6X, so that the *Frame
generation* row has room. Four more rows set the package's `dlssg_sm86.ini` in place. The
package reads the file when KSP starts, so a change applies after a restart, and the row
says so.

| Row | Choices | Does |
|---|---|---|
| *DLSS-G preset* | *Auto*, *A*, *B*, *C (Lite)* | *C* is NVIDIA's lighter frame blending. It costs less GPU time, and in KSP the HUD and the scene come out the same as with *Auto*. Installing the package from the settings window sets *C* where the package's file still has *Auto*. *A* and *B* force the package's interface recomposition off and on |
| *DLSS-G multiplier* | *2X* to *6X* | the most frames shown per rendered frame that the package allows. Installing the package from the settings window sets *6X*. The *Frame generation* row chooses within it, at once; only a higher ceiling needs a restart |
| *DLSS super resolution* | *Off*, *On* | runs DLSS's presets L and M with the package's INT8 kernels on RTX 30 and RTX 20. It needs files of about 180 MB, which *Install SR files* takes out of the zip. It does nothing while Ray Reconstruction runs |
| *DLSS-G frame timing* | *Off*, *On* | the package writes the GPU time of each frame generation to `frames_<pid>.jsonl` in `dlssg_sm86\logs` |

Measured on an RTX A4500 at the Medium profile, preset *C* gave 4 to 8 % more rendered
frames a second than *Auto*. Without raytracing that is 71.1 against 66.1, with it 41.8
against 40.2. The HUD stayed intact. Its pixels that never move came out equal to the
rendered frames'. Along the HUD's edges *C* scored 1 to 3 dB below *Auto*
([development/flow-proxy-present.md](../development/flow-proxy-present.md)).

### XeSS frame generation

XeSS frame generation needs two DLLs from Intel's XeSS SDK, 2.1 or newer:
`libxess_fg.dll` and `libxell.dll`. They go next to `KSP_x64.exe`, or into the folder
named by `xessFgDirectory`. *Download ...* beside *Intel XeSS files* fetches both from
Intel's XeSS SDK 3.0.2
([installing.md](installing.md#get-the-files-for-dlss-xess-and-amds-upscaler)). A
`libxess_fg.dll` older than 1.2.2 is refused and FSR 3 runs instead, unless
`xessFgAllowOld` is 1.

XeSS frame generation brings Intel's XeLL, which lowers the latency
([low-latency.md](low-latency.md)). On a GPU other than Intel's, XeSS frame generation
generates one frame and needs Shader Model 6.4.

The `xessFg...` and `xell...` settings of the proxy's ini decide how many frames it
generates and how long it stays on across frames without inputs. They also decide how it
treats the UI and which frame latency waitable object KSP gets. Each is described in
[reference/proxy-settings.md](../reference/proxy-settings.md).

### When frame generation holds off

Generated frames need enough rendered frames to be made from. Frame generation holds
off by itself, and presents the rendered frames alone:

* while the rendered frame rate, over two seconds, is below a minimum. AMD designed FSR
  3.1 frame generation for at least 60 rendered frames a second and FSR 4's for at least
  30. DLSS frame generation holds off below 30, and XeSS frame generation below 40. It
  comes back once the rate is a little above the minimum again;
* for a single frame that takes more than one and a half times the minimum's frame
  time, such as a hitch while something loads;
* steadily, while V-Sync caps the frames so low that the rendered rate with frame
  generation would stay below the minimum. On a 60 Hz monitor with FSR 3 frame
  generation, that is 30 rendered frames a second;
* while KSP's pause menu is open.

`fgMinimumFps` in the proxy's settings sets another minimum, or none.

### V-Sync with frame generation

Turn V-Sync on in KSP's settings while frame generation runs. This holds on a fixed
refresh rate monitor and with G-Sync or FreeSync alike. Without V-Sync, more frames can
reach the monitor than it shows, and they tear or drop unevenly. That happens with
G-Sync and FreeSync too, once the frame rate is above the monitor's range.

With DLSS frame generation, V-Sync presents every refresh at most. *Every second
refresh* is therefore not offered in ReDefinition's window while it runs, and a value
set elsewhere is corrected. The same holds while Intel's XeLL lowers the latency, which
is always so with XeSS frame generation. The number of generated frames is limited. The
monitor must be able to show them, and the game must still render at least the minimum
([above](#when-frame-generation-holds-off)). Every frame, rendered or generated, takes
a refresh. With the minimum of 30, 60 Hz reaches 2x and 120 Hz 4x. The refresh rate
divided by 15 limits it as well, so 60 Hz reaches 4x at most even with
`fgMinimumFps=0`. A DLSS frame generation build without V-Sync support presents
without V-Sync.

With FSR 3 frame generation, V-Sync holds the game at half the refresh rate, and every
rendered and generated frame is shown for one refresh. On a 60 Hz monitor that is 30
rendered frames a second. If the minimum is 60, frame generation then stays off
for as long as V-Sync is on; a monitor of 120 Hz or more lets it run.

## What your GPU needs

| Feature | GPU | Besides |
|---|---|---|
| DLSS | NVIDIA RTX 20 and newer | `nvngx_dlss.dll` |
| DLSS frame generation | NVIDIA RTX 40 and newer; RTX 30 and RTX 20 with dlssg_for_sm86 | Windows 10 20H1 or newer, hardware-accelerated GPU scheduling, the Streamline 2.14.16 DLLs |
| *Dynamic* frame generation | NVIDIA, with multi frame generation | NVIDIA driver 595.41 or newer |
| FSR 3 frame generation | any GPU with Direct3D 12 | nothing |
| FSR 4 | Radeon RX 7000 and RX 9000 | `amd_fidelityfx_upscaler_dx12.dll` |
| XeSS | Intel Arc; any other GPU with Shader Model 6.4 and DP4a | `libxess.dll` |
| XeSS frame generation | Intel Arc and Core Ultra; any other GPU with Shader Model 6.4 | `libxess_fg.dll` and `libxell.dll` |
| NVIDIA Reflex | NVIDIA GeForce GTX 900 and newer | a driver of the R455 branch or newer |
| Intel XeLL | Intel GPUs; any other GPU only with XeSS frame generation | `libxell.dll` |
| AMD Anti-Lag 2 | Radeon RX 5000 and newer | `antiLag2` set in the proxy's ini |

What raytracing needs: [raytracing.md](raytracing.md).
