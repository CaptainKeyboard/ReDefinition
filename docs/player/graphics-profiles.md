# Graphics profiles

Five profiles -- **Low, Medium, High, Ultra, Max** -- set the quality of KSP's own
graphics and of every bundled mod together. A profile sets **quality settings only**
-- what costs frame time -- and **High is the mod authors' defaults**. Choosing a
profile also makes ReDefinition active: the upscaler runs at *AA only*, and other
mods' antialiasing is switched off for it
([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md)).

Every default and its source: [../reference/mod-defaults.md](../reference/mod-defaults.md);
what the installed mods require: [../reference/requirements.md](../reference/requirements.md).

## What a profile does

* It starts from the **defaults of the installed builds** and sets **quality
  settings** from there. Whether a setting is quality, taste or other is its
  registration's `kind`, with the reason in `mod-defaults.md`.
* **High is those defaults**, sized for an RTX 3080 or 4080 at 1440p, and deviates
  only in KSP's own settings (below). With Volumetric Clouds installed its author's
  values are the defaults, so High follows them.
* **Below High**, a profile lowers only settings that are documented to cost frame
  time. **Above High**, it raises the settings the mods' own higher presets raise.
* A value is set the way a row set by hand is: saved in each mod as its own window
  saves it, with what the setting had before kept for *Restore settings from before
  ReDefinition* ([settings-window.md](settings-window.md)).
* **What an installed mod requires counts over every profile**: with Parallax, KSP's
  highest terrain detail, its own scatter off, and reflections at 256 at most and not
  off; with Volumetric Clouds, a TUFX flight profile with ambient occlusion. Low sets
  terrain detail *Low*; with Parallax installed it is *High*.
* **In every profile**: Scatterer's TAA and SMAA and Deferred's editor SMAA off, since
  the upscaler does the antialiasing; with Volumetric Clouds installed, TUFX's
  *Blackrack_TUFX* profile in every scene, which Volumetric Clouds is made with.
* A setting **another mod holds** is left to it: Firefly holds KSP's aerodynamic FX,
  Deferred KSP's reflection refresh and resolution, Kopernicus the terrain shader
  quality where it enforces one.
* A row changed after a profile is applied makes the choice **Custom**; the profile it
  came from is still named.

## The tiers

| Tier | Made for | Video memory |
|---|---|---|
| Low | GTX 1660 / RTX 2060 class | 6 GB |
| Medium | RTX 3060 / 4060, RX 6600 XT class | 8--12 GB |
| **High** | RTX 3080 / 4080 class | 10--16 GB |
| Ultra | RTX 4090 class | 24 GB |
| Max | RTX 4090 at its limit | 24 GB |

Calibrated for **1440p at native resolution** with the upscaler at *AA only*. At 4K a
tier lower fits (1.78 times the pixels), at 1080p a tier higher (0.56 times). The
tiers differ in GPU work -- volumetric clouds, tessellation, long-distance shadows,
scatter, reflections -- since KSP's CPU work (physics, part count, terrain) is the same
in every tier.

**High deviates from the defaults** in KSP's settings only:

| Setting | Default | High | Reason |
|---|---|---|---|
| Texture quality | Half | Full | 10--16 GB of video memory |
| Terrain detail | Default | High | Parallax requires the highest preset |
| Terrain shader quality | none | Ultra | KSP has no default for it; Ultra is its highest |
| Reflection refresh | Off | Low | Parallax asks for its minimum, not off; Deferred raises Off to Low |

## The values

"d" marks the default of the installed build, "·" the same value as the column
before. Where Scatterer's builds differ: public / Volumetric Clouds.

### KSP

| Setting | Low | Med | High | Ultra | Max | Source |
|---|---|---|---|---|---|---|
| Render quality | 4 *Beautiful* | 5 *Fantastic* d | · | · | · | each Unity level carries its own shadow and filtering settings |
| Texture quality | Half | Full | Full (d Half) | · | · | a higher mipmap limit uses less video memory (Unity) |
| Pixel light count | 4 | 8 d | · | 16 | 32 | forward-rendered lights only; Deferred raises it to at least 64 |
| Shadow cascades | 2 | 4 d | · | · | · | cascades cost rendering (Unity) |
| Terrain detail | Low | Default | High (d Default) | · | · | KSP's presets; High with Parallax |
| Terrain shader quality | High | Ultra | Ultra (no default) | · | · | |
| Aerodynamic FX | Low | Normal d | · | · | · | only without Firefly |
| Reflection refresh | Low | · | Low (d Off) | · | · | not off with Parallax |
| Reflection resolution | 128 | 256 d | · | · | · | Parallax recommends 128 and warns above 256 |

Planet shadows, surface FX and KSP's terrain scatter keep their defaults in every
tier; the scatter stays off, and off with Parallax.

### Scatterer

| Setting | Low | Med | High | Ultra | Max | Source |
|---|---|---|---|---|---|---|
| Wave detail (fourier grid) | 64 | 64 / 128 | 128 / 256 d | · | 256 | the shipped presets; "128 is the sweet spot between quality performance, 256 is the highest quality" (wiki) |
| Ocean mesh resolution (lower is finer) | 8 | 6 d | · | 4 | 4 | the shipped presets |
| Ocean transparency and refraction (Volumetric Clouds' build) | off | on d | · | · | · | off in *Very low* |
| Ocean screen-space reflections (Volumetric Clouds' build) | off | off | on d | · | · | a screen-space trace on the ocean |
| Light shafts through clouds (Volumetric Clouds' build) | off | off | on d | · | · | on from *Optimal* |
| Terrain light shafts (Volumetric Clouds' build) | off d | · | · | on | on | on in *High* and *Very High* |
| Long-distance terrain shadows | off d | · | · | on | on | on in *High* and *Very High* |
| Terrain shadow distance, resolution | 50 000, 8192 d | · | · | 25 000, 4096 | 50 000, 8192 | *High* and *Very High* |
| Dual-camera shadow distance | 50 000 d | · | · | 30 000 | 50 000 | *High* and *Very High* |

The ocean, its shadows, lights, foam and caustics, eclipses and ring shadows keep their
defaults in every tier.

### EVE volumetric clouds

| Setting | Low | Med | High | Ultra | Max | Source |
|---|---|---|---|---|---|---|
| Volumetric cloud upscaling | x16 | x12 | x9 d | x6 | x4 | EVE's wiki: "8x and 9x are the sweet spot and are good for 1440p and 1080p"; "At 16x quality loss starts to become really noticeable"; the cost follows the pixels rendered per frame, 1/N |

Clouds in reflections, the anti-tiling noise and the light volume keep their defaults.

### Parallax Continued

| Setting | Low | Med | High | Ultra | Max | Parallax's tooltip |
|---|---|---|---|---|---|---|
| Terrain tessellation (max 64) | 16 | 32 | 64 d | · | · | "Moderate" |
| Tessellation edge length (lower is finer) | 8 | 6 | 4 d | 3 | 2 | "Moderate" |
| Tessellation range | 20 | 30 d | · | 50 | 80 | "Low" |
| Scatter density | 0.5 | 0.75 | 1 d | 1.5 | 2 | "High GPU performance impact" |
| Scatter range | 0.6 | 0.8 | 1 d | 1.25 | 1.5 | "High" |
| Shadows from part lights | off | on d | · | · | · | "Moderate when lights are on" |
| Part light shadow quality | Low | Low | Medium d | High | VeryHigh | "Moderate" |
| Planet shadow steps | 24 | 32 | 48 d | 64 | 96 | "Moderate" |

Height blending, terrain occlusion, scatter fade-out and planet shadows keep their
defaults.

### Firefly, Deferred, Waterfall, Distant Object

| Setting | Low | Med | High | Ultra | Max | Source |
|---|---|---|---|---|---|---|
| Firefly: re-entry particles | off | on d | · | · | · | a particle system per vessel |
| Deferred: screen-space reflections | off | on d | · | · | · | shipped on |
| Deferred: at half resolution | on d | · | · | off | off | full resolution traces four times the pixels |
| Waterfall: engine plume lights | off | on d | · | · | · | filed under "Quality settings" in its config |
| Distant Object: which vessels are drawn | target only d | · | · | all | all, kept | the cost grows with the vessels in range |

Waterfall's heat distortion and Distant Object's range keep their defaults.

## Sources

| For | Source |
|---|---|
| Every default | `mod-defaults.md`: the release files of the installed versions and the mods' code |
| Scatterer | its shipped `config/qualityPresets.cfg` (*Integrated Graphics, Very low, Low, Optimal, High, Very High*), in the public build and in Volumetric Clouds' package; its wiki, `GeneralConfig` |
| EVE volumetrics | [Temporal upscaling and noise detiling](https://github.com/LGhassen/EnvironmentalVisualEnhancements/wiki/Temporal-upscaling-and-noise-detiling); `RaymarchedCloudsQuality`'s defaults |
| Parallax Continued | its window's tooltips (`ToolbarMenu.cs`), which rate each setting's cost; its installation instructions and `ConfigLoader.CheckSettings` |
| Firefly | `SettingsManager` defaults, `AtmoFxModule` |
| Deferred | `Settings` defaults, `HandleStockProbe` |
| TUFX, Volumetric Clouds | the package's Readme: "Use my TUFX profile or your profile of choice with ambient occlusion enabled" |
| Waterfall | `WaterfallSettings.cfg` |
| Distant Object | its settings object, `VesselDraw`, decompiled from 2.2.1.7 |
| KSP | `GameSettings.SetDefaultValues`, `PQSCache.CreateDefaultPresetList`, `VideoSettings`, decompiled from 1.12.5 |
| Unity | [Quality settings](https://docs.unity3d.com/2019.4/Documentation/Manual/class-QualitySettings.html) and [shadow cascades](https://docs.unity3d.com/2019.4/Documentation/Manual/shadow-cascades.html), 2019.4 |

## Where the profiles are defined

* **The tiers** stand in `GameData/ReDefinition/Profiles/ReDefinition-Profiles.cfg`:
  name, title, order, hardware, description, and a `MODULE` node for ReDefinition's
  own modules (`enabled = True`, `quality = NativeAA`). A profile sets the upscaler's
  `enabled` and `quality` only; the technique, the DLSS preset, the sharpness and
  frame generation are the player's.
* **What a tier sets for a mod** stands in that mod's registration in
  `GameData/ReDefinition/Mods`: a `PROFILE` block per tier with the values that differ
  from the defaults, an `ALL_PROFILES` block for every tier, and blocks with a `build`
  for one build of the mod. Blocks for every build count first, then those for the
  installed build.
* `ProfileApplier.Values` takes the defaults of the installed builds, the
  registrations' values for the profile over them, and the requirements over all of
  it.
* A key a profile names that an installed mod does not offer, a taste or other
  setting, or a value its control cannot take is left out and reported in `KSP.log`
  once per run. A mod that is not installed, and a setting its registration leaves out,
  are skipped without a report.
* `tools/check_bundled_mods.ps1` reads the profiles and registrations with KSP's own
  `ConfigNode`, checks every value against the installed builds, and refuses a taste or
  other setting in a profile, a block for a profile that does not exist, and a key High
  sets beyond its deviations.
* **A visual pack** changes a mod's values for a tier in its registration:
  `@REDEFINITION_MOD[parallax] { @PROFILE[medium]:HAS[~build[]] { ... } }`. A block for
  one build is selected with `:HAS[#build[volumetric]]`, the block for every build with
  `:HAS[~build[]]`. The guide: [../modders/registering-a-mod.md](../modders/registering-a-mod.md).

## Open

| Question | What answers it |
|---|---|
| What KSP's render-quality levels 4 and 5 set: shadow distance, resolution, anisotropic filtering | a log line per level at the main menu; the levels are in KSP's build data, not in its code |
| Whether the tiers land where they are meant to on real GPUs | frame times per tier at 1440p and 4K |
| Steps for the light volume, the light shaft step count, Parallax's fade-out | their authors' cost guidance |
