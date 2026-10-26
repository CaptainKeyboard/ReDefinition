# Build and check everything

**For:** a developer who is about to commit a change to ReDefinition.
**You need:** the repository, the .NET SDK 10, KSP 1.12.5 with HarmonyKSP, Visual Studio Build
Tools with CMake, Python 3, and a GPU for the harnesses.
**You get:** every command to run before a commit, in order, and what each one needs.

Run every command from the repository root. Skip a step only when your change cannot reach
it.
Each step links to the section of [Building and testing](building-and-testing.md) that
explains it: what the step needs, what it puts where, and what its options do. KSP must not
run while you build the proxy, because the game holds `dxgi.dll`.

## The list, in order

1. Build the mod. It also deploys into the game, and it writes `build/ReDefinition.dll`,
   which the check scripts in step 8 load
   ([Build the mod](building-and-testing.md#build-the-mod)).

   ```
   dotnet build ReDefinition.sln
   ```

2. Build the proxy. It makes `dxgi.dll` and both harnesses, and it copies `dxgi.dll` into the
   game folder ([Build the proxy](building-and-testing.md#build-the-proxy)).

   ```
   cmake -S src/DxgiProxy -B build/DxgiProxy -G "Visual Studio 17 2022" -A x64
   cmake --build build/DxgiProxy --config Release
   ```

3. Build the shader bundle when you changed a file under `unity/Assets/ReDefinition`. It
   needs Unity 2019.4.18f1 and writes `redefinition.shaders` into the game. The command is
   in [Build the shader bundle](building-and-testing.md#build-the-shader-bundle).
   After a change to the shader include, run
   [its check](building-and-testing.md#check-the-shader-include). After a change to the
   motion vectors, run [theirs](building-and-testing.md#check-the-computed-motion-vectors).
   Both exit 1 on a failure.

4. Run the unit tests. They need no running game, and building them builds the mod
   ([Run the unit tests](building-and-testing.md#run-the-unit-tests)).

   ```
   dotnet test tests/ReDefinition.Tests
   ```

   The tests run the DocLock check from step 7 as well
   ([the test that does it](building-and-testing.md#run-the-unit-tests)).

5. Run the proxy harness. It prints a `PASS` line for each part that ran, or a `FAIL` line.
   Its variables are in [Run the proxy harness](building-and-testing.md#run-the-proxy-harness).

   ```
   build\DxgiProxy\Release\ProxyHarness.exe
   ```

6. Run the raytracing harness. It needs a GPU with DirectX Raytracing tier 1.1
   ([Run the raytracing harness](building-and-testing.md#run-the-raytracing-harness)).

   ```
   build\DxgiProxy\Release\RaytracingHarness.exe
   ```

7. Check the comments with DocLock. `check` fails on a broken reference or a new lint
   finding. `lint` counts the findings. [Writing code comments](writing-code-comments.md)
   says what to do with a finding.

   ```
   dotnet run --project tools/DocLock -c Release -- check
   dotnet run --project tools/DocLock -c Release -- lint
   ```

8. Run the check scripts in `tools/checks`. The first four need the installed mods and the
   built `build/ReDefinition.dll`. The last needs Python only. What each one checks is in
   [Run the check scripts](building-and-testing.md#run-the-check-scripts).

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
* The raytracing: `python tools/build_rt_player.py` builds and runs the test player
  ([Run the raytracing test player](building-and-testing.md#run-the-raytracing-test-player)).
* A release: [Build the release package](building-and-testing.md#build-the-release-package),
  on a committed tree. [Packaging](packaging.md) says what goes in.

## What can go wrong

* The build stops because KSP or HarmonyKSP is missing. The message names the path, which
  `KspRoot` in `Directory.Build.props` sets.
* The proxy build says `dxc not found: dxgi.dll is built without raytracing`. Install the
  Windows SDK, or `RaytracingHarness.exe` has nothing to test.
* `dotnet test` fails in `DeveloperDocumentationTests`. The output is DocLock's list of the
  elements to fix.
* A check script finds no mod. `check_foreign_code.ps1` passes where its mod is not installed,
  and a bare KSP leaves `check_bundled_mods.ps1` almost nothing to check.
