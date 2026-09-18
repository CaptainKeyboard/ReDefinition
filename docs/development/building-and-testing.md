# Building and testing

## The mod

**Needs:** the .NET SDK 8. No .NET Framework developer pack: the reference assemblies
come as a NuGet package, and the repository brings its own `NuGet.config` because the
global one has no package sources on some machines. KSP 1.12.5 and HarmonyKSP in
`GameData/000_Harmony` must be installed: the build references the game's assemblies
and Harmony, and stops with a message where either is missing.

```
dotnet build ReDefinition.sln
```

The game's path is in `Directory.Build.props` (`KspRoot`), overridable with the
`KspRoot` environment variable or `-p:KspRoot=...`. Every build deploys into the game:

* `ReDefinition.dll` into `GameData/ReDefinition/Plugins`;
* `ReDefinition.version`, generated from the project's version;
* the repository's static `GameData` files -- registrations and profiles. What the
  build ships is removed from the game first (`Profiles/ReDefinition-*.cfg`, `Mods/*.cfg`),
  so a file dropped from the repository leaves the game. `PluginData` is never touched.

The solution builds the example mod too (`docs/modders/examples/ReDefinitionExample`),
into `build/examples/ReDefinitionExample`, never into the game. The mod's build writes
`ReDefinition.xml` beside the DLL and deploys it with it: the interface's documentation
for a mod's IDE.

`-p:DeployToGameData=false` builds without deploying. In Rider, open `ReDefinition.sln`
and choose the .NET SDK's MSBuild, not *Auto*, which may pick Build Tools without the
SDK.

## Unit tests

The store, `bundled.cfg`, the settings window's edit model, ReDefinition's modules, the
registrations, the frame packet layouts shared with the proxy, Streamline's camera
matrices, EVE's cloud motion, TUFX's split around the upscaler and NVIDIA's file list are
tested without the game, on .NET Framework 4.8:

```
dotnet test tests/ReDefinition.Tests
```

The tests use KSP's own `ConfigNode` from the game's `Assembly-CSharp`, copied beside
them. Types of the test assembly stand in for mods (`RegistrationTests.cs`), and
`Fakes.cs` has a mod and a game host for the store.

## Check scripts

What only the installed mods can answer, the check scripts answer. Both run in Windows
PowerShell 5.1 and in PowerShell 7, and take the game's path from `KspRoot` as the
build does:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_bundled_mods.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_profile_parser.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_key_bindings.ps1
```

**`check_bundled_mods.ps1`** loads the installed mods' assemblies and the built DLL,
reads every registration with KSP's `ConfigNode`, and builds each mod with the registry
the game runs. It checks that every member a registration names exists in the installed
build, lists the settings, rows, *Advanced* tabs, defaults and requirements it derives,
checks every default and profile value against the setting's control
(`ProfileApplier.Refusal`, the method the game asks), that High sets only its deviations
from the defaults, that the toolbar button assemblies are the ones the mods' buttons
live in, and that registrations of Waterfall that differ -- a missing `needs`, `required`
or `ready` member, a default of the wrong type -- behave as the guide says. It also reads
and builds `docs/modders/examples` and any registration in the installed `GameData`.

**`check_profile_parser.ps1`** feeds the profile and registration readers from the built
DLL with nodes holding one mistake of each kind, and checks what they make of it.

**`check_key_bindings.ps1`** reads the key bindings from the built DLL: the texts the
mods and KSP write, every `KEY` block of the shipped registrations with its default, and
KSP's own bindings in `GameSettings` -- that they are the shape the Keys tab reads, and
that every one of them has a name for its row.

Reflection from PowerShell into KSP's assemblies has two traps, both handled in the
scripts: an `AssemblyResolve` handler must use only `[IO.File]`/`[IO.Path]` and guard
against re-entry, or Windows PowerShell 5.1 overflows its stack; and arguments must be
passed as `object[]` of their base objects (`.PSObject.BaseObject`), never wrapped in
`@()`.

## The shaders

A Unity player cannot compile shaders at run time: they come as an AssetBundle from
exactly Unity 2019.4.18f1, KSP's version. In the editor, open `unity/`, then
*ReDefinition → Set KSP path* if needed and *ReDefinition → Build AssetBundle*. Headless:

```
"C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity ^
    -executeMethod ReDefinition.EditorTools.BundleBuilder.BuildFromCommandLine ^
    -logFile build\bundle.log
```

The bundle goes straight into the game, `GameData/ReDefinition/Shaders/redefinition.shaders`.
Errors are in the log file, not in the exit code. `unity/Packages/manifest.json` must
contain `com.unity.modules.assetbundle`: without it Unity writes a bundle the player
refuses, with a message that points at a version mismatch that is not there.

The FSR shaders are made from FSR3Unity's by `tools/port_fsr3_shaders.py` (the keyword
combination baked in, constant buffers turned into structured buffers, FSR 3.1.4's
tuning constants added):

```
python tools/port_fsr3_shaders.py <FSR3Unity checkout>
```

It replaces only the FSR files in `unity/Assets/ReDefinition/Shaders`. Change those
through the script, not by hand, and rebuild the bundle after.

**The shader include** for mods, `unity/Assets/ReDefinition/Include/ReDefinition.cginc`, is
not in the bundle. After a change, compile it with every function called:

```
"C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity ^
    -executeMethod ReDefinition.EditorTools.IncludeCheck.RunFromCommandLine ^
    -logFile build\include-check.log
```

The exit code is 1 on any shader error or warning, which the log names with its line.

## The proxy

**Needs:** MSVC with the C++ workload and a Windows 10 SDK; the CMake that comes with
Visual Studio Build Tools is enough, at
`...\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`
where it is not on the path.

```
cmake -S src/DxgiProxy -B build/DxgiProxy -G "Visual Studio 17 2022" -A x64
cmake --build build/DxgiProxy --config Release
```

The build copies `dxgi.dll` next to `KSP_x64.exe` (`-DKSP_DIR=...` for another game
folder; not while KSP runs, which holds the file), and `ReDefinitionProxy.ini` only where
none is there yet. Check the build's output and the DLL's time stamp rather than searching
the output for "error".

**The harness** does what Unity does -- loads `dxgi.dll` by bare name, creates a Direct3D
11 device, asks for the swapchain KSP asks for, renders, presents -- and drives frame
generation on, off, on again, across mode changes and a resize, checks the HUD-less copy,
and runs a compute pass on Direct3D 12 for mods from DXBC and, where the build found the
Windows SDK's `dxc`, from DXIL (`HarnessPass.cso`):

```
build\DxgiProxy\Release\ProxyHarness.exe
```

It runs FSR's frame generation where `amd_fidelityfx_framegeneration_dx12.dll` lies next to
it. Environment variables add what the player brings:

| Variable | A folder with | Runs |
|---|---|---|
| `REDEFINITION_STREAMLINE_DIR` | Streamline 2.14.1's DLLs, the SDK's `bin\x64` | DLSS frame generation's phases, V-Sync every second refresh among them, in place of FSR's; on an NVIDIA adapter only |
| `REDEFINITION_DLSS_DIR` | `nvngx_dlss.dll` | the DLSS upscaler |
| `REDEFINITION_AMD_UPSCALER_DIR` | `amd_fidelityfx_upscaler_dx12.dll` | AMD's upscaler |

One swapchain has one frame generation: a run with `REDEFINITION_STREAMLINE_DIR` and one
without cover both. DLSS frame generation generates only while the harness window has the
focus. Run it after every change to the proxy; it ends with one `PASS` or `FAIL` line.

**The field audit** enumerates every field of FSR's frame generation descriptors from
the vendored headers and fails on any the proxy neither sets nor lists with a reason:

```
python tools/audit_ffx_fields.py
```

## The release package

`dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true`, from a committed
tree: [packaging.md](packaging.md).
