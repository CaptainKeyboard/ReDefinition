# Building and testing

**For:** anyone building ReDefinition, its shaders or its proxy, and running the checks.
**You need:** the repository, KSP 1.12.5 with HarmonyKSP in `GameData/000_Harmony`, and
the tool each section names.
**You get:** the command for every part, what it needs, and what it puts where.

## Build the mod

You need the .NET SDK 8. You do not need the .NET Framework developer pack: the reference
assemblies come as a NuGet package. The repository brings its own `NuGet.config`, because
the global one has no package sources on some machines.

KSP 1.12.5 and HarmonyKSP have to be installed. The build references the game's
assemblies and Harmony, and stops with a message where either is missing.

```
dotnet build ReDefinition.sln
```

The game's path is `KspRoot` in `Directory.Build.props`. Override it with the `KspRoot`
environment variable or with `-p:KspRoot=...`.

Every build deploys into the game:

* `ReDefinition.dll` into `GameData/ReDefinition/Plugins`;
* `ReDefinition.version`, generated from the project's version;
* the repository's static `GameData` files, meaning the registrations and the profiles.

What the build ships is removed from the game first (`Profiles/ReDefinition-*.cfg` and
`Mods/*.cfg`), so a file dropped from the repository leaves the game too. `PluginData` is
never touched.

The solution builds the example mod as well
(`docs/modders/examples/ReDefinitionExample`), into `build/examples/ReDefinitionExample`
and never into the game. The mod's build writes `ReDefinition.xml` beside the DLL and
deploys it with the DLL. That file is the interface's documentation for a mod's IDE.

`-p:DeployToGameData=false` builds without deploying. In Rider, open `ReDefinition.sln`
and choose the .NET SDK's MSBuild, not *Auto*, which may pick Build Tools without the SDK.

## Run the unit tests

```
dotnet test tests/ReDefinition.Tests
```

These run without the game, on .NET Framework 4.8. They cover the store, `bundled.cfg`,
the settings window's edit model, ReDefinition's modules, the registrations, the frame
packet layouts shared with the proxy, Streamline's camera matrices, EVE's cloud motion,
TUFX's split around the upscaler, NVIDIA's file list, and the key bindings with the
decisions the *Controls* tab makes.

The tests use KSP's own `ConfigNode` from the game's `Assembly-CSharp`, copied beside
them. Types of the test assembly take the place of mods (`RegistrationTests.cs`), and
`Fakes.cs` has a mod and a game host for the store.

## Run the check scripts

What only the installed mods can answer, the check scripts answer. The three mod checks
run in Windows PowerShell 5.1 and in PowerShell 7, and take the game's path from
`KspRoot` as the build does. `check_docs.ps1` reads only the pages and needs neither.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_bundled_mods.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_profile_parser.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_key_bindings.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_foreign_code.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_docs.ps1
```

`check_docs.ps1` checks the pages under `docs/` rather than the game:
[writing-these-pages.md](writing-these-pages.md) says what it wants.

**`check_bundled_mods.ps1`** loads the installed mods' assemblies and the built DLL. It
can only check a mod that is installed, so a run against a bare KSP checks almost
nothing. It
reads every registration with KSP's `ConfigNode` and builds each mod with the registry the
game runs. It then checks:

* that every member a registration names exists in the installed build;
* the settings, the rows of the four tabs, defaults and requirements it derives, which
  it lists;
* every default and profile value against the setting's control, through
  `ProfileApplier.Refusal`, the method the game asks;
* that High sets only its deviations from the defaults;
* that the toolbar button assemblies are the ones the mods' buttons live in;
* that registrations of Waterfall that differ behave as
  [modders/registration-reference.md](../modders/registration-reference.md) says. Such a
  registration is missing a `needs`, `required` or `ready` member, or has a default of the
  wrong type.

It also reads and builds `docs/modders/examples` and any registration in the installed
`GameData`.

**`check_profile_parser.ps1`** feeds the profile and registration readers from the built
DLL with nodes holding one mistake of each kind, and checks what they make of it.

**`check_key_bindings.ps1`** reads the key bindings from the built DLL: the texts the mods
and KSP write, every `KEY` block of the shipped registrations with its default, and KSP's
own bindings in `GameSettings`. It checks that they are the shape the *Controls* tab reads,
and that every one of them has a name for its row.

**`check_foreign_code.ps1`** checks the catalogue of what ReDefinition assumes about other
mods' code (`src/Core/ForeignAssumptions.cs`) against the installed mods. Each assumption
names types, methods, fields and string literals, and the methods it rests on with a
fingerprint of their instructions (`src/Core/ForeignCode.cs`). It passes where all of that
is there and the fingerprints match, or where its mod is not installed. It fails where
something named is missing or the code changed, and prints the installed fingerprint to
add once the new code has been read. The exit code is the number of failures. Run it after
a bundled mod's update. In the game, an assumption marked `WhenChanged.Fallback` takes the
way that does not rest on it where its code changed, and `ForeignAssumptionReport` writes
what came of each into `KSP.log` at the main menu and into the diagnostics window's *Other
mods* page, under *Their code*.

Reflection from PowerShell into KSP's assemblies has two traps, and the scripts handle
both. An `AssemblyResolve` handler must use only `[IO.File]` and `[IO.Path]` and guard
against re-entry, or Windows PowerShell 5.1 overflows its stack. Arguments must be passed
as an `object[]` of their base objects (`.PSObject.BaseObject`), never wrapped in `@()`.

The pages under `docs/` have a check of their own:
[writing-these-pages.md](writing-these-pages.md).

## Build the shader bundle

A Unity player cannot compile shaders at run time. The shaders come as an AssetBundle
from exactly Unity 2019.4.18f1, KSP's version.

In the editor, open `unity/`, then choose *ReDefinition → Set KSP path* if needed and
*ReDefinition → Build AssetBundle*. Headless:

```
"C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity ^
    -executeMethod ReDefinition.EditorTools.BundleBuilder.BuildFromCommandLine ^
    -logFile build\bundle.log
```

The bundle goes straight into the game, as
`GameData/ReDefinition/Shaders/redefinition.shaders`. Shader errors go into the log
file. The process exits 1 only where the build itself fails.

`unity/Packages/manifest.json` must contain `com.unity.modules.assetbundle`. Without it
Unity writes a bundle the player refuses, with a message that points at a version mismatch
that is not there.

## Port the FSR shaders

`tools/port_fsr3_shaders.py` makes the FSR shaders from FSR3Unity's. It bakes in the
keywords, six of the seven, turns constant buffers into structured buffers, and adds
FSR 3.1.4's tuning constants.

```
python tools/port_fsr3_shaders.py <FSR3Unity checkout>
```

It replaces only the FSR files in `unity/Assets/ReDefinition/Shaders`. Change those
through the script, not by hand, and rebuild the bundle afterwards.

## Check the shader include

The shader include for mods, `unity/Assets/ReDefinition/Include/ReDefinition.cginc`, is
not in the bundle. After a change, compile it with every function called:

```
"C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity ^
    -executeMethod ReDefinition.EditorTools.IncludeCheck.RunFromCommandLine ^
    -logFile build\include-check.log
```

The exit code is 1 on any shader error or warning, which the log names with its line.

## Check the computed motion vectors

The distant planets' motion vectors and `ReDefinitionMotionVector` in the include are
computed, not taken from Unity. After a change to either, compare them with Unity's own:

```
"C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity ^
    -executeMethod ReDefinition.EditorTools.MotionVectorCheck.RunFromCommandLine ^
    -logFile build\motion-vector-check.log
```

It builds a small Windows player into `build\motion-vector-check` and runs it. A window
opens for a few seconds and stays black, since the camera renders into a render texture.
The player moves first its camera, then a sphere, and writes Unity's motion vectors over
the sphere next to those pass 2 of the cloud motion shader computes. The exit code is 1
when the pass misses more than 5% of the sphere's pixels or its mean differs from
Unity's by more than 3%. The log shows both values for each case.

Four more cases check the vessel's motion vectors and the instruments. A readback of the
sphere's centre must find the sphere at the row the in-game checks read. The vessel
check's shader, drawn over the moving sphere with its previous matrix, must find more
than 98% of the sphere's pixels right, and drawn with the current matrix in its place,
more than 90% wrong. Its pass that writes the vessel's motion vectors, drawn into empty
ones, must leave more than 98% of the sphere's pixels right. A cube drawn only in the
forward pass must be missing from `ResolvedDepth`, present in the camera's own depth
buffer, and written into the depth by the vessel's depth pass within 1% of that value.

Three cases check the vessel's shadow for frame generation (`VesselShadowLayer`): a red
block over a white ground under a light with soft shadows. In the first the block and the
camera move together, in the second only the camera. In the third the ground is moved
back instead, as Krakensbane does, and everything is moved by a few metres on top, as the
floating origin does, both reported as KSP reports them. In all three the pass must find
more than 90% of the ground in the block's shadow and take less than 2% more from lit
ground. Where the shadow stands on the screen, the mean weight must keep it in place
(above 0.8), and after the composition the shadowed ground must reach 93 to 107% of its
brightness in the same frame rendered without the block's shadow, the lit ground
unchanged. Where only the camera moves, the weight must leave the shadow to the motion
vectors (below 0.2).

One case checks the far ground (`SceneMotion`): a camera 150 km from the origin, with
KSP's near and far plane, a sphere 150 km ahead, and a move of 10 m sideways and 6 m up.
Pass 2 must lie within 0.01 px of the parallax worked out for each pixel, and neighbouring
pixels must differ by less than 0.01 px on average. Its mean must lie within 3% of
Unity's. The log shows how much Unity's own vectors differ from one pixel to the next.

The check camera carries the plugin's motion vector repair (`UnityMotionRepair`): the
buffer cleared at the end of every frame, and pass 3 at `BeforeImageEffectsOpaque`. In the
camera and the object case a copy right after the clear must hold the cleared value on every
pixel, and no pixel of the next frame's copy at the repair may still hold it: Unity has
written every pixel anew, and the repair kept its values. One more case clears
what Unity wrote right before the repair and moves the camera; pass 3 must then give the
camera's move within 0.01 px on average over a sphere ahead.

## Build the proxy

You need MSVC with the C++ workload and a Windows 10 SDK. The CMake that comes with Visual
Studio Build Tools is enough. If it is not on the path, it is here:

```
...\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe
```

```
cmake -S src/DxgiProxy -B build/DxgiProxy -G "Visual Studio 17 2022" -A x64
cmake --build build/DxgiProxy --config Release
```

The build copies `dxgi.dll` next to `KSP_x64.exe`. Use `-DKSP_DIR=...` for another game
folder. Do not build while KSP runs, because it holds the file. `ReDefinitionProxy.ini` is
copied only where none is there yet. The proxy writes and completes the file itself from
the template built into it ([packaging.md](packaging.md#the-proxys-settings-file)).

Check the build's output and the DLL's time stamp, rather than searching the output for
"error".

## Run the proxy harness

The harness does what Unity does. It loads `dxgi.dll` by bare name, creates a Direct3D 11
device, asks for the swapchain KSP asks for, renders and presents.

It drives frame generation on, off and on again, across mode changes and a resize, and it
checks the HUD-less copy. It also runs a compute pass on Direct3D 12 for mods from DXBC,
and, where the build found the Windows SDK's `dxc`, from DXIL (`HarnessPass.cso`).

```
build\DxgiProxy\Release\ProxyHarness.exe
```

It runs FSR's frame generation where `amd_fidelityfx_framegeneration_dx12.dll` lies next
to it. Environment variables add what the player brings:

| Variable | A folder with | Runs |
|---|---|---|
| `REDEFINITION_STREAMLINE_DIR` | Streamline 2.14.16's DLLs, the SDK's `bin\x64` | DLSS frame generation's phases, V-Sync every second refresh among them, in place of FSR's; on an NVIDIA adapter only |
| `REDEFINITION_XESS_FG_DIR` | `libxess_fg.dll` and `libxell.dll` | XeSS frame generation's phases, in place of FSR's |
| `REDEFINITION_DLSS_DIR` | `nvngx_dlss.dll` | the DLSS upscaler |
| `REDEFINITION_AMD_UPSCALER_DIR` | `amd_fidelityfx_upscaler_dx12.dll` | AMD's upscaler |
| `REDEFINITION_XESS_DIR` | `libxess.dll` | the XeSS upscaler |

The XeSS folders can be the ones *Intel XeSS files* fills (`xessDirectory` and
`xessFgDirectory` of an install's ini, or its game folder), and `REDEFINITION_XESS_DIR` and
`REDEFINITION_XESS_FG_DIR` can name a copy from the same release. The benchmark runner's
`--xess-dll-dir` and `--xess-fg-dll-dir` take them too ([benchmarks.md](benchmarks.md)).

One swapchain has one frame generation, so a run with `REDEFINITION_STREAMLINE_DIR`, one
with `REDEFINITION_XESS_FG_DIR` and one with neither cover all three. Setting both names
is refused.

These variables change a run without naming a folder:

| Variable | Value | Does |
|---|---|---|
| `REDEFINITION_DLSSG_MODE` | `on` or `dynamic` | starts DLSS frame generation in that mode (`dlssgMode`). The harness asks Dynamic for a target of 8000 frames a second, since its own thousand frames a second are far above the display's |
| `REDEFINITION_XESS_FG_WAITABLE` | `auto`, `0` or `1` | `xessFgOwnWaitable` for the run; the waits on the frame latency waitable object are counted |
| `REDEFINITION_HARNESS_FOCUS` | any | DLSS frame generation generates nothing while the harness window is out of the foreground. The taskbar takes the foreground for a moment and the window gets it back: 1.00 presents per frame out of it, and more in it |
| `REDEFINITION_LATENCY_TEST` | `1` | compares what lowers the latency most on this GPU: nothing, the brake, and AMD Anti-Lag 2 on either device, measured alike under a Direct3D 11 compute load that holds the frames back ([low-latency.md](low-latency.md)) |
| `REDEFINITION_ADAPTER_VENDOR` | a PCI vendor id in hex: `1002` AMD, `10DE` NVIDIA, `8086` Intel | runs on the adapter of that vendor rather than the default one |
| `REDEFINITION_HARNESS_CHECKER` | any | AMD's upscaler gets a frame time in seconds rather than milliseconds, so that its debug checker (`vendorDiagnostics=1`) reports it into the log |

Run the harness after every change to the proxy. It prints a `PASS` line for each part
that ran, or one `FAIL` line.

## Run the raytracing harness

The raytracing harness loads the proxy the same way and needs a GPU with DirectX
Raytracing tier 1.1. It rasterises a G-buffer in Unity's layout, hands the proxy meshes and
instances, and checks every raytracing pass against the CPU
([raytracing.md](raytracing.md), "Testing without the game").

```
build\DxgiProxy\Release\RaytracingHarness.exe
```

It prints a `PASS` or `FAIL` line per check and the GPU time of each pass. It writes an
image per check next to itself, `rt_*.bmp`, to look at.

Where NVIDIA's `sl.dlss_d.dll` and `nvngx_dlssd.dll` lie next to the other Streamline
DLLs, it also checks DLSS Ray Reconstruction, through Streamline and through NGX. Both come
with the Streamline SDK 2.14.16, in `bin\x64`.

One argument narrows the run to the opacity micromaps:

| Argument | Runs |
|---|---|
| `--omm` | only the checks against the driver: the micro-triangles' order and packing, the baker |
| `--omm-table` | the same, and lists the order the driver has |
| `--omm-scene` | only the micromaps through the proxy |
| `--omm-none` | the same, with the driver's capability simulated as none (`REDEFINITION_OMM_CAPS=none`) |

The full run has both, and a run of its own with the capability simulated as none.
[raytracing.md](raytracing.md#testing-without-the-game) lists what each phase checks.

## Run the raytracing test player

The test player is a Unity 2019.4.18f1 player built as KSP's is: Direct3D 11, native
graphics jobs, gamma colour space. It builds a scene of a ground and two boxes, renders it
with and without raytracing, and checks the images. It runs three times: with forward
rendering, with Unity's deferred lighting, and with Deferred's lighting shaders from the
installed game. The first two check that raytracing replaces Unity's ambient light
without Deferred; the third checks Deferred's path, a floating origin jump and the night
side.

```
python tools\build_rt_player.py
```

It needs Unity 2019.4.18f1 at `C:\Program Files\Unity\Editor\Unity.exe` and a built
proxy; `--unity` and `--ksp` name other places, `--no-build` runs the last build again.
It prints each run's `PASS` and `FAIL` lines and exits 0 when all of them passed. The
images of each run are in `build\RaytracingPlayer\<run>`.

With `REDEFINITION_D3D12_DEBUG=1` set, the proxy turns on Direct3D 12's debug layer in
either harness and the test player, and writes its messages into its log.

### The camera turn

```
python tools\build_rt_player.py --turn
```

Instead of the checks, the player records a camera turn around a rocket on its pad
(`RtTurnRunner.cs`): settled, 20 frames at 2 degrees a frame, 10 at 12, one jump of 40, as
after a hitch, then still. It runs through DLSS Ray Reconstruction as the upscaler sends
it, at the render size; with ReDefinition's denoiser; with Unity's lighting alone; through
Ray Reconstruction with the upscaler's jitter; and, where Deferred is installed, with
jitter and Deferred's lighting shaders, and with Deferred's lighting alone; and with the
trace before the lighting of its own frame (`-early`), as the game runs it, beside the
trace after the frame. For Ray Reconstruction the script writes a `ReDefinitionProxy.ini`
next to the player that names Streamline's folder (`--streamline`, by default
`REDEFINITION_STREAMLINE_DIR` or the SDK's `bin\x64` beside the repository), so that the
proxy sets Streamline up on the player's swapchain, and removes it afterwards. Each frame
of the turn and each pose again after the camera stood there, as its reference, go into
`build\RaytracingPlayer\turn-<run>`, with the poses halfway between two frames.

`tools\analyze_rt_turn.py` then compares each frame with its reference: the error over
what came into view in that frame, behind a silhouette or past the frame's edge, over what
came into view within four frames, and over the rest. It checks Unity's motion vectors
against the cameras' motion as well, and writes `error_<frame>.png` images. It prints
`PASS` or `FAIL` per run against limits of about twice the values measured on 2026-10-01;
without raytracing the turn equals its references.

### The upscalers on the camera turn

```
python tools/build_rt_player.py --upscale
```

The same turn without raytracing, rendered at two thirds and at half the display size
with the upscaler's jitter and upscaled by FSR 3 (its Unity port), DLSS and AMD's
upscaler, as the rig sends each (`RtTurnUpscale.cs`), and once natively without jitter.
DLSS comes from the Streamline SDK's folder (`--streamline`), AMD's upscaler from
`--amd`, FSR 3's shaders from the game's bundle. The references are each pose rendered at
twice the display size without jitter. `tools/analyze_upscale_turn.py` prints, per run,
the error where something came into view, the rest, the error once still and how much a
still frame flickers.

`-jitterSign x|y|xy` and `-motionSign x|y|xy` negate what the upscaler is given, as a
counter-check of the conventions: each measures worse than the rig's.

The turn goes through frame generation as well:

```
python tools\turn_to_fg_dump.py build\RaytracingPlayer\turn-rr <dump folder> 18 40
tools\replay_fg.ps1 <dump folder, absolute>
python tools\measure_fg_turn.py <recording>\client build\RaytracingPlayer\turn-rr 18
```

The first writes frames 18 to 40 of the turn as frame generation's input dump. The last
compares each generated frame with the reference of the pose halfway, beside a copy of
the newer frame, which is what a reset would show there.

## Render KSP's meshes in the raytracing harness

With a scene of KSP's own meshes the raytracing harness also renders the launch pad and the
SPH. The scene holds Squad's geometry, so write it outside the repository:

```
python tools\export_rt_scene.py "<KSP folder>" %TEMP%\ksc.rtscene
set REDEFINITION_RT_SCENE=%TEMP%\ksc.rtscene
build\DxgiProxy\Release\RaytracingHarness.exe
```

## Replay the game's frames through frame generation

*Record the screen in 5 s (120 frames)* on the diagnostics window's *Capture & log* page
also writes eight consecutive frames of what frame
generation receives into a folder ending in `-inputs`: the HUD-less colour, depth, motion
vectors, the frame as presented and each frame's packet (`FrameGenerationDump.cpp`). The
frames are taken from the first frame the camera turns by more than a degree, or after 600
frames without. The staging textures they are copied into are made while the dump waits
for the turn, so the frames taken cost a copy each; they are read back one texture a frame
and written in a thread of their own: written at once, the files stalled the game for
about 300 ms in the recording.

From the same turn, four frames of the stages go into a folder ending in `-stages`
(`StageDump.cs`): the image the upscaler or DLSS Ray Reconstruction takes (`input`) and
gives (`output`), and the raytracing's signals traced in that frame, `sun` with the kept
view depth in w, `diffuse`, `specular`, `forward` and `depth`, rows bottom-up as Unity
reads them back. `meta.txt` gives each frame's camera, without jitter, and the jitter.
The textures are copied on the GPU into textures made while the dump waits for the turn,
and read back one a frame afterwards through one buffer, so the frames dumped cost a copy
each. `frames.txt` of the recording names each recorded frame's game frame, the number
`meta.txt` and the input dump's `packetIndex` give.
They show at which stage a fault enters the frame: in the signals, in the lighting that
reads them, or in the upscaler.

The replay reads each texture at its own size, so a dump rendered below the display size
plays too.

```
python tools\analyze_fg_inputs.py <folder>
```

It checks the inputs against each other: the previous frame's colour fetched where each
motion vector points, with the vectors as delivered, scaled and reversed; the vectors
against the camera motion from depth and `clipToPrevClip`; how much of the frame has
depth.

```
tools\replay_fg.ps1 <folder> [NAME=1 ...]
```

It plays the folder through DLSS frame generation in the harness, at half its size, and
records the screen. The recording is cut to the replay window at once and the rest of
the screen deleted; it prints the detail of rendered and generated frames. The window
has to stay in front, otherwise nothing is recorded. `REDEFINITION_STREAMLINE_DIR` must
be set. Each `NAME` changes one input:

| Variable | Changes |
|---|---|
| `REDEFINITION_REPLAY_NO_JITTER` | jitter 0 |
| `REDEFINITION_REPLAY_NO_MOTION` | every motion vector 0 |
| `REDEFINITION_REPLAY_STILL_CAMERA` | `clipToPrevClip` the identity |
| `REDEFINITION_REPLAY_FLAT_DEPTH` | one depth wherever anything is drawn |
| `REDEFINITION_REPLAY_PLAIN_HUDLESS` | the frame itself as the HUD-less image, as before the vessel's shadow was taken out |

```
python tools\make_fg_test_dump.py <folder> <speed> [--perspective] [--jitter PX] [--noise GREY]
```

It writes such a folder with known inputs: grain moving `<speed>` pixels a frame on the
left half with exact motion vectors, still on the right. `--perspective` makes the speed
grow from the top to the bottom, `--jitter` and `--noise` change each frame's content.

## Audit the frame generation fields

```
python tools/checks/audit_ffx_fields.py
```

It enumerates every field of FSR's frame generation descriptors from the vendored headers.
It fails on any field the proxy neither sets nor lists with a reason.

## Build the release package

```
dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true
```

The working tree has to be committed first. What goes into the package, and why:
[packaging.md](packaging.md).
