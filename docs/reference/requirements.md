# What the installed mods require

**For:** players wondering why a row is locked, and mod authors writing a `REQUIRES`
block.
**You need:** nothing.
**You get:** every requirement ReDefinition enforces, with its source, and the rules by
which it holds.

A requirement is a value one mod needs of another mod's setting. The mod errors or warns
about that setting whether or not ReDefinition bundles it, so the requirement holds
while the mod is loaded. While *Bundle other mods here* is on, ReDefinition keeps the
setting as the mod needs it, against a profile, the reset and the player alike.

Each requirement is written as a `REQUIRES` block in the registration of the mod that needs
it ([modders/registering-a-mod.md](../modders/registering-a-mod.md)). Its keys:
[modders/registration-reference.md](../modders/registration-reference.md).

## The requirements

| # | While | Setting | Kept at | Source |
|---|---|---|---|---|
| R1 | Parallax is loaded | KSP terrain detail | the highest preset | Parallax's `ConfigLoader.CheckSettings` shows "Parallax Installation Error" at every start otherwise; its installation instructions |
| R2 | Parallax is loaded | KSP terrain scatter | off | Parallax brings its own scatter; KSP's would be drawn beside it for nothing |
| R3 | Parallax is loaded | KSP reflection resolution | 256 at most | `CheckSettings` warns above 256 |
| R4 | Parallax is loaded | KSP reflection refresh | not *Off* | its installation instructions: "Minimum (NOT off!)". With Deferred's own refresh cap on, the row is not shown at all; with that cap off, the row stays without *Off*, which Deferred raises to Low |
| R5 | Volumetric Clouds and TUFX are loaded | TUFX's flight profile | one with ambient occlusion. The author's *Blackrack_TUFX* where the chosen one has none | Volumetric Clouds' Readme: "Use my TUFX profile or your profile of choice with ambient occlusion enabled" |
| R6 | DLSS frame generation runs | KSP V-Sync | off, or every refresh | NVIDIA's DLSS-G programming guide, 22.2: a sync interval above 1 is not supported |
| R7 | DLSS frame generation runs in a build without V-Sync support | KSP V-Sync | off | the guide, 22.1: V-Sync only where `bIsVsyncSupportAvailable` is set |

R6 and R7 are in KSP's registration, `GameData/ReDefinition/Mods/KSP.cfg`, as the
checks `DlssFrameGenerationEveryRefresh` and `DlssFrameGenerationVSync`
([modders/registration-reference.md](../modders/registration-reference.md)). While
ReDefinition's frame generation is off, or while FSR 3 presents it, every value passes.

## How a requirement is kept

* A profile's values and the reset's pass through the requirements before they are
  shown or set.
* A row a requirement locks (`lock = True`) cannot be moved. A list offers only the
  allowed entries, and any other row is corrected as it is applied. The tooltip ends
  with the reason.
* A forbidden value is corrected and saved at every scene load, when a scene is ready,
  when KSP's own settings are applied, and when frame generation is switched on. One
  message per run names the reason.
* A per-save value is corrected in the loaded save only, unless a choice for every save
  is kept.
* Requirements are enforced while *Bundle other mods here* is on. With it off, the mods
  hold their settings themselves again.
* *Restore settings from before ReDefinition* puts back what was there before, even
  where a requirement forbids it. The next scene load applies the requirement again
  while the bundling is on.
* A requirement naming a check ReDefinition cannot answer for that setting's mod is
  reported once and not enforced.

## Recommended, not enforced

The profiles use these, and no player is held to them
([player/graphics-profiles.md](../player/graphics-profiles.md)):

* Parallax's reflection resolution of 128 "for best performance", and its window's cost
  notes per setting;
* Scatterer's quality presets;
* EVE's guidance on temporal upscaling.

## Held by the mods themselves

ReDefinition leaves these to the mod, and drops or limits the row instead:

* Firefly sets KSP's aerodynamic effects to their lowest, and warns whenever they are
  raised.
* Deferred caps KSP's reflection refresh and resolution at every scene load.
* Kopernicus can enforce a terrain shader quality level.

## Set by ReDefinition

These are not requirements of other mods. With Volumetric Clouds installed, every
profile sets TUFX's *Blackrack_TUFX* profile in every scene, through the `ALL_PROFILES`
block in TUFX's registration.

The antialiasing is not set in any profile. While the upscaler runs, ReDefinition
switches off KSP's MSAA, Scatterer's TAA and SMAA, Deferred's editor SMAA and TUFX's
antialiasing at run time, and holds Unity's MSAA at off so that KSP's own settings
screen cannot put it back in the middle of a flight. When the upscaler stops, each gets
back what it had.

## Dependencies, not requirements

Scatterer's screen-space reflections on the ocean need Deferred. Its legacy terrain
light shafts need its long-distance terrain shadows. The tooltips say so.
