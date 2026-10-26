# Build and check everything

**For:** a developer who is about to commit a change to ReDefinition.
**You need:** the repository, the .NET SDK, KSP 1.12.5 with HarmonyKSP, Visual Studio Build
Tools with CMake, Python 3, and a GPU for the harnesses.
**You get:** every command to run before a commit, in order, and what each one needs.

Run every command from the repository root. Skip a step only when your change cannot reach it.
[Building and testing](building-and-testing.md) explains the parts one by one. KSP must not run
while you build the proxy, because the game holds `dxgi.dll`.

## The list, in order

1. Build the mod. It also deploys into the game.

   ```
   dotnet build ReDefinition.sln
   ```

   `KspRoot` names the game folder, by default the Steam location. `-p:DeployToGameData=false`
   builds without deploying. The output is `build/ReDefinition.dll`, which the check scripts
   in step 8 load.

2. Build the proxy. CMake with the Visual Studio 2022 generator makes `dxgi.dll`,
   `ProxyHarness.exe` and `RaytracingHarness.exe` in `build/DxgiProxy/Release`.

   ```
   cmake -S src/DxgiProxy -B build/DxgiProxy -G "Visual Studio 17 2022" -A x64
   cmake --build build/DxgiProxy --config Release
   ```

   If `cmake` is not on the path, call the one that comes with Build Tools:

   ```
   & "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
   ```

   The build copies `dxgi.dll` next to `KSP_x64.exe`. `-DKSP_DIR=...` names another game
   folder.

3. Build the shader bundle when you changed a file under `unity/Assets/ReDefinition`. It needs
   Unity 2019.4.18f1 and writes `redefinition.shaders` into the game.

   ```
   & "C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -projectPath unity -executeMethod ReDefinition.EditorTools.BundleBuilder.BuildFromCommandLine -logFile build\bundle.log
   ```

   After a change to `unity/Assets/ReDefinition/Include/ReDefinition.cginc`, run
   `IncludeCheck.RunFromCommandLine` the same way. After a change to the motion vectors, run
   `MotionVectorCheck.RunFromCommandLine`. Both are in the same editor namespace and exit 1 on
   a failure.

4. Run the unit tests. They need no running game, and building them builds the mod.

   ```
   dotnet test tests/ReDefinition.Tests
   ```

   `DeveloperDocumentationTests.EveryElementIsDocumentedAndNoneIsDirty` runs the DocLock check
   from step 7 as part of this.

5. Run the proxy harness. It prints a `PASS` line for each part that ran, or a `FAIL` line.

   ```
   build\DxgiProxy\Release\ProxyHarness.exe
   ```

6. Run the raytracing harness. It needs a GPU with DirectX Raytracing tier 1.1.

   ```
   build\DxgiProxy\Release\RaytracingHarness.exe
   ```

7. Check the comments with DocLock. `check` fails on an undocumented, dirty or unlocked
   element, a broken reference or a new lint finding. `lint` counts the findings, and the count
   must not rise. [Writing code comments](writing-code-comments.md) says what to do with a
   finding.

   ```
   dotnet run --project tools/DocLock -c Release -- check
   dotnet run --project tools/DocLock -c Release -- lint
   ```

8. Run the check scripts in `tools/checks`. The first four need the installed mods and the
   built `build/ReDefinition.dll`. The last needs Python only.

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/check_bundled_mods.ps1
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/check_profile_parser.ps1
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/check_key_bindings.ps1
   powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/check_foreign_code.ps1
   python tools/checks/audit_ffx_fields.py
   ```

9. Run the Python tests of the tools.

   ```
   python -m unittest
   ```

10. Check the pages under `docs/`. It must end with "All checks passed". Add `-Strict` to
    make the style lines fail too.

    ```
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/check_docs.ps1
    ```

## When your change touches more

* The dashboard: `npm install` and `npm run validate` in `tools/icuewidget` check the folder as
  an iCUE widget. The build packs it into `build/ksp-dashboard.icuewidget`.
* The raytracing: `python tools/build_rt_player.py` builds and runs the test player.
* A release: `dotnet build src/ReDefinition.csproj -c Release -p:ReleasePackage=true` on a
  committed tree. [Packaging](packaging.md) says what goes in.

## The raytracing test player

The player is a Unity project in `tests/RaytracingPlayer`. `tools/build_rt_player.py` copies
the raytracing sources from `src` into `Assets/Linked`, and they compile against the stubs in
`tests/RaytracingPlayer/Assets/Runner`: `GameStubs.cs`, `UpscalerRigStub.cs` and
`DeferredStub.cs`. Building it needs Unity 2019.4.18f1 and a built proxy. The unit tests do not
build it. `RaytracingPlayerStubTests.TheStubHasTheRigsMaximumSharpness` only holds one stub
value to the rig's.

## What can go wrong

* The build stops because KSP or HarmonyKSP is missing. The message names the path, which
  `KspRoot` in `Directory.Build.props` sets.
* The proxy build says `dxc not found: dxgi.dll is built without raytracing`. Install the
  Windows SDK, or `RaytracingHarness.exe` has nothing to test.
* `dotnet test` fails in `DeveloperDocumentationTests`. The output is DocLock's list of the
  elements to fix.
* A check script finds no mod. `check_foreign_code.ps1` passes where its mod is not installed,
  and a bare KSP leaves `check_bundled_mods.ps1` almost nothing to check.
* A check script's header still names `tools\checks\check_*.ps1`. The scripts are in `tools/checks`.
