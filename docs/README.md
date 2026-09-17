# ReDefinition documentation

| You want to | Read |
|---|---|
| install ReDefinition, or take it out again | [player/installing.md](player/installing.md) |
| use the settings window, the reset and the other mods' windows | [player/settings-window.md](player/settings-window.md) |
| choose a graphics profile, and know what each one sets | [player/graphics-profiles.md](player/graphics-profiles.md) |
| know what the upscaler and frame generation do, and what they cost | [player/upscaler-and-frame-generation.md](player/upscaler-and-frame-generation.md) |
| register your mod, or change a mod's values for a visual pack | [modders/registering-a-mod.md](modders/registering-a-mod.md) and [modders/examples](modders/examples) |
| use ReDefinition's jitter, history resets, hooks or Direct3D 12 from your mod | [modders/shared-foundation.md](modders/shared-foundation.md) |
| look up a mod's defaults, what the mods require, how each mod keeps its settings, or what a graphics mod does to the frame | [reference](reference) |
| build, test or package ReDefinition | [development/building-and-testing.md](development/building-and-testing.md), [development/packaging.md](development/packaging.md) |
| find your way around the code | [development/architecture.md](development/architecture.md) |
| know how the upscaler, frame generation, the settings store and the interface for mods work | [development/upscaler.md](development/upscaler.md), [development/frame-generation.md](development/frame-generation.md), [development/settings-store.md](development/settings-store.md), [development/shared-foundation.md](development/shared-foundation.md) |
| try it in the game | [testing/next-flight.md](testing/next-flight.md) |
| know what is done and what is open | [project/status.md](project/status.md), [project/reviews.md](project/reviews.md) |

Everyone whose work ReDefinition builds on or talks to, with the licence of that
work: [credits-and-licences.md](credits-and-licences.md).

## The layout

```
docs/
  README.md                          this page
  credits-and-licences.md
  player/                            for players
    installing.md
    settings-window.md
    graphics-profiles.md
    upscaler-and-frame-generation.md
  modders/                           for mod and visual pack authors
    registering-a-mod.md
    shared-foundation.md             the interface for mods
    examples/                        a worked example, a template, the interface's wrapper, an example mod
  reference/                         facts per mod, with their sources
    mod-defaults.md
    requirements.md
    how-each-mod-keeps-its-settings.md
    graphics-mod-compatibility.md
  development/                       for working on ReDefinition
    architecture.md
    building-and-testing.md
    packaging.md
    upscaler.md
    frame-generation.md
    settings-store.md
    shared-foundation.md
  testing/
    next-flight.md                   what to do and see in the next game session
  project/
    status.md                        what exists, what is open, what is settled
    reviews.md                       how changes are reviewed, and the review log
```

Code comments point to these pages where a reason is longer than a comment
should be: `See docs/development/settings-store.md, "Values from before"`.
