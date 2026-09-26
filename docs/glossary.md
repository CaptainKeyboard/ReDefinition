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
| the diagnostics window | ReDefinition's second window, opened with *Diagnostics*. It holds the frame rate, what the upscaler receives, and the switches for finding out what happens. |
| member path | Where a value lives, written as in C#: `Deferred.Deferred.settings.useScreenSpaceReflections`. |
| behaviour | Code that answers for settings a member path cannot reach. Either one of ReDefinition's own seven, named without a dot (`Tufx`, `Scatterer`, `Eve`, `Ksp`, `DistantObject`, `Firefly`, `ParallaxScatter`), or a type the mod itself brings, named with its namespace. |

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
| *Reset* | Sets every setting to the default of the installed build, every key binding to its default, KSP's bindings to KSP's own, and chooses High over them. It is not the same as *Restore settings from before ReDefinition*. |

## Rendering

| Word | Means |
|---|---|
| the proxy | ReDefinition's `dxgi.dll`, next to `KSP_x64.exe`. It presents the game's frames through Direct3D 12, runs DLSS, AMD's upscaler DLL and frame generation, and runs the compute passes other mods ask for. |
| the rig | The upscaler on one scene's camera stack: its render targets, the jitter, the captures and the dispatch. |
| the presenter | The camera behind the 3D stack that draws the upscaled image into the frame buffer, before the interface. |
| technique | Which upscaler runs: *FSR 3*, *DLSS* or *AMD FSR (DLL)*. |
| mode | How much smaller the scene renders, from *AA only* at full resolution to the smallest factor. |
| upscaling | Rendering at a lower resolution and reconstructing the full-resolution image from the game's depth and motion vectors. |
| *AA only* | Rendering at the full resolution and using the upscaler as antialiasing alone. |
| frame generation | Putting generated frames between the rendered ones, in the proxy. |
| jitter | The sub-pixel shift of the camera each frame that the upscaler needs. |
| motion vectors | How far each pixel moved since the last frame. The upscaler needs them for everything that moves. |
| history reset | The signal that the upscaler has to forget its previous frames. A camera cut causes one, and so does a mod that asks for one. |
| HUD-less copy | The frame without the interface, which frame generation needs so the interface is not warped into generated frames. |
| hook | A place where a mod takes part in ReDefinition's rendering: motion vectors into the capture, drawing after upscaling, or an overlay. |
