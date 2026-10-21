# Glossary

**For:** anyone reading these pages or the lines ReDefinition writes into `KSP.log`.
**You need:** nothing.
**You get:** the one word this documentation uses for each thing, and what it means.

## The settings window

| Word | Means |
|---|---|
| bundled mod | A mod whose settings ReDefinition shows in its window. A mod is bundled once it is installed and a registration for it was read. |
| registration | The config file that tells ReDefinition about a mod: a `MOD_SETTINGS` node with the mod's settings, defaults, profile values, key bindings and requirements. ReDefinition ships one per bundled mod in `GameData/ReDefinition/Mods`, and a mod can ship its own. |
| setting | One value ReDefinition keeps for a mod, with its control and its default. |
| setting key | The name a setting is kept under: `<mod>.<setting>`, for example `scatterer.oceanFoam`. |
| key, key binding | A keyboard key, with its modifiers, that does something in the game. The *Controls* tab holds them all. |
| row | A setting's line in the window, in one of the tabs. Some settings a profile sets have no row. |
| tab | One page of the window: *Gameplay*, *Audio*, *Display*, *Graphics* with *Detail* under it, *Controls* with *Axes* and *Devices* under it, and *Mods / Toolbar*. The last one is titled *Mods and toolbar (!)* while a mod has settings the window cannot show. |
| control | What a row looks like: a toggle, a slider, a list of choices, a key binding, or a plain value, which is kept but has no row. |
| *Accept*, *Cancel*, *Close* | *Accept* sets what was changed, *Cancel* puts the rows back; neither closes the window. *Close*, shown while nothing is changed, closes it. |
| *Custom* | The status on the *Graphics* tab once a row was changed after a profile was applied. |
| *Advanced* | The row at the end of the *Planets* and *Effects* tabs, with a button for each bundled mod whose own settings window ReDefinition can open. |
| the settings window | ReDefinition's main window, with the tabs, *Reset*, and *Accept* and *Cancel* or *Close*. |
| the diagnostics window | ReDefinition's second window, opened with *Diagnostics*. A strip on top shows what runs, the frame rates, the latency and the load; below are its pages: *Overview*, *Upscaler inputs*, *Frame generation & latency*, *Game settings*, *Capture & log*, *Previews* and *Other mods*. |
| the strip | The top of the diagnostics window: what runs, the rendered and the shown frame rate, a graph of the last frame times, the latency and the load, the search, *Changed* and *Reset debug switches*. |
| debug switch | An option of the diagnostics window for finding out what happens, most of them not saved. *Reset debug switches* puts the ones away from their default back. |
| pinned option | An option of the diagnostics window that the star on its row keeps at the window's foot. |
| member path | Where a value lives, written as in C#: `Deferred.Deferred.settings.useScreenSpaceReflections`. |
| foreign assumption | Something ReDefinition relies on in another mod's code, kept in one catalogue with a fingerprint of the method it reads and what ReDefinition does when the fingerprint no longer matches. `tools/check_foreign_code.ps1` checks them all. |
| behaviour | Code that answers for settings a member path cannot reach. Either one of ReDefinition's own seven, named without a dot (`Tufx`, `Scatterer`, `Eve`, `Ksp`, `DistantObject`, `Firefly`, `ParallaxScatter`), or a type the mod itself brings, named with its namespace. |

## The editor and the flight UI

| Word | Means |
|---|---|
| planner | The mode of the VAB and the SPH that lays out a route from stop to stop, works out the delta-v of each leg and shares the vessel's stages out over the legs. Its button, with *Δv* on it, is next to KSP's mode buttons above the parts list. |
| fuel plan | The planner's table of stages by legs: what each stage gives to each leg. |
| pin | A stage the player sets to pay for a leg, in a cell of the fuel plan. A leg with pins burns its pinned stages only. |
| stop | A place on the planner's route: a body, on its surface or in an orbit at an altitude. |
| leg | The flight from one stop of the planner's route to the next. |
| lander | In the planner, the stages that fly down and back up alone while the rest of the vessel waits, and are then carried on. |
| navball position | How far the navball stands from the bottom centre of the flight screen. Dragging its show/hide button sets it; with another screen resolution it goes back to the centre. |
| flight switch | One of ReDefinition's rows under *Gameplay*, *Flight*, that turns one of its features of the flight HUD, the staging stack or the space centre on or off. |
| world button | A button ReDefinition puts into the world: over the launch pad or the runway in the VAB or the SPH, over the VAB or the SPH while a vessel stands near it, and over the space centre's other buildings before a launch. |
| situation | Where KSP's delta-v numbers in the editor are for: a body, and sea level, an altitude in its atmosphere, or vacuum. KSP's delta-v app sets it, and its staging stack follows it. |

## The store

| Word | Means |
|---|---|
| the store | The part of ReDefinition that hands values to the mods, keeps what a mod has not saved yet, and remembers what each mod held before. |
| `bundled.cfg` | The store's file, in `GameData/ReDefinition/PluginData`. It holds whether the mods are bundled, the chosen profile, which mods the main menu already asked about, the values ReDefinition keeps for the mods, and the values from before ReDefinition. |
| value from before ReDefinition | What a setting held when ReDefinition changed it for the first time. *Restore settings from before ReDefinition* puts it back. Only a setting ReDefinition has changed has one. |
| requirement | A value an installed mod needs of another mod's setting. ReDefinition keeps it that way whatever a profile or the player sets, and says why. |

How a value lasts is the mod's `saving`:

| Saving | Means |
|---|---|
| `InModFiles` | The mod saves the value in its own files. |
| `PerSave` | The mod keeps the value per save. The player's choice counts for the game, and ReDefinition sets it into every save as that save loads. |
| `AtEveryStart` | The mod keeps nothing. ReDefinition keeps the value and sets it at every start and scene change. |

## Profiles

| Word | Means |
|---|---|
| graphics profile, tier | One of the five steps from Low to Max. It sets the quality settings of every bundled mod and KSP's own graphics at once. High is every mod as its authors ship it. |
| quality setting | A setting that costs frame time. A profile sets only these. |
| taste setting | A setting that decides how the game looks rather than what it costs. No profile changes one, except where a mod needs it changed to run with the upscaler. |
| *Reset* | Sets every setting to the default of the installed build, every key binding to its default, KSP's settings and bindings to KSP's own except ReDefinition's recommended values for KSP, and chooses High over them. It is not the same as *Restore settings from before ReDefinition*. |

## Rendering

| Word | Means |
|---|---|
| the proxy | ReDefinition's `dxgi.dll`, next to `KSP_x64.exe`. It presents the game's frames through Direct3D 12, runs DLSS, AMD's upscaler DLL, XeSS and frame generation, and runs the compute passes other mods ask for. |
| `ReDefinitionProxy.ini` | The proxy's settings, next to `KSP_x64.exe`. The proxy writes it from its own template at the first start and adds the keys a newer version brings. |
| `ReDefinitionProxy.defaults` | The proxy's record of the values it wrote into `ReDefinitionProxy.ini` itself. A value still as written follows a newer version's default; a value the player changed stays. |
| the rig | The upscaler on one scene's camera stack: its render targets, the jitter, the captures and the dispatch. |
| the presenter | The camera behind the 3D stack that draws the upscaled image into the frame buffer, before the interface. |
| technique | Which upscaler runs: *FSR 3*, *DLSS*, *AMD FSR (DLL)* or *XeSS (DLL)*. |
| mode | How much smaller the scene renders, from *AA only* at full resolution to the smallest factor. |
| upscaling | Rendering at a lower resolution and reconstructing the full-resolution image from the game's depth and motion vectors. |
| *AA only* | Rendering at the full resolution and using the upscaler as antialiasing alone. |
| frame generation | Putting generated frames between the rendered ones, in the proxy. FSR 3, DLSS (DLSS-G) and XeSS (XeSS-FG) do it. |
| multiplier | The frames shown for each rendered frame: *2x* to *6x*, or *Dynamic*, where DLSS frame generation chooses it to reach the display's refresh rate. |
| DLSS-G | NVIDIA's DLSS frame generation, through Streamline. dlssg_for_sm86 runs it on RTX 30 and RTX 20. |
| Streamline | NVIDIA's SDK that hosts DLSS frame generation, Reflex and, beside it, DLSS Ray Reconstruction. The proxy loads version 2.14.16 only. |
| XeSS-FG | Intel's XeSS frame generation. It brings its own swapchain and requires XeLL. |
| low latency | What shortens the time from an input to the screen: NVIDIA Reflex, Intel XeLL, AMD Anti-Lag 2 or the latency brake. Only one of them works at a time. |
| the latency brake | The proxy's wait at the start of a frame, in place of the waits that otherwise queue frames in the proxy. It works on any GPU. |
| Ray Reconstruction | NVIDIA's DLSS Ray Reconstruction: the denoiser and upscaler for raytraced frames, in place of DLSS Super Resolution. It runs through Streamline or through NGX. |
| radiance cache | A hash grid over the world that holds the indirect light the rays found, so that a ray's path can end in it (`rtRadianceCache`). |
| opacity micromap | A per-triangle map of the transparent and opaque parts of a cut-out surface, which lets the GPU decide a ray's hit on grass without the shader (`rtOpacityMicromaps`). |
| RTX Mega Geometry | NVIDIA's cluster acceleration structures, which build many small meshes' structures at once (`rtMegaGeometry`). |
| jitter | The sub-pixel shift of the camera each frame that the upscaler needs. |
| motion vectors | How far each pixel moved since the last frame. The upscaler needs them for everything that moves. |
| history reset | The signal that the upscaler has to forget its previous frames. A camera cut causes one, and so does a mod that asks for one. |
| HUD-less copy | The frame without the interface, which frame generation needs so the interface is not warped into generated frames. |
| hook | A place where a mod takes part in ReDefinition's rendering: motion vectors into the capture, drawing after upscaling, or an overlay. |
| surface intent | How a surface is meant to shine for the rays, worked out from its material: for example dry, with KSP's legacy highlight, or water ([development/raytracing.md](development/raytracing.md)). |
| raytracing slot | What one raytracing job sends to the GPU, in the proxy: its command list, constants, descriptors and timestamps. The proxy reuses a slot once the GPU is done with it. |
| the GPU profiler | ReDefinition's markers in Unity's render stream, with a D3D11 timestamp at each, which name the GPU's time by camera, pass and mod (`-redefinitionGpuProfile`). |
| the perf monitor | ReDefinition's timing of the main thread's script methods (`-redefinitionPerfMonitor`). |
| the mod cost probe | A probe run that switches each graphics mod off and on in turn and measures the difference (`-redefinitionProbeModCost`). |
| the development player | Unity's `UnityPlayer.dll` with its profiler, put in place of KSP's for one run (`tools/unity_profile.ps1`). |
