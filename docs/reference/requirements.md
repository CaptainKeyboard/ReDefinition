# What the installed mods require

Some mods need a setting -- usually one of KSP's -- to be a certain way, and show an
error, a warning, or do work for nothing otherwise. ReDefinition keeps those
settings as they need them while the mod is loaded, whatever a profile, the reset
or a row says, and whether or not it bundles the mod itself. Each requirement stands
as a `REQUIRES` block in the registration of the mod that needs it
([modders/registering-a-mod.md](../modders/registering-a-mod.md), "What your mod
needs of other settings").

## Enforced

| # | While | Setting | Kept at | Source |
|---|---|---|---|---|
| R1 | Parallax is loaded | KSP terrain detail | the highest preset | Parallax's `ConfigLoader.CheckSettings` shows "Parallax Installation Error" at every start otherwise; its installation instructions |
| R2 | Parallax is loaded | KSP terrain scatter | off | Parallax replaces KSP's scatter only on Eeloo; everywhere else both would be drawn |
| R3 | Parallax is loaded | KSP reflection resolution | 256 at most | `CheckSettings` warns above 256 |
| R4 | Parallax is loaded | KSP reflection refresh | not *Off* | its installation instructions: "Minimum (NOT off!)". With Deferred installed the row does not exist, and Deferred raises *Off* itself |
| R5 | Volumetric Clouds and TUFX are loaded | TUFX's flight profile | one with ambient occlusion -- the author's *Blackrack_TUFX* where the chosen one has none | Volumetric Clouds' Readme: "Use my TUFX profile or your profile of choice with ambient occlusion enabled" |
| R6 | DLSS frame generation runs | KSP V-Sync | off or every refresh | NVIDIA's DLSS-G programming guide, 22.2: a sync interval above 1 is not supported |
| R7 | DLSS frame generation runs in a build without V-Sync support | KSP V-Sync | off | the guide, 22.1: V-Sync only where `bIsVsyncSupportAvailable` is set |

R6 and R7 stand in KSP's registration (`GameData/ReDefinition/Mods/KSP.cfg`) and are
answered by `KspBehaviour.Check`: while ReDefinition's frame generation is off, or
presented by FSR 3, every value passes.

## How they are kept

* A profile's values and the reset's pass through them before they are shown or set.
* A row a requirement fixes to one value is locked; a list offers only the allowed
  entries. The tooltip ends with the reason.
* At every scene load, when a scene is ready, when KSP's own settings are applied, and
  when frame generation is switched on, a value that a requirement forbids is put
  right and saved, with one message per run naming the reason. A per-save value is put
  right in the loaded save only, unless a choice for every save is kept.
* Requirements are enforced while the other mods are bundled (*Bundle other mods here*).
* *Restore settings from before ReDefinition* puts back what was there before,
  even where a requirement forbids it; the next scene load applies the requirement
  again while the bundling is on.
* A requirement that names a check ReDefinition cannot answer for the setting's mod
  is reported once and not enforced.

## Recommended, not enforced

Used by the profiles ([player/graphics-profiles.md](../player/graphics-profiles.md)),
never forced on a player: Parallax's reflection resolution of 128 "for best
performance" and its window's cost notes per setting; Scatterer's quality presets;
EVE's guidance on temporal upscaling.

## Held by the mods themselves

ReDefinition leaves these to the mod, and drops or limits the row instead:

* Firefly sets KSP's aerodynamic effects to their lowest, and warns whenever they are raised.
* Deferred caps KSP's reflection refresh and resolution at every scene load.
* Kopernicus can enforce a terrain shader quality level.

## Set by ReDefinition while a profile is chosen

Not requirements of other mods: the upscaler does the antialiasing, so every profile
switches Scatterer's TAA and SMAA and Deferred's editor SMAA off (`ALL_PROFILES` in
their registrations), and the upscaler switches MSAA off while it runs.

## Dependencies, not requirements

Scatterer's screen-space reflections on the ocean need Deferred; its legacy terrain
light shafts need its long-distance terrain shadows. The tooltips say so.
