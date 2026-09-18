# The settings window

ReDefinition's settings window holds the upscaler, frame generation and the graphics
settings of the installed graphics mods, one row per feature, sorted by what a
setting does. A mod that is not installed has no rows.

## Opening it

* **ReDefinition's toolbar button**, in every scene including the main menu.
* **KSP's own settings dialog** (*Settings* in the pause menu of flight and of the
  space centre): a *ReDefinition* section at the end of its graphics part.
* **Hotkeys**, as they come and as the *Keys* tab sets them:

| Keys | Does |
|---|---|
| `RightCtrl` + `RightShift` + `U` | upscaler on or off |
| `RightCtrl` + `RightShift` + `K` | diagnostics window open or closed |
| `RightCtrl` + `RightShift` + `N` | camera list into `KSP.log` |
| not bound | this window open or closed |

The window is built from KSP's own dialog elements, so a UI theme such as ZTheme
themes it. It edits copies: *Apply* sets what was changed, *Accept* sets it and
closes, *Cancel* closes without setting anything. Clicks on the window do not reach
the scene behind it.

## The tabs

| Tab | Rows |
|---|---|
| **Profiles** | the graphics profiles with the hardware each is made for, a status line -- the chosen profile, *Custom* with the rows changed since, what waits for *Apply* -- and *Reset to defaults* |
| **General** | Upscaler, Technique, Mode, DLSS preset, Sharpness, Frame generation; *NVIDIA DLSS files*; KSP's render quality, texture quality, V-Sync and frame limit |
| **Shadows and reflections** | KSP's shadow cascades, Scatterer's long-distance terrain shadows, Deferred's screen-space reflections |
| **Planets** | EVE's volumetric cloud upscaling, cloud ambience and thunder volume; Scatterer's light shafts through clouds, ocean and waves moving vessels; KSP's terrain detail; Parallax's scatter density and scatter collisions |
| **Effects** | KSP's aerodynamic FX (while Firefly is not installed), Firefly's re-entry particles, Waterfall's plume lights and heat distortion, Scatterer's sun flare, Distant Object's flares, names on flares and distant vessels |
| **Keys** | every key binding: ReDefinition's own, the bundled mods' and KSP's, with a search field above them |
| **Mods and toolbar** | *Bundle other mods here*, the list of bundled mods, and *Restore settings from before ReDefinition* |

The *Diagnostics* button at the end of the tab row opens the diagnostics window.

A row's tooltip says what the setting does and when a change takes effect: at once,
from the next scene on, or after a restart.

**Advanced.** The *Planets* and *Effects* tabs end with a button for each mod whose
own window has more: Scatterer, EVE and Parallax under *Planets*; Firefly, TUFX and
Distant Object under *Effects*. It opens the mod's own window beside ReDefinition's,
with an X at its top right to close it. A change made there shows in ReDefinition's
rows; a change made here shows there once applied.

**The upscaler and frame generation** can be changed only while a graphics profile is
chosen.

## Key bindings

The *Keys* tab holds every binding in one place: ReDefinition's own hotkeys, the
bindings of the bundled mods, and KSP's own, in the groups KSP sorts them into.
KSP's groups are folded until one is opened with a click on its title. The search
field at the top filters the rows by their name or their mod, in every group.

Click a binding, and the row says it is listening: the next key pressed is taken.
Escape cancels, and *x* beside it clears the binding. The modifiers are the
switches *Ctrl*, *Alt* and *Shift* beside it: a click sets the left one (*L Ctrl*),
a second the right one (*R Ctrl*), a third none. A binding holds up to two; a
switch is grey where it has no room. Pressed modifiers are not taken from the
keyboard, since Windows turns AltGr into Ctrl and Alt at once -- a binding with
right Alt answers to AltGr. As everywhere in this window, *Apply* or *Accept* sets
what was changed and *Cancel* leaves it.

*Reset to defaults* puts every binding back to its default: ReDefinition's own,
the mods', and KSP's to what KSP ships -- the same keys KSP's own reset sets.

KSP keeps two keys per binding, a first and a second, and one key each without a
modifier -- its own modifier key is a binding of its own. The mods' and
ReDefinition's own take modifiers.

Where two rows hold the same combination, both are shown in yellow. Nothing is
refused: KSP's bindings that never count at the same time -- one in flight, one in
the editor -- are no conflict and stay unmarked.

## Where a change goes

A setting changed here is saved in its mod, as that mod's own window saves it:

| Mod | Kept |
|---|---|
| KSP | in `settings.cfg`, as KSP's own settings screens save it |
| Scatterer, EVE, Parallax, Firefly | in their own config files, through their own save routines |
| TUFX, Distant Object | per save: a choice here counts for every save, goes into the loaded save, and into every other save as it loads; a change made in their own windows becomes the choice here |
| Deferred, Waterfall | they have no save routine: ReDefinition keeps the value in its `bundled.cfg` and sets it at every start and scene change |

A value a mod cannot take yet -- EVE before its cloud settings are loaded, a mod whose
settings exist only in flight -- waits in `bundled.cfg` and reaches the mod once it
can take it.

How each mod keeps its settings:
[reference/how-each-mod-keeps-its-settings.md](../reference/how-each-mod-keeps-its-settings.md).

## Profiles, and *Reset to defaults*

Choosing a profile fills the rows of every tab with its values; *Apply* or *Accept*
sets them. A row changed afterwards makes the choice *Custom*. A profile sets quality
-- what costs frame time -- and switches the upscaler on. What each profile sets:
[graphics-profiles.md](graphics-profiles.md).

*Reset to defaults* asks first and names what it resets. It fills the rows with every
setting's default -- what the mods' releases ship, with Volumetric Clouds installed
what its author ships, and KSP's graphics settings without resolution and full
screen -- including the settings that have no row. ReDefinition's own settings go
back to theirs, and no profile stays chosen: the upscaler and frame generation are
off, and the other mods keep their own antialiasing, until a profile is chosen. A
setting without a known default -- KSP's terrain shader quality -- stays as it is.
Every default and its source: [reference/mod-defaults.md](../reference/mod-defaults.md).

## Values from before ReDefinition

Before ReDefinition first changes a setting, the value its mod had is written to
`bundled.cfg` -- for TUFX and Distant Object once per save. *Restore settings from
before ReDefinition*, under *Mods and toolbar*, asks first, puts every one of them
back, saves it in its mod and clears the profile. A save that is not loaded gets its
values back the next time it loads. The button is greyed out while changes wait for
*Apply*.

When another profile is chosen, a setting the previous profile set and the new one
does not goes back to its value from before ReDefinition, unless it was changed
since.

## What the installed mods require

Some mods need a setting to be a certain way and show an error or warning otherwise.
Whatever a profile, the reset or a row says, ReDefinition keeps such a setting as the
mod needs it: the row is locked or offers only the allowed values, its tooltip says
why, and a value set elsewhere -- in KSP's own settings screen, for example -- is put
right at the next scene load or when KSP's settings are applied, with one message per
run. The list, with the source of each:
[reference/requirements.md](../reference/requirements.md).

## The other mods' toolbar buttons

While their settings are bundled here, the toolbar buttons of Scatterer, EVE,
Parallax, Firefly, TUFX and Distant Object are hidden, as long as ReDefinition's
window can open their own windows through the *Advanced* buttons. Waterfall's button,
which opens its effect editor, stays, and so do buttons on Blizzy's toolbar.

At the first start the main menu explains ReDefinition and offers *Use High*, which
chooses the High profile and bundles the mods, or *Later*, which leaves every mod as
it is until a profile is chosen. A graphics mod installed later follows that choice,
with a short note when it is bundled.

*Bundle other mods here*, under *Mods and toolbar*, switched off: the buttons come
back, each mod's own window decides again, and the profile is cleared. What was saved
in the mods stays; what only ReDefinition kept -- the choices for every save, and
Deferred's and Waterfall's settings -- is dropped.

## KSP's own graphics settings

Rows: render quality, texture quality, V-Sync, frame limit, shadow cascades, terrain
detail, and aerodynamic FX while Firefly is not installed. The profiles and *Reset to
defaults* also set KSP's pixel light count, terrain shader quality, terrain scatter,
planet shadows and reflections. Some of them another mod holds for itself:

* aerodynamic FX: Firefly sets it to its lowest, since it replaces KSP's effects;
* the reflection refresh: Deferred holds it at Low;
* the reflection resolution: Deferred holds it at 256 at most;
* the terrain shader quality: Kopernicus, where it enforces a level.

## The section in KSP's settings dialog

Upscaler, Technique, Mode (all six, with the scale factor), DLSS preset, Sharpness and
Frame generation. What the dialog shows is a copy, set on *Apply* or *Accept*, as KSP's
own graphics settings are. The dialog and the settings window use the same settings
file. The main menu's settings screen has no ReDefinition section.

## The diagnostics window

*Diagnostics* in the settings window, or `RightCtrl` + `RightShift` + `K`. The status,
the on/off button and the frame rates -- measured separately with the upscaler on and
off, with the load of the main thread, the render thread and the GPU -- sit above two
tabs.

**General:** Technique, Mode, DLSS preset, Sharpness, the frame generation switch, and
whether other visual mods run anything that conflicts with the upscaler. Where the
chosen technique cannot run, the reason, and after a failure *Try ... again*.

**Debug**, for finding out what happens:

| Section | Holds |
|---|---|
| Measure | *Write diagnostics to log*; *Without upscaler (bypass)*; *Motion vector check on fast turns* (with frame generation; not saved) |
| What the upscaler receives | Jitter; EVE clouds: jitter and motion vectors (not saved); Mipmap bias; Skinned motion vectors; TUFX after upscaling; Auto exposure (FSR 3, AMD); Transparency mask (FSR 3); Reactive mask (FSR 3) |
| Game settings while upscaling | LOD bias compensation; MSAA forced off; Anisotropic filtering forced |
| FSR 3 internals | *FSR's debug view* -- in development builds only |

Below them: the other mods' state as ReDefinition sees it, *Show mod registrations* --
the bundled mods, the installed mods that are not bundled with the reason, and every
problem in the registrations -- and a preview of what the upscaler receives: motion
vectors, depth, the image at render resolution, the transparency and reactive masks,
and the result. The motion vector, depth and mask previews read the image back from
the GPU and cost frame rate while they are shown.

Everything ReDefinition reports goes into `KSP.log` with the prefix `[ReDefinition]`;
the proxy writes `ReDefinitionProxy.log` next to `KSP_x64.exe`.

## Not bundled

Kopernicus, whose window holds technical configuration rather than graphics settings;
Deferred's caps on KSP's reflection probe, which lower KSP's own settings; and any
setting a mod does not save itself -- Scatterer keeps only the fields it marks
`[Persistent]`, so a field without that mark stays Scatterer's and is named in the
log.

Other mods and visual packs can register their settings with a config file in their
own folder: [modders/registering-a-mod.md](../modders/registering-a-mod.md).

## After a mod's update

A mod's new version can drop a setting, hold it another way, or save one more. The
tab *Mods and toolbar* is then marked *(!)*, and under *Not shown in this window* it
names what is missing, per mod:

* a row this window places that the new version no longer has as ReDefinition knows it;
* a setting the new version saves that no registration names yet;
* a new version that ReDefinition cannot bundle at all -- its settings then stay with
  the mod, and its toolbar button stays.

Each of them is still in the mod's own window, which the button beside the line opens.
A version that changes nothing of this is not mentioned.
