# Seasons on Kerbin

ReDefinition Seasons is a mod of its own for a snow line on Kerbin that moves with the year.
It needs neither HarmonyKSP nor ZTheme nor ReDefinition. Parallax and EVE are optional.

This release holds the clock, the snow mask and the settings. It does not draw snow on the
ground, from orbit or in storms yet, so a game with only this release looks as before.

## Install it

1. Extract `ReDefinitionSeasons_<version>.zip` into the KSP folder, the one with
   `KSP_x64.exe`. `GameData/ReDefinition/Seasons` and `GameData/ReDefinition/Common` go into
   `GameData`.
2. If you use CKAN, install *ReDefinition Seasons* there. CKAN installs *ReDefinition Common*
   with it and suggests Parallax and EVE.

The full ReDefinition zip carries the same two folders. If you install ReDefinition, you do
not need this one.

## How the seasons run

* Only Kerbin has seasons. KSP has no axial tilt, so the season follows Kerbin's position in
  its orbit.
* A year is Kerbin's orbit period, 9 203 545 seconds or 426 days of 6 hours. The *year length
  factor* scales it: 0.1 makes the year a tenth as long.
* The two hemispheres are half a year apart. When the north has winter, the south has summer.
* The snow line is at 60 degrees latitude on average. In the midwinter of a hemisphere it
  is 42 degrees from the equator, and in its midsummer 78 degrees. Snow lies higher up a
  mountain first: the line moves 1.5 degrees toward the equator for each kilometre of
  height. The snow trails the sun by a few weeks of the year.
* The polar caps stay covered all year, from 78 degrees on.
* The numbers are design choices, not game data. They are in `src/Seasons/SnowMask.cs`.

At the real year length, a year passes in 106 Earth days of game time at 1x speed. The
seasons show only under time warp. Lower the *year length factor* if you want to see them
change without it.

## The settings

The file is `GameData/ReDefinition/Seasons/PluginData/settings.cfg`. The mod makes it when
it saves. There is no settings window for it yet, so edit the file with the game closed. A
value outside its range is brought into it, and a value that is not a number is ignored.

| Key | Default | Range | What it does |
|---|---|---|---|
| `enabled` | `True` | `True`, `False` | Switches the seasons on or off. |
| `yearLengthFactor` | `1` | 0.01 to 10 | Scales the year. 1 is the real Kerbin year. |
| `snowStrength` | `1` | 0 to 1 | How much of the snow cover the mask gives is shown. |
| `storms` | `True` | `True`, `False` | Switches snow storms on or off. |
| `stormFrequency` | `1` | 0 to 2 | How often storms happen, against the default. |
| `winterSnowLine` | `42` | 30 to 60 | The latitude of the snow line in a hemisphere's midwinter, in degrees. |

At the start the mod writes one line into `KSP.log`, beginning `[ReDefinition] Seasons:`. It
names the settings and whether Parallax and EVE were found.

## What works with what

| Other mod | What is known |
|---|---|
| Parallax | Planned: the seasons drive the ground colour that Parallax's terrain reads. Parallax's terrain shader has no snow or season input, and its licence is All Rights Reserved, so the seasons will not change its files. Without Parallax the stock terrain shaders are used. |
| EVE | Planned: snow storms with EVE's snow particles. The installed configuration already has polar snow clouds and snow particles, scheduled by time and not by season. Whether the upscalers' masks catch EVE's snow flakes is not tested. |
| Kopernicus, ModuleManager | Not required. |
| Scatterer | The ocean is Scatterer's. A frozen sea is not planned. |

Nothing of this has been run in the game yet. The year length and the snow line are
computed and tested outside the game. Whether the ground, the orbit view and storms look
right with Parallax, with stock terrain and under the upscalers is not verified.
