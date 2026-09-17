# What the next session has to show

What to do in the game, and what to see. ReDefinition writes what it needs to know into
`KSP.log` and `ReDefinitionProxy.log` on its own; after the session those two files are
read, nothing has to be looked up in them during play.

## Main menu

| Do | See |
|---|---|
| Open ReDefinition's window with its toolbar button | the *Profiles* tab with Low to Max |
| On an NVIDIA RTX GPU without NVIDIA's files: *General*, *NVIDIA DLSS files*, *Download ...* | a dialog with NVIDIA's licences and *Accept and download*; after it, a grey line under the row saying the files are in place |
| Choose *Ultra*, *Accept* | no error message |

## Space centre

| Do | See |
|---|---|
| Click a building | it opens |
| Pause menu, *Settings* | a *ReDefinition* section at the end of the graphics part |

## Keys

| Do | See |
|---|---|
| Open the *Keys* tab | ReDefinition's four bindings, the installed mods' and KSP's own in groups |
| Type "camera" into the search field | only the rows whose name or mod holds it |
| Click *Upscaler on or off*, press `RightAlt` + `P` | the row says it is listening, then shows `RightAlt+P`; pressing it in flight switches the upscaler |
| Click a row and press Escape | the binding stays as it was |
| Click *x* beside a row | the row shows *None*, and that key does nothing afterwards |
| Click *Alt* beside a bound row | the label turns yellow and the binding gains `LeftAlt` |
| Set two rows to the same combination | both are shown in yellow, and both still work |
| Set KSP's *Pitch down* to `K`, *Accept*, launch | the vessel pitches on `K` |
| Bind a row to `Space` in flight, then press the row and `Space` again | nothing stages while the row takes the key |
| *Reset to defaults*, *Accept* | ReDefinition's bindings and the mods' stand at their defaults again; KSP's are untouched |

## Flight

Play normally for a few minutes: launch, staging, the map and back, IVA and back, a
vessel switch with `]`.

| Do | See |
|---|---|
| Fly with the settings as chosen | a steady picture; no ghost of the previous view after IVA, the map or a vessel switch |
| Turn the camera quickly under clouds | cloud edges without a trail |
| Look at engine plumes against the sky | no dark or smeared halo around them |
| With DLSS frame generation on: *General*, *V-Sync* | *Every second refresh* is not offered |
| Open *Diagnostics* | the frame rate line shows a second number in brackets while frame generation runs |

## VAB

| Do | See |
|---|---|
| Pick up a part and attach it | the part sits under the cursor |
| Look at thin parts such as antennas | smooth edges |

After the session: quit KSP normally, so both logs are complete.
