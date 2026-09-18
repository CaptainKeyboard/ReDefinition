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
| In flight, click the search field and type `wasd` | the letters appear; the vessel does not move |
| Press Escape in the search field | the field lets go; KSP's pause menu does not open |
| Open *Mods* and *EVA* | ReDefinition's, Scatterer's and Deferred's keys under *Mods*; *B* under *EVA* is not yellow, though *Brakes* under *Flight* is *B* too |
| Click *Upscaler on or off*, press `P` | the row says it is listening, then shows `P`; its *Ctrl* and *Shift* switches keep what they held |
| Click *Alt* beside it twice | *L Alt*, then *R Alt*; `AltGr` + `P` in flight then switches the upscaler |
| Click a row and press Escape | the binding stays as it was |
| Click *x* beside a row | the row shows *None*, and that key does nothing afterwards |
| Click *Alt* a third time | the switch shows *Alt* again, without the modifier |
| Open the *Keys* tab and drag the window | it moves as smoothly as with the other tabs |
| Click *KSP: Flight*, then drag the window | the group opens; the window still moves smoothly |
| Set two rows to the same combination | both are shown in yellow, and both still work |
| Set KSP's *Pitch down* to `K`, *Accept*, launch | the vessel pitches on `K` |
| Bind a row to `Space` in flight, then press the row and `Space` again | nothing stages while the row takes the key |
| *Reset to defaults*, *Accept* | every binding stands at its default again, KSP's included (*Pitch down* is `W`, *Launch stages* `Space`) |

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
