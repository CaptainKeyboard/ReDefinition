# ReDefinition documentation

**For:** players, mod authors and anyone working on ReDefinition.
**You need:** nothing. Each page says what it needs.
**You get:** the page for what you are doing right now.

ReDefinition does two things. It puts the graphics settings of many mods, the graphics
profiles and every key binding into one window. And it adds upscaling and frame
generation: FSR 3 inside the game, DLSS and AMD's upscaler DLL in a Direct3D 12 proxy
beside it.

## For players

| You want to | Page |
|---|---|
| install it, or take it out again | [player/installing.md](player/installing.md) |
| use the settings window, its tabs, *Accept* and *Reset* | [player/settings-window.md](player/settings-window.md) |
| choose a graphics profile and see what it sets | [player/graphics-profiles.md](player/graphics-profiles.md) |
| set up upscaling or frame generation, and know what they cost | [player/upscaler-and-frame-generation.md](player/upscaler-and-frame-generation.md) |

## For mod and visual pack authors

| You want to | Page |
|---|---|
| get your first setting from your mod into the window | [modders/getting-started.md](modders/getting-started.md) |
| put your settings, profile values and key bindings into the window | [modders/registering-a-mod.md](modders/registering-a-mod.md) |
| look up a key of the registration format | [modders/registration-reference.md](modders/registration-reference.md) |
| read the frame's state, add motion vectors, draw after the upscaler, follow the chosen profile, ask for your key, run a compute pass | [modders/shared-foundation.md](modders/shared-foundation.md) |

## Looking something up

| You want to | Page |
|---|---|
| the default of a bundled mod's setting, with its source | [reference/mod-defaults.md](reference/mod-defaults.md) |
| what an installed mod requires of other settings | [reference/requirements.md](reference/requirements.md) |
| where each bundled mod keeps its settings | [reference/how-each-mod-keeps-its-settings.md](reference/how-each-mod-keeps-its-settings.md) |
| what a graphics mod does to the frame, and how it meets the upscaler | [reference/graphics-mod-compatibility.md](reference/graphics-mod-compatibility.md) |
| a word this documentation uses | [glossary.md](glossary.md) |

## Working on ReDefinition

| You want to | Page |
|---|---|
| find your way around the code | [development/architecture.md](development/architecture.md) |
| build it, run the tests and the checks | [development/building-and-testing.md](development/building-and-testing.md) |
| build the release package | [development/packaging.md](development/packaging.md) |
| understand the upscaler | [development/upscaler.md](development/upscaler.md) |
| understand frame generation | [development/frame-generation.md](development/frame-generation.md) |
| understand the settings store | [development/settings-store.md](development/settings-store.md) |
| understand the interface for mods | [development/shared-foundation.md](development/shared-foundation.md) |
| write or change a page here | [development/writing-these-pages.md](development/writing-these-pages.md) |
| see what is done, open or settled | [project/status.md](project/status.md), [project/reviews.md](project/reviews.md) |

ReDefinition builds on other people's work and talks to their mods.
[credits-and-licences.md](credits-and-licences.md) names everyone, with the licence of
that work.

## The folders

Each page serves one need: getting started, how to, reference, or explanation
([development/writing-these-pages.md](development/writing-these-pages.md)). The folders
follow the reader:

| Folder | For |
|---|---|
| `player/` | players |
| `modders/` | mod and visual pack authors, with the examples in `modders/examples/` |
| `reference/` | facts about the bundled mods, with their sources |
| `development/` | working on ReDefinition itself |
| `project/` | what is done, and how changes are reviewed |

Code comments point here where a reason is longer than a comment should be, by naming
the page in brackets: `What the store keeps (docs/development/architecture.md)`.
