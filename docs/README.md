# ReDefinition documentation

**For:** players, mod authors and anyone working on ReDefinition.
**You need:** nothing. Each page says what it needs.
**You get:** the page for what you are doing right now.

ReDefinition puts the settings of KSP and of many mods, the graphics profiles and every
key binding into one window. It adds upscaling, frame generation and raytracing: FSR 3
inside the game, DLSS, AMD's upscaler DLL, XeSS and the raytracing in a Direct3D 12 proxy
beside it. And it adds to the game's interface: the flight HUD, the staging stack, the
editors, the main menu and the space centre.

## For players

| You want to | Page |
|---|---|
| install it, or take it out again | [player/installing.md](player/installing.md) |
| use the settings window, its tabs, *Accept* and *Reset* | [player/settings-window.md](player/settings-window.md) |
| choose a graphics profile and see what it sets | [player/graphics-profiles.md](player/graphics-profiles.md) |
| set up upscaling or frame generation, and know what they cost | [player/upscaler-and-frame-generation.md](player/upscaler-and-frame-generation.md) |
| lower the latency, and see what lowers it on your GPU | [player/low-latency.md](player/low-latency.md) |
| switch on raytraced shadows and light, and know what they need | [player/raytracing.md](player/raytracing.md) |
| plan a mission and check the vessel carries enough fuel | [player/planner.md](player/planner.md) |
| move the navball to the left or right | [player/navball.md](player/navball.md) |
| see the figures for what you are doing in flight | [player/flight-panel.md](player/flight-panel.md) |
| launch from the VAB or the SPH, and go back there from the pad | [player/launch-and-return.md](player/launch-and-return.md) |
| keep a lander from sliding on a slope | [player/leg-grip.md](player/leg-grip.md) |
| use the delta-v tools and the stage details | [player/stage-details.md](player/stage-details.md) |
| read the map, labels and mod windows on a large screen | [player/ui-scaling.md](player/ui-scaling.md) |

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
| a setting of the proxy's ini, with its default | [reference/proxy-settings.md](reference/proxy-settings.md) |
| what a graphics mod does to the frame, and how it meets the upscaler | [reference/graphics-mod-compatibility.md](reference/graphics-mod-compatibility.md) |
| what Parallax puts on screen, and what the rays must know of it | [reference/render-contract-parallax.md](reference/render-contract-parallax.md) |
| what Scatterer puts on screen, and what the rays must know of it | [reference/render-contract-scatterer.md](reference/render-contract-scatterer.md) |
| what EVE puts on screen, and what the rays must know of it | [reference/render-contract-eve.md](reference/render-contract-eve.md) |
| what Deferred puts on screen, and what the rays must know of it | [reference/render-contract-deferred.md](reference/render-contract-deferred.md) |
| what Kopernicus puts on screen, and what the rays must know of it | [reference/render-contract-kopernicus.md](reference/render-contract-kopernicus.md) |
| a word this documentation uses | [glossary.md](glossary.md) |

## Working on ReDefinition

| You want to | Page |
|---|---|
| know where to begin | [development/start-here.md](development/start-here.md) |
| find your way around the code | [development/architecture.md](development/architecture.md) |
| build it, run the tests and the checks | [development/howto-build-and-check.md](development/howto-build-and-check.md) |
| see the build and test details | [development/building-and-testing.md](development/building-and-testing.md) |
| find the script in `tools` for a job | [development/tools-index.md](development/tools-index.md) |
| build the release package | [development/packaging.md](development/packaging.md) |
| follow the startup and what a scene change takes away | [development/flow-startup.md](development/flow-startup.md) |
| follow a frame through the mod | [development/flow-frame.md](development/flow-frame.md) |
| follow a frame through the proxy | [development/flow-proxy-present.md](development/flow-proxy-present.md) |
| add a feature to the proxy | [development/howto-add-a-proxy-feature.md](development/howto-add-a-proxy-feature.md) |
| change what the mod and the proxy tell each other | [development/flow-native-channels.md](development/flow-native-channels.md) |
| understand what lowers the latency, and how it is measured | [src/DxgiProxy/Latency/README.md](../src/DxgiProxy/Latency/README.md) |
| add a setting | [development/howto-add-a-setting.md](development/howto-add-a-setting.md) |
| follow a setting through the store | [development/flow-setting.md](development/flow-setting.md) |
| understand the interface for mods | [development/flow-mod-interface.md](development/flow-mod-interface.md) |
| understand the raytracing | [development/flow-raytracing.md](development/flow-raytracing.md) |
| add or change a shader | [development/flow-shaders.md](development/flow-shaders.md) |
| change a feature on screen | [development/flow-game-ui.md](development/flow-game-ui.md) |
| change the dashboard | [development/flow-dashboard.md](development/flow-dashboard.md) |
| understand the planner and the engineer window | [src/Engineer/README.md](../src/Engineer/README.md) |
| understand the scaling of what KSP leaves unscaled | [src/Scaling/README.md](../src/Scaling/README.md) |
| measure what costs the frame, in ReDefinition, KSP or another mod | [src/Probe/README.md](../src/Probe/README.md) |
| compare the upscalers, frame generation, raytracing and latency models in fps and latency | [development/benchmarks.md](development/benchmarks.md) |
| write a comment in the code | [development/writing-code-comments.md](development/writing-code-comments.md) |
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
| `reference/` | facts about the bundled mods and the proxy's ini, with their sources |
| `development/` | working on ReDefinition itself |
| `project/` | what is done, and how changes are reviewed |

Code comments point here where a reason is longer than a comment should be, by naming
the page in brackets: `What the store keeps (docs/development/architecture.md)`.
