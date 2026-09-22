# Status

**For:** anyone working on ReDefinition.
**You need:** nothing.
**You get:** what exists, what is still open, and which decisions are settled.

Released: 0.1.2.

## What exists

| Part | State |
|---|---|
| **Upscaler** -- FSR 3 in Unity, DLSS and AMD's upscaler DLL in the proxy; six modes, *AA only* in every profile; sharpness; FSR 3's transparency and reactive masks; EVE's clouds jittered and with their own motion vectors; TUFX's effects after the upscaler; skinned motion vectors | built; DLSS passes the harness |
| **Other mods' antialiasing** -- Scatterer's TAA and SMAA with their leftovers, TUFX's antialiasing, Deferred's editor SMAA, Kerbal Frame Generator's blend, while a profile is chosen | built |
| **Proxy** -- Direct3D 12 presentation, FSR 3.1 frame generation, DLSS frame generation through Streamline 2.14.1, HUD-less copy and its check, V-Sync as NVIDIA's guide allows, half-refresh limit, live ini | built; harness passes with FSR and with DLSS frame generation; `tools/audit_ffx_fields.py` passes |
| **Frame generation without the upscaler** | built; harness passes |
| **NVIDIA DLSS files** -- download from NVIDIA's Streamline release after licence consent, SHA-256 pinned | built, unit tests |
| **KSP settings dialog section** | built |
| **Settings window and diagnostics window** -- tabs by feature, *Advanced*, toolbar takeover, close buttons on mods' windows | built |
| **Saving in the mods, values from before ReDefinition, restore** | built, unit tests |
| **Graphics profiles** Low to Max, **Reset**, **requirements** R1-R7 | built, checked against the installed mods |
| **Registrations** -- KSP and eight mods, the worked example (Trajectories), template, diagnostics list | built, unit tests, checked against the installed mods |
| **Release package** -- zip with licences and notices, source zip, AMD runtime pinned | built |
| **Interface for mods** -- the frame's state as properties and shader globals, history resets from ReDefinition and from mods, hooks for motion vectors, the upscaled image and overlays, the chosen profile, Direct3D 12 compute passes from HLSL or bytecode; the wrapper, the shader include, the example mod, the XML documentation | built; unit tests; the harness runs compute passes compiled by the proxy and from DXIL; the include compiles in Unity 2019.4.18f1 |

## Open

| Area | Question | What settles it |
|---|---|---|
| Profiles | Whether the tiers land where they are meant to on real GPUs | frame times per tier at 1440p and 4K |
| Profiles | What KSP's render quality levels 4 and 5 set | a log line per level at the main menu |
| Profiles | Steps for EVE's light volume, Scatterer's light shaft steps, Parallax's fade-out | their authors' cost guidance |
| Upscaler | Whether the masks take out ghosting; floating origin shifts and motion vectors; camera cuts that keep target and parent; world-anchored UI in smaller modes | [development/upscaler.md](../development/upscaler.md), "Open" |
| Frame generation | The fence's and the check's cost; pacing per profile; DLSS frame generation in flight | [development/frame-generation.md](../development/frame-generation.md), "Open" |
| Interface for mods | The hooks and the frame's state in the game; raytracing and mask contributions | [development/shared-foundation.md](../development/shared-foundation.md) |
| Compatibility | Ghosting behind plumes; thin trajectory lines; Singularity in the smaller modes; Kerbal Frame Generator installed | [reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md), "Open" |
| Modders | Whether a mod's own behaviour type covers what the mods ReDefinition bundles needed | a third-party mod that brings one |

## Settled

| Decision | |
|---|---|
| ReDefinition is active only while a graphics profile is chosen; *Reset* leaves no profile chosen | the upscaler and the other mods' antialiasing change together |
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
