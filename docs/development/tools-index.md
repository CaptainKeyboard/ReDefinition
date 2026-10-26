# The scripts in tools

**For:** a developer who looks for the script that does a job, or who wonders what one is for.
**You need:** the repository.
**You get:** every script in `tools`, grouped by purpose, with what it needs and which ones
look obsolete.

The purpose of each row comes from the script's own header. "Game" means the script starts KSP
or reads files the game wrote. "RenderDoc" means it drives RenderDoc. "Neither" means it runs
on files only. The Python scripts run from the repository root. Those that read images need
numpy and Pillow.

## Checks

These live in `tools/checks`. The headers of the PowerShell ones still say `tools\checks\check_*.ps1`,
which is the old path. [Build and check everything](howto-build-and-check.md) lists the order.

| Script | What it is for | Game or RenderDoc |
|---|---|---|
| `tools/checks/check_common.ps1` | Shared by the check scripts: finds the repository and the game, resolves KSP's assemblies. | Neither, but the game folder |
| `tools/checks/check_bundled_mods.ps1` | Reads every registration and builds each mod with the built DLL, then reports the members, rows and defaults it found. | Installed mods, no running game |
| `tools/checks/check_profile_parser.ps1` | Feeds the profile reader nodes with one mistake of each kind and checks what it makes of them. | Neither, but KSP's DLLs |
| `tools/checks/check_key_bindings.ps1` | Checks the key bindings from the mods, the registrations and KSP's `GameSettings`. | Neither, but KSP's DLLs |
| `tools/checks/check_foreign_code.ps1` | Checks what ReDefinition assumes about other mods' code against the installed mods. | Installed mods, no running game |
| `tools/checks/check_docs.ps1` | Checks the pages under `docs/`: links, paths and style. | Neither |
| `tools/checks/audit_ffx_fields.py` | Checks that every field of the FidelityFX frame generation descriptors is set or listed with a reason. | Neither |
| `tools/tests/test_benchmark_matrix.py` | Tests the log parser and the matrix of `benchmark_matrix.py` on log excerpts. Run `python -m unittest`. | Neither |
| `tools/DocLock` | The comment check: documented, dirty and unlocked elements, references and lint. | Neither |

## Build and package

| Script | What it is for | Game or RenderDoc |
|---|---|---|
| `tools/port_fsr3_shaders.py` | Ports the FSR3Unity shaders so they compile with Unity 2019.4.18f1. | Neither, but an FSR3Unity checkout |
| `tools/fetch_amd_runtime.py` | Downloads AMD's FidelityFX runtimes for the player package and checks their SHA-256. | Neither, but the network |
| `tools/make_icon.py` | Draws the toolbar icon, the flight panel's tabs, the planner's symbols and the forum picture. | Neither |
| `tools/build_rt_player.py` | Builds the raytracing test player with Unity and runs its checks. | Neither, but Unity and the proxy |
| `tools/icuewidget` | Validates the dashboard folder as an iCUE widget with `icuewidget-cli`. | Neither, but Node |
| `tools/Refactor` | Restructures the repository from a manifest of moves and renames, with `git mv`. | Neither |

## In-game launchers and probes

| Script | What it is for | Game or RenderDoc |
|---|---|---|
| `tools/benchmark_matrix.py` | Runs the standing comparison of upscalers, frame generation, raytracing and latency models, and writes `docs/development/benchmarks.md`. | Game |
| `tools/benchmark_matrix.json` | The matrix that `benchmark_matrix.py` runs. | Game, through the script |
| `tools/hud_probe.py` | Measures what DLSS frame generation does to the HUD, one KSP start per case. | Game |
| `tools/flip_probe.ps1` | Records two camera sweeps, with and without frame generation, for the FLIP comparison. | Game |
| `tools/vendor_probe.ps1` | Runs the vendors' own debug checks of the upscalers and frame generation in the game. | Game, and NVIDIA's development DLLs |
| `tools/ngx_debug_on.reg` | Turns on NVIDIA NGX's on-screen debug output for `vendor_probe.ps1`. | Registry only |
| `tools/ngx_debug_off.reg` | Undoes `ngx_debug_on.reg`. | Registry only |
| `tools/unity_profile.ps1` | Runs KSP once with Unity's development player so Unity's profiler records. | Game, and the Unity editor's player |
| `tools/renderdoc_probe.ps1` | Starts KSP under RenderDoc with the scene probe and captures frames with and without the vessel. | Game and RenderDoc |
| `tools/replay_fg.ps1` | Plays an input dump through frame generation in the proxy harness and measures the result. | Neither, but a GPU and Streamline |

## Capture analysis

| Script | What it is for | Game or RenderDoc |
|---|---|---|
| `tools/analyze_fg_inputs.py` | Checks frame generation's inputs from an input dump against each other. | Neither, files from the game |
| `tools/analyze_flip.py` | Matches frame generation's frames to rendered ones with NVIDIA's FLIP. | Neither, recordings from `flip_probe.ps1` |
| `tools/analyze_probe.py` | Measures what the vessel does to the sky around it in a scene probe run. | Neither, files from the game |
| `tools/analyze_rt_turn.py` | Measures the test player's camera turn against its reference poses. | Neither, files from the player |
| `tools/analyze_upscale_turn.py` | Measures the same turn through each upscaler. | Neither, files from the player |
| `tools/analyze_renderdoc_pair.py` | Finds the stage of a frame that changes the sky beside the vessel, from two captures. | Neither, output of `renderdoc_stages.py` |
| `tools/renderdoc_stages.py` | Saves the scene colour after each stage of a capture. Runs in RenderDoc's Python. | RenderDoc |
| `tools/renderdoc_gpu_times.py` | Sums the GPU time of a capture by marker, shader, target and mod. Runs in RenderDoc's Python. | RenderDoc |
| `tools/cpu_samples.py` | Names where the main thread spends its time, from the CPU sampler's file. | Neither, a file from the game |
| `tools/measure_fg_turn.py` | Measures frame generation on the test player's camera turn. | Neither, a replay recording |
| `tools/replay_measure.py` | Cuts a replay recording to the replay window and names each frame. | Neither, a replay recording |
| `tools/turn_to_fg_dump.py` | Writes frames of the camera turn as frame generation's input dump. | Neither, files from the player |
| `tools/fg_dump.py` | The reader and writer of the input dump's format, used by the scripts above. | Neither |

## One-off investigations

| Script | What it is for | Game or RenderDoc |
|---|---|---|
| `tools/sea_reflection_check.py` | Compares the sun's and the moons' reflections on the sea against a physical model. | Neither, files from the light probe |
| `tools/surface_sheet.py` | Draws the probe's surface images as sheets and tables per region. | Neither, files from the probe |
| `tools/side_by_side.py` | Puts shots of the same names from two light probe runs side by side. | Neither, files from the probe |
| `tools/craft_stages.py` | Works out the stages of stock crafts from their files, as C# for the planner's tests. | Neither, but the KSP folder |
| `tools/export_rt_scene.py` | Exports the launch pad and the SPH meshes from the game's files for the raytracing harness. | Neither, but the KSP folder |
| `tools/make_fg_test_dump.py` | Writes a synthetic input dump with known motion for the harness replay. | Neither |
| `tools/make_bar_dump.py` | Writes a synthetic input dump of a still bar and pole before a moving sky. | Neither |

## Scripts that look obsolete

Nothing here was deleted. A script is listed when no source, page or script outside the
`.doclock` files, the review log and the changelog names it.

* `tools/make_bar_dump.py` is named nowhere. Its sibling `make_fg_test_dump.py` is in
  [Building and testing](building-and-testing.md).
* `tools/side_by_side.py` is named nowhere.
* `tools/Refactor` is named only by its own files. It served the restructuring of the
  repository.
* `tools/vendor_probe.ps1` is named only by `tools/ngx_debug_on.reg`. No page describes it.
* `tools/measure_fg_turn.py` is named only by [building and testing](building-and-testing.md).
