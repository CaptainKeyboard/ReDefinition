# The settings window

**For:** players.
**You need:** ReDefinition installed ([installing.md](installing.md)).
**You get:** how to find every setting, apply it, reset it, and put the mods back the
way they were.

The window holds the upscaler, frame generation and the graphics settings of the
installed graphics mods, one row per feature, sorted by what a setting does. A mod that
is not installed has no rows.

Nothing of this is active until you choose a graphics profile. Until then the upscaler
and frame generation are off, and every mod keeps its own settings.

## Open it

| Way | Where |
|---|---|
| ReDefinition's toolbar button | every scene, the main menu included |
| KSP's own settings dialog | *Settings* in the pause menu of flight and of the space centre, at the end of its graphics part |
| A hotkey | once you set one in the *Keys* tab; none is bound at first |

The four hotkeys you can set are in the *Keys* tab, in this order: *Upscaler on or
off*, *Settings window*, *Diagnostics window* and *Camera list to the log*.

The window is built from KSP's own dialog elements, so a UI theme such as ZTheme themes
it as well. It edits copies of the values. *Apply* sets what you changed, *Accept* sets
it and closes, *Cancel* closes without setting anything. Clicks on the window do not
reach the scene behind it.

*Reset to defaults* is at the bottom left, beside those three, on every tab.

## The tabs

| Tab | Rows |
|---|---|
| *Profiles* | a status line, and one row per graphics profile with the hardware it is made for and what it sets |
| *General* | Upscaler, Technique, Mode, DLSS preset, Sharpness, Frame generation, *NVIDIA DLSS files*, and KSP's render quality, texture quality, V-Sync and frame limit |
| *Shadows / Reflections* | KSP's shadow cascades, Scatterer's long-distance terrain shadows, Deferred's screen-space reflections |
| *Planets* | EVE's volumetric cloud upscaling, cloud ambience and thunder volume; Scatterer's light shafts through clouds, its ocean and waves moving vessels; KSP's terrain detail; Parallax's scatter density and scatter collisions |
| *Effects* | KSP's aerodynamic FX while Firefly is not installed, Firefly's re-entry particles, Waterfall's plume lights and heat distortion, Scatterer's sun flare, Distant Object's flares, names and distant vessels |
| *Keys* | every key binding: ReDefinition's own, the bundled mods' and KSP's, with a search field above them |
| *Mods / Toolbar* | *Bundle other mods here*, the list of bundled mods, and *Restore settings from before ReDefinition* |

The status line on *Profiles* names the chosen profile, shows *Custom* where rows were
changed since, and says what still waits for *Apply*.

Each row's tooltip says what the setting does, which mod holds the value, where that
value is kept, and when the change arrives: at once, from the next scene on, after a
restart, or when the mod next reads it.

The *Diagnostics* button below the tabs opens the diagnostics window.

The upscaler and frame generation can only be changed while a graphics profile is
chosen.

## Reach a mod's own window

A tab ends with an *Advanced* row where a bundled mod puts its own window there. With
the mods bundled today that is *Planets*, for Scatterer, EVE and Parallax, and
*Effects*, for Firefly, TUFX and Distant Object.

A button opens that mod's own window beside ReDefinition's, with an X at its top right
to close it. A change you make there shows up in ReDefinition's rows. A change
you make here shows up there once you apply it.

While their settings are bundled here, the toolbar buttons of those mods are hidden.
Waterfall's button opens its effect editor rather than settings, so it stays, and so do
buttons on Blizzy's toolbar.

## Set a key

The *Keys* tab holds every binding in one place, in sections. *Mods* holds
ReDefinition's own hotkeys and the mods', and a mod can place its bindings in another
section. KSP's own bindings are in the groups KSP sorts them into: *Flight*, *EVA*,
*Editor*, *Camera*, *Map and vessels* and *General*.

A section opens and folds with a click on its title, and *Mods* is open at first. The
search field filters the rows by their name or their mod, in every section. While the
search field has the keyboard, no key reaches the game: W is a letter there, not a
pitch. Enter confirms, Escape leaves the field.

To set a binding:

1. Click the row. It says that it is listening.
2. Press the key you want. Escape cancels, and *x* beside the row clears the binding.
3. Set the modifiers with the switches *Ctrl*, *Alt* and *Shift* beside the row. One
   click takes the left one (*L Ctrl*), a second the right one (*R Ctrl*), a third
   none.
4. Press *Apply* or *Accept*.

A binding holds up to two modifiers. A switch is grey while no key is set, and where the
binding already holds as many modifiers as it can. The modifiers come from the switches
rather than from the keyboard, because Windows turns AltGr into Ctrl and Alt at once. A
binding with right Alt answers to AltGr.

*Reset to defaults* puts every binding back to its default: ReDefinition's own, the
mods', and KSP's to the keys KSP ships, the same ones KSP's own reset sets.

KSP keeps two keys per binding, a first and a second, each without a modifier. Its
modifier key is a binding of its own. The mods' bindings and ReDefinition's take
modifiers.

If two rows hold the same combination in the same situation, both are shown in
yellow, and both still work. Against one of KSP's bindings only the key counts, since
KSP's fire on their key whatever modifiers are held: a mod's Ctrl+U meets KSP's U. A
binding counts where its section does: flying a vessel, map view included, on EVA, or
in the editor. *Mods* and *General* count everywhere. B that brakes a vessel and B that
boards one on EVA never meet, so neither is marked. Within flight KSP's own modes count
too: Space stages, and in docking mode the same key translates, so those two never
meet.
Keys KSP itself ships twice, such as W for pitch and for driving a rover, stay unmarked
while both stand at KSP's default.

## Find out why a row is missing

Three things leave a mod out of the window:

* the mod is not installed;
* the installed build is one ReDefinition does not bundle. Plain EVE Redux without the
  volumetric clouds is such a build;
* *Bundle other mods here*, under *Mods / Toolbar*, is switched off. Then only
  ReDefinition's own rows and KSP's are in the window.

Which of the three it is, the game says: *Diagnostics*, *Debug*, *Show mod
registrations*. It lists every mod that is loaded and not bundled, with the reason.

## Where your change goes

A setting you change here is saved in its mod, the way that mod's own window saves it:

| Mod | Kept |
|---|---|
| KSP | in `settings.cfg`, as KSP's own settings screens save it |
| Scatterer, EVE, Parallax, Firefly | in their own config files, through their own save routines |
| TUFX, Distant Object | per save. Your choice counts for every save: it goes into the loaded save, and into every other save as that save loads. A change in their own windows becomes your choice here |
| Deferred, Waterfall | they have no save routine, so ReDefinition keeps the value in `bundled.cfg` and sets it at every start and scene change |

A value a mod cannot take yet waits in `bundled.cfg` and reaches the mod once it can
take it. EVE before its cloud settings are loaded is such a case, and so is a mod whose
settings exist only in flight.

Where each mod keeps its settings:
[how-each-mod-keeps-its-settings.md](../reference/how-each-mod-keeps-its-settings.md).

## Choose a profile, or reset

Choosing a profile fills the rows of every tab with its values, and *Apply* or *Accept*
sets them. A row you change afterwards makes the choice *Custom*. A profile sets what
costs frame time, and switches the upscaler on. What each profile sets:
[graphics-profiles.md](graphics-profiles.md).

*Reset to defaults* asks first and names what it resets. It fills the rows with every
setting's default, the settings without a row included. Those defaults are what the
mods' releases ship. With Volumetric Clouds installed, the defaults of EVE, Scatterer
and TUFX are the ones its author ships. KSP's graphics settings are reset without
resolution and full screen.

After a reset, ReDefinition's own settings stand at their defaults and no profile is
chosen: the upscaler and frame generation are off, and the other mods keep their own
antialiasing until you choose a profile. A setting without a known default, KSP's
terrain shader quality, stays as it is. Every default and its source:
[reference/mod-defaults.md](../reference/mod-defaults.md).

## Put the mods back as they were

Before ReDefinition changes a setting for the first time, it writes that setting's
current value into `bundled.cfg`. For TUFX and Distant Object it does so once per save.

*Restore settings from before ReDefinition*, under *Mods / Toolbar*, asks first, puts
every one of those values back, saves each in its mod and clears the profile. A save
that is not loaded gets its values back the next time you load it. The button is greyed
out while changes wait for *Apply*.

When you choose another profile, a setting the previous profile set and the new one
does not goes back to its value from before ReDefinition, unless you changed it since.

*Bundle other mods here*, under *Mods / Toolbar*, switched off gives the mods back
their toolbar buttons, lets each mod's own window decide again, and clears the profile.
What was saved in the mods stays. What only ReDefinition kept is dropped: the choices
for every save, and Deferred's and Waterfall's settings.

At the first start, the main menu explains ReDefinition and offers *Use High*, which
chooses the High profile and bundles the mods, or *Later*, which leaves every mod as it
is until you choose a profile. A graphics mod installed later follows that choice, with
a short note when it is bundled.

## What the installed mods require

Some mods need another setting to stand a certain way and show an error or a warning
otherwise. ReDefinition keeps such a setting as the mod needs it, whatever a profile,
the reset or a row says. The row is locked or offers only the allowed values, and its
tooltip says why.

A value set elsewhere, in KSP's own settings screen for example, is corrected at the
next scene load or when KSP's settings are applied, with one message per run. The list,
with the source of each:
[reference/requirements.md](../reference/requirements.md).

## KSP's own graphics settings

The rows are: render quality, texture quality, V-Sync, frame limit, shadow cascades,
terrain detail, and aerodynamic FX while Firefly is not installed. The profiles also set KSP's
pixel light count, terrain shader quality and reflections. *Reset to defaults* sets
those and its terrain scatter, planet shadows, scatter density and surface FX.

Some of them another mod holds for itself:

| Setting | Held by |
|---|---|
| aerodynamic FX | Firefly, at its lowest, since it replaces KSP's effects |
| reflection refresh | Deferred, at Low, while its own cap for it is on |
| reflection resolution | Deferred, at 256 at most, while its own cap for it is on |
| terrain shader quality | Kopernicus, where its config enforces a level or warns about one |

## The section in KSP's settings dialog

It holds Upscaler, Technique, Mode with all six modes and their scale factors, DLSS
preset, Sharpness and Frame generation. What the dialog shows is a copy, set on *Apply*
or *Accept*, as KSP's own graphics settings are. The dialog and the settings window use
the same settings file. The main menu's settings screen has no ReDefinition section.

## The diagnostics window

Open it with *Diagnostics* in the settings window, or with the hotkey you set for it.
At the top stand the status, the frame rate, with the presented rate in brackets while
frame generation runs, and the on/off button. Below them are two tabs.

*General* holds Technique, Mode, DLSS preset, Sharpness, the frame generation switch,
and whether other visual mods run anything that conflicts with the upscaler. If the
chosen technique cannot run, it names the reason, and after a failure it offers
*Try ... again*.

*Debug* is for finding out what happens:

| Section | Holds |
|---|---|
| Measure | for troubleshooting and bug reports: the frame rates with the upscaler on and off, the load of the main thread, the render thread and the GPU, *Write diagnostics to log*, *Without upscaler (bypass)*, and *Motion vector check on fast turns* with frame generation, which is not saved. The load is measured only while this tab is open |
| What the upscaler receives | switches for a bug report, each explained by its tooltip: Jitter; EVE clouds: jitter and motion vectors, not saved; Mipmap bias; Skinned motion vectors; TUFX after upscaling; Auto exposure for FSR 3 and AMD; Transparency mask and Reactive mask for FSR 3 |
| Game settings while upscaling | LOD bias compensation, MSAA forced off, Anisotropic filtering forced |
| FSR 3 internals | *FSR's debug view*, in development builds only |

Below them stand three buttons. *Show other mods' state* lists what ReDefinition sees of
the other mods. *Show mod registrations* lists the bundled mods and the problems
of their registrations. *Show preview* shows what the upscaler receives: motion
vectors, depth, the image at render resolution, the transparency and reactive masks,
and the result. The motion vector, depth and mask previews read the image back from the
GPU and cost frame rate while they are shown.

Everything ReDefinition reports goes into `KSP.log` behind `[ReDefinition]`. The proxy
writes `ReDefinitionProxy.log` next to `KSP_x64.exe`.

## After a mod's update

A mod's new version can drop a setting, hold it another way, or save one more. The tab
is then titled *Mods and toolbar (!)*, and under *Not shown in this window* it names
what is missing, per mod:

* a row this window places that the new version no longer has as ReDefinition knows it;
* a setting the new version saves that no registration names yet;
* a new version ReDefinition cannot bundle at all. Its settings then stay with the mod,
  and its toolbar button stays.

Each of them is still in that mod's own window. Where ReDefinition can open that window,
a button beside the line does so; a build it cannot bundle at all keeps its own toolbar
button instead.
A version that changes nothing of this is not mentioned.

## What is not bundled

* Kopernicus, whose window holds technical configuration rather than graphics settings.
* Deferred's caps on KSP's reflection probe, which lower KSP's own settings.
* Any setting a mod does not save itself. Scatterer keeps only the fields it marks
  `[Persistent]`, so a field without that mark stays Scatterer's and is named in the
  log.

Other mods and visual packs can register their settings with a config file in their own
folder: [modders/registering-a-mod.md](../modders/registering-a-mod.md).
