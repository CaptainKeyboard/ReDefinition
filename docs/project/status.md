# Status

**For:** anyone working on ReDefinition.
**You need:** nothing.
**You get:** what exists, what is still open, and which decisions are settled.

Released: 0.1.3.

## What exists

| Part | State |
|---|---|
| **Upscaler** -- FSR 3 in Unity, DLSS and AMD's upscaler DLL in the proxy; six modes, *AA only* in every profile; the DLSS model per profile; sharpness; the transparency and reactive masks for FSR 3 and AMD's upscaler; Reflex and its latency markers; EVE's clouds jittered and with their own motion vectors; TUFX's effects after the upscaler; skinned motion vectors | built; DLSS passes the harness |
| **Flight HUD** -- the altimeter that switches to the ground, vertical and horizontal speed beside the navball, time to impact, the round gauge, the flight panel's profiles in KSP's maneuver mode with their editor, the docking marker on the navball, Better Time Warp's button in the time panel, crew slot buttons, a switch for each | built, unit tests |
| **Staging and the editors** -- stage details in two columns, the delta-v tools inside the staging stack, the editor's launch buttons, the buttons back to the VAB or the SPH and over the space centre's buildings, grip for landing legs | built, unit tests |
| **Planner in the VAB and the SPH** -- a mode with a route of stops A → B → C, each leg's delta-v from the loaded bodies, the fuel plan of stages by legs with pins, a lander used more than once, the vessel's parts in the colours of their stages, one plan per vessel | built, unit tests; its first form was seen in the game and rebuilt |
| **Navball moved sideways** -- by dragging its show/hide button, with the SAS switches and gauges; its position in the settings window | built, unit tests |
| **UI scaling** -- the UI scale for what KSP leaves at a fixed size, R&D nodes and part tiles, other mods' windows, other mods' scaling overridden; the settings window and its dialogs fit the screen | built, unit tests |
| **Main menu and space centre** -- fireworks behind the main menu, the camera flight down to the space centre, Parallax's grass on the lawns, the runway lights dimmed, KSP's reflection probe kept on, Firefly skipped while it shows nothing | built |
| **Other mods' antialiasing** -- Scatterer's TAA and SMAA with their leftovers, TUFX's antialiasing, Deferred's editor SMAA, Kerbal Frame Generator's blend, while the upscaler runs | built |
| **Proxy** -- Direct3D 12 presentation, FSR 3.1 frame generation, DLSS frame generation through Streamline 2.14.1, HUD-less copy and its check, V-Sync as NVIDIA's guide allows, half-refresh limit, a minimum rendered frame rate, off in KSP's pause menu; the ini written from a template in the proxy, completed with new keys, its untouched defaults following a newer version's, live keys; the notice when Windows refused the proxy | built; harness passes with FSR and with DLSS frame generation; `tools/audit_ffx_fields.py` passes |
| **Frame generation without the upscaler** | built; harness passes |
| **Raytracing, stages 1 to 7** -- the service in the proxy: meshes, materials, material textures, instances from the CPU and from the GPU, acceleration structures; sun visibility, ambient occlusion, diffuse light with bounces, reflections, emission, the sky; ReDefinition's own denoiser; DLSS Ray Reconstruction through Streamline in the upscaler; AMD FSR Ray Regeneration where AMD's DLL runs; the Unity side: scene, camera, capture, the lighting shaders; KSP's parts, terrain, KSC with its flags and Parallax's scatter in the scene, the settings and their log line; moonlight, the moons' glow, the sun and the moons on the sea, EVE's clouds in the rays, the light colour; with Deferred or without it; every installed mod handled; the raytracing harness, a scene of KSP's meshes, the raytracing test player with a budget run | built; the raytracing harness and the test player pass on an RTX 4090; run in the game, [development/performance-findings.md](../development/performance-findings.md) |
| **NVIDIA DLSS files** -- download from NVIDIA's Streamline release after licence consent, SHA-256 pinned | built, unit tests |
| **KSP settings dialog section** | built |
| **Profiling** -- the GPU profiler, the perf monitor for every assembly and every coroutine, the mod cost probe, RenderDoc by mod, Unity's development player, the proxy's wait report | built; run in the game, [development/performance-findings.md](../development/performance-findings.md) |
| **Settings window and diagnostics window** -- tabs by feature, *Advanced*, toolbar takeover, close buttons on mods' windows | built |
| **Saving in the mods, values from before ReDefinition, restore** | built, unit tests |
| **Graphics profiles** Low to Max, **Reset**, **requirements** R1-R7 | built, checked against the installed mods |
| **Registrations** -- KSP and eight mods, the worked example (Trajectories), template, diagnostics list | built, unit tests, checked against the installed mods |
| **Other mods' code** -- a catalogue of what ReDefinition assumes about other mods' code, with fingerprints of their methods and fallbacks; `tools/check_foreign_code.ps1` after a mod's update | built, unit tests |
| **Release package** -- zip with licences and notices, source zip, AMD runtime pinned | built |
| **Interface for mods** -- the frame's state as properties and shader globals, history resets from ReDefinition and from mods, hooks for motion vectors, the upscaled image and overlays, the chosen profile, Direct3D 12 compute passes from HLSL or bytecode; the wrapper, the shader include, the example mod, the XML documentation | built; unit tests; the harness runs compute passes compiled by the proxy and from DXIL; the include compiles in Unity 2019.4.18f1 |

## Open

| Area | Question | What settles it |
|---|---|---|
| Profiles | Whether the tiers land where they are meant to on real GPUs | frame times per tier at 1440p and 4K |
| Profiles | What KSP's render quality levels 4 and 5 set | a log line per level at the main menu |
| Profiles | Steps for EVE's light volume, Scatterer's light shaft steps, Parallax's fade-out | their authors' cost guidance |
| Upscaler | Whether the masks take out ghosting; floating origin shifts and motion vectors; camera cuts that keep target and parent | [development/upscaler.md](../development/upscaler.md), "Open" |
| Frame generation | The fence's and the check's cost; pacing per profile; DLSS frame generation in flight | [development/frame-generation.md](../development/frame-generation.md), "Open" |
| Interface for mods | The hooks and the frame's state in the game; mask contributions | [development/shared-foundation.md](../development/shared-foundation.md) |
| Raytracing | AMD FSR Ray Regeneration on a Radeon RX 9000 | [development/raytracing.md](../development/raytracing.md) |
| Performance | The improvements that cost image quality, the player's choice; the shadow maps in other scenes than the launch pad | [development/performance-findings.md](../development/performance-findings.md), "What is left" |
| Compatibility | Ghosting behind plumes; thin trajectory lines; Singularity in the smaller modes; Kerbal Frame Generator installed | [reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md), "Open" |
| Modders | Whether a mod's own behaviour type covers what the mods ReDefinition bundles needed | a third-party mod that brings one |

## Settled

| Decision | |
|---|---|
| ReDefinition is active only while a graphics profile is chosen; *Reset* chooses High | choosing High after a reset changes nothing |
| The other mods' antialiasing is switched off at run time while the upscaler runs, never in their settings | without the upscaler their antialiasing is the only one there is |
| *AA only* in every profile | upscaling buys no frame rate on the development machine ([development/upscaler.md](../development/upscaler.md)) |
| A profile sets quality, and a mod's taste only where that mod needs it changed to run with the upscaler; High is the mod authors' defaults; *Reset* resets everything | [player/graphics-profiles.md](../player/graphics-profiles.md) |
| A requirement of an installed mod is enforced whatever profile is chosen | [reference/requirements.md](../reference/requirements.md) |
| Settings are saved in each mod through its own routine, with the values from before kept for a restore | [development/settings-store.md](../development/settings-store.md) |
| Mods register through config nodes in their own folders; a setting no member path reaches goes through a behaviour, one ReDefinition brings or one the mod itself brings | [modders/registering-a-mod.md](../modders/registering-a-mod.md) |
| Frame generation's V-Sync follows KSP's own setting; with DLSS frame generation only the intervals NVIDIA supports | [development/frame-generation.md](../development/frame-generation.md) |
| The player package holds the proxy and AMD's frame generation runtime; the player downloads NVIDIA's DLLs from NVIDIA's release after accepting NVIDIA's licences | [development/packaging.md](../development/packaging.md) |
| Harmony is required | `src/KspAssemblyInfo.cs` |
| Licence GPL-3.0-or-later with the Modding and Linking Exceptions | `LICENSE`, `EXCEPTIONS.md` |
| ReDefinition keeps no migration for its own stored formats: a format changes in place | |
