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

Three more cases check the instruments. A readback of the sphere's centre must find the
sphere at the row the in-game checks read. The vessel check's shader, drawn over the
moving sphere with its previous matrix, must find more than 98% of the sphere's pixels
right, and drawn with the current matrix in its place, more than 90% wrong.

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
copied only where none is there yet.

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
| `REDEFINITION_STREAMLINE_DIR` | Streamline 2.14.1's DLLs, the SDK's `bin\x64` | DLSS frame generation's phases, V-Sync every second refresh among them, in place of FSR's; on an NVIDIA adapter only |
| `REDEFINITION_DLSS_DIR` | `nvngx_dlss.dll` | the DLSS upscaler |
| `REDEFINITION_AMD_UPSCALER_DIR` | `amd_fidelityfx_upscaler_dx12.dll` | AMD's upscaler |

One swapchain has one frame generation, so a run with `REDEFINITION_STREAMLINE_DIR` and
one without cover both. DLSS frame generation generates only while the harness window has
the focus.

Run the harness after every change to the proxy. It prints a `PASS` line for each part
that ran, or one `FAIL` line.

## Audit the frame generation fields

```
python tools/audit_ffx_fields.py
```

It enumerates every field of FSR's frame generation descriptors from the vendored headers.
It fails on any field the proxy neither sets nor lists with a reason.

## Build the release package

```
dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true
```

The working tree has to be committed first. What goes into the package, and why:
[packaging.md](packaging.md).
