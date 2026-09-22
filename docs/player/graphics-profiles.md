# Graphics profiles

**For:** players choosing a profile.
**You need:** ReDefinition installed, and the settings window open on its *Graphics*
tab.
**You get:** what each of the five tiers sets, for KSP and for every bundled mod.

Five profiles, Low, Medium, High, Ultra and Max, set the quality of KSP's own graphics
and of every bundled mod together. Choosing one also makes ReDefinition active: the
upscaler runs at *AA only*, and the other mods' antialiasing is switched off for it
([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md)).

## What a profile sets

A profile sets quality settings, the ones that cost frame time. It leaves a setting
that decides how the game looks alone, except where a mod needs one changed to run with
the upscaler. Which of the two each setting is, with the
reason, is in [reference/mod-defaults.md](../reference/mod-defaults.md).

* It starts from the defaults of the installed builds, and changes quality settings
  from there.
* **High is those defaults.** Beyond what every
  profile sets, it deviates only in KSP's own settings, listed below. With Volumetric
  Clouds installed, its author's values are the defaults, so High follows them.
* Below High, a profile lowers only settings that are documented to cost frame time.
  Above High, it raises the settings the mods' own higher presets raise.
* A value is set the way a row set by hand is. Each mod saves it as its own window
  saves it, and what the setting held before is kept for *Restore settings from before
  ReDefinition*.
* What an installed mod requires takes precedence over every profile. With Parallax, that is
  KSP's highest terrain detail, KSP's own scatter off, and reflections at 256 at most
  and not off. With Volumetric Clouds, it is a TUFX flight profile with ambient
  occlusion. Low sets terrain detail *Low*, and *High* where Parallax is installed.
* Every profile switches off KSP's own antialiasing and Scatterer's TAA and SMAA and
  Deferred's editor SMAA, because the upscaler does the antialiasing. KSP's row is in
  its own settings screen, and the choice is put back by *Restore settings from before
  ReDefinition*. With Volumetric Clouds installed, every
  profile sets TUFX's *Blackrack_TUFX* profile in every scene, the one Volumetric
  Clouds is made with.
* A setting another mod holds is left to that mod. Firefly holds KSP's aerodynamic FX,
  Deferred KSP's reflection refresh and resolution, and Kopernicus the terrain shader
  quality where its config enforces a level or warns about one.
* A row you change after applying a profile makes the choice *Custom*. The profile it
  came from is still named.
* A mod you install later is set up by the profile you have chosen, at the next start,
  without pressing *Apply*. The same where a mod's build changes, as it does when
  Volumetric Clouds brings its own EVE and Scatterer. Only those mods are set; what you
  changed in the others stays.

## The tiers

| Tier | What it sets |
|---|---|
| Low | every visual mod on, at its lowest useful level |
| Medium | clouds, ocean and scatter a step below the mods' defaults |
| **High** | every mod as its authors ship it |
| Ultra | Scatterer's High preset, finer clouds, tessellation and scatter |
| Max | Scatterer's Very High preset and the finest detail everywhere |

**Which tier a graphics card holds is not said here, because it has not been measured.**
Start with High, the mods' own defaults. If the frame rate does not hold, take the tier
below it, or switch the upscaler from *AA only* to a smaller mode
([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md)). A larger screen
costs more: 4K has 2.25 times the pixels of 1440p, 1080p 0.56 times.

The tiers differ in the work the graphics card does: volumetric clouds, terrain detail,
long-distance shadows, ground scatter and reflections. What the processor does, the
physics and the part count, is the same in every tier.

High deviates from the defaults in KSP's settings only:

| Setting | Default | High | Reason |
|---|---|---|---|
| Texture quality | Half | Full | 10 to 16 GB of video memory |
| Terrain detail | Default | High | Parallax requires the highest preset |
| Terrain shader quality | none | Ultra | KSP has no default for it, and Ultra is its highest |
| Reflection refresh | Off | Low | Parallax asks for its minimum rather than off, and Deferred raises Off to Low |

## What each tier sets

"d" marks the default of the installed build, and "·" the same value as the column
before. Where Scatterer's builds differ, the two values are public build / Volumetric
Clouds build.

### KSP

| Setting | Low | Med | High | Ultra | Max | Why |
|---|---|---|---|---|---|---|
| Render quality | 4 *Beautiful* | 5 *Fantastic* d | · | · | · | each level sets KSP's shadows and texture filtering together |
| Texture quality | Half | Full | Full (d Half) | · | · | half-size textures use less video memory |
| Pixel light count | 4 | 8 d | · | 16 | 32 | how many lights can light an object at once. Deferred raises it to at least 64 |
| Shadow cascades | 2 | 4 d | · | · | · | more cascades give finer shadows and cost more |
| Terrain detail | Low | Default | High (d Default) | · | · | KSP's presets; High with Parallax |
| Terrain shader quality | High | Ultra | Ultra (no default) | · | · | |
| Aerodynamic FX | Low | Normal d | · | · | · | only without Firefly |
| Reflection refresh | Low | · | Low (d Off) | · | · | not off with Parallax |
| Reflection resolution | 128 | 256 d | · | · | · | Parallax warns at every start above 256 |

Planet shadows, surface FX and KSP's terrain scatter keep their defaults in every tier.
The scatter stays off, and stays off with Parallax.

### Scatterer

| Setting | Low | Med | High | Ultra | Max | Why |
|---|---|---|---|---|---|---|
| Wave detail (fourier grid) | 64 | 64 / 128 | 128 / 256 d | · | 256 | the shipped presets; "128 is the sweet spot between quality performance, 256 is the highest quality" (wiki) |
| Ocean mesh resolution (lower is finer) | 8 | 6 d | · | 4 | 4 | the shipped presets |
| Ocean transparency and refraction (Volumetric Clouds' build) | off | on d | · | · | · | off in *Very low* |
| Ocean screen-space reflections (Volumetric Clouds' build) | off | off | on d | · | · | a screen-space trace on the ocean |
| Light shafts through clouds (Volumetric Clouds' build) | off | off | on d | · | · | on from *Optimal* |
| Terrain light shafts (Volumetric Clouds' build) | off d | · | · | on | on | on in *High* and *Very High* |
| Long-distance terrain shadows | off d | · | · | on | on | on in *High* and *Very High* |
| Terrain shadow distance, resolution | 50 000, 8192 d | · | · | 25 000, 4096 | 50 000, 8192 | Ultra takes Scatterer's *High* preset, which is shorter and coarser than Scatterer's own default. Max keeps that default |
| Dual-camera shadow distance | 50 000 d | · | · | 30 000 | 50 000 | *High* and *Very High* |

The ocean, its shadows, lights, foam and caustics, eclipses and ring shadows keep their
defaults in every tier.

### EVE volumetric clouds

| Setting | Low | Med | High | Ultra | Max | Why |
|---|---|---|---|---|---|---|
| Volumetric cloud upscaling | x16 | x12 | x9 d | x6 | x4 | a higher factor renders fewer cloud pixels and costs less. EVE's author calls 8x and 9x the sweet spot for 1440p and 1080p, and says quality loss becomes noticeable at 16x |

Clouds in reflections, the anti-tiling noise and the light volume keep their defaults.

### Parallax Continued

| Setting | Low | Med | High | Ultra | Max | Parallax's tooltip |
|---|---|---|---|---|---|---|
| Terrain tessellation (max 64) | 16 | 32 | 64 d | · | · | "Moderate" |
| Tessellation detail falloff (lower is finer) | 8 | 6 | 4 d | 3 | 2 | "Moderate" |
| Tessellation distance | 20 | 30 d | · | 50 | 80 | "Low" |
| Scatter density | 0.5 | 0.75 | 1 d | 1.5 | 2 | "High GPU performance impact" |
| Scatter distance | 0.6 | 0.8 | 1 d | 1.25 | 1.5 | "High" |
| Shadows from part lights | off | on d | · | · | · | "Moderate when lights are on" |
| Part light shadow quality | Low | Low | Medium d | High | VeryHigh | "Moderate" |
| Planet shadow quality from afar | 24 | 32 | 48 d | 64 | 96 | "Moderate" |

Height blending, terrain occlusion, scatter fade-out and planet shadows keep their
defaults.

### Firefly, Deferred, Waterfall, Distant Object

| Setting | Low | Med | High | Ultra | Max | Why |
|---|---|---|---|---|---|---|
| Firefly: re-entry particles | off | on d | · | · | · | a particle system per vessel |
| Deferred: screen-space reflections | off | on d | · | · | · | shipped on |
| Deferred: at half resolution | on d | · | · | off | off | full resolution costs about four times as much |
| Waterfall: engine plume lights | off | on d | · | · | · | Waterfall itself counts these as a quality setting |
| Distant Object: distant vessels drawn | target only d | · | · | all | all, kept | the cost grows with the vessels in range. A profile sets this one, and the window has no row for it |

Waterfall's heat distortion and Distant Object's range keep their defaults.

## Where the values come from

Every value here comes from the mods' own releases and from what their authors write
about them. Each one, with its source:
[reference/mod-defaults.md](../reference/mod-defaults.md).

A visual pack can change what a tier sets for a mod. How, and what a profile refuses:
[modders/registration-reference.md](../modders/registration-reference.md).
