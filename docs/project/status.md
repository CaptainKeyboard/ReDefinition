# Status

Before the first release.

## What exists

| Part | State |
|---|---|
| **Upscaler** -- FSR 3 in Unity, DLSS and AMD's upscaler DLL in the proxy; six modes, *AA only* in every profile; sharpness; DLSS presets; FSR 3's transparency and reactive masks; EVE's clouds jittered and with their own motion vectors; TUFX's effects after the upscaler; skinned motion vectors | built; DLSS passes the harness |
| **Other mods' antialiasing** -- Scatterer's TAA and SMAA with their leftovers, TUFX's antialiasing, Deferred's editor SMAA, Kerbal Frame Generator's blend, while a profile is chosen | built |
| **Proxy** -- Direct3D 12 presentation, FSR 3.1 frame generation, DLSS frame generation through Streamline 2.14.1, HUD-less copy and its check, V-Sync as NVIDIA's guide allows, half-refresh limit, live ini | built; harness passes with FSR and with DLSS frame generation; `tools/audit_ffx_fields.py` passes |
| **Frame generation without the upscaler** | built; harness passes |
| **NVIDIA DLSS files** -- download from NVIDIA's Streamline release after licence consent, SHA-256 pinned | built, unit tests |
| **KSP settings dialog section** | built |
| **Settings window and diagnostics window** -- tabs by feature, *Advanced*, toolbar takeover, close buttons on mods' windows | built |
| **Saving in the mods, values from before ReDefinition, restore** | built, unit tests |
| **Graphics profiles** Low to Max, **Reset to defaults**, **requirements** R1-R7 | built, checked against the installed mods |
| **Registrations** -- KSP and eight mods, the worked example (Trajectories), template, diagnostics list | built, unit tests, checked against the installed mods |
| **Release package** -- zip with licences and notices, source zip, AMD runtime pinned | built |
| **Interface for mods** -- the frame's state as properties and shader globals, history resets from ReDefinition and from mods, hooks for motion vectors, the upscaled image and overlays, the chosen profile, Direct3D 12 compute passes from HLSL or bytecode; the wrapper, the shader include, the example mod, the XML documentation | built; unit tests; the harness runs compute passes compiled by the proxy and from DXIL; the include compiles in Unity 2019.4.18f1 |

What to try in the game, and what counts as right:
[testing/next-flight.md](../testing/next-flight.md).

## Open

| Area | Question | What settles it |
|---|---|---|
| Game | The parts above in flight, in the editors and at the space centre | [testing/next-flight.md](../testing/next-flight.md) |
| Profiles | Whether the tiers land where they are meant to on real GPUs | frame times per tier at 1440p and 4K |
| Profiles | What KSP's render quality levels 4 and 5 set | a log line per level at the main menu |
| Profiles | Steps for EVE's light volume, Scatterer's light shaft steps, Parallax's fade-out | their authors' cost guidance |
| Upscaler | Whether the masks take out ghosting; floating origin shifts and motion vectors; camera cuts that keep target and parent; world-anchored UI in smaller modes | [development/upscaler.md](../development/upscaler.md), "Open" |
| Frame generation | The fence's and the check's cost; pacing per profile; DLSS frame generation in flight | [development/frame-generation.md](../development/frame-generation.md), "Open" |
| Interface for mods | The hooks and the frame's state in the game; raytracing and mask contributions | [development/shared-foundation.md](../development/shared-foundation.md) |
| Compatibility | Ghosting behind plumes; thin trajectory lines; Singularity in the smaller modes; Kerbal Frame Generator installed | [reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md), "Open" |
| Modders | Settings that need code of their own can be bundled only through a behaviour in ReDefinition; choices read from a mod's own list need one too | a mod that needs it |
| Release | The version file's `URL` and `DOWNLOAD` for the update check | [development/packaging.md](../development/packaging.md) |

## Settled

| Decision | |
|---|---|
| ReDefinition is active only while a graphics profile is chosen; *Reset to defaults* leaves no profile chosen | the upscaler and the other mods' antialiasing change together |
| *AA only* in every profile | upscaling buys no frame rate on the development machine ([development/upscaler.md](../development/upscaler.md)) |
| A profile sets quality, never a mod's taste; High is the mod authors' defaults, for an RTX 3080/4080 at 1440p; *Reset to defaults* resets everything | [player/graphics-profiles.md](../player/graphics-profiles.md) |
| A requirement of an installed mod is enforced whatever profile is chosen | [reference/requirements.md](../reference/requirements.md) |
| Settings are saved in each mod through its own routine, with the values from before kept for a restore | [development/settings-store.md](../development/settings-store.md) |
| Mods register through config nodes in their own folders; code for a mod is a behaviour in ReDefinition | [modders/registering-a-mod.md](../modders/registering-a-mod.md) |
| Frame generation's V-Sync follows KSP's own setting; with DLSS frame generation only the intervals NVIDIA supports | [development/frame-generation.md](../development/frame-generation.md) |
| The player package holds the proxy and AMD's frame generation runtime; the player downloads NVIDIA's DLLs from NVIDIA's release after accepting NVIDIA's licences | [development/packaging.md](../development/packaging.md) |
| Harmony is required | `src/KspAssemblyInfo.cs` |
| Licence GPL-3.0-or-later with the Modding and Linking Exceptions | `LICENSE`, `EXCEPTIONS.md` |
| No compatibility with earlier builds of ReDefinition itself before the first release | |
