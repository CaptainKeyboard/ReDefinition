# Seasons on Kerbin

ReDefinition Seasons is a mod of its own for a snow line on Kerbin that moves with the year.
It needs neither HarmonyKSP nor ZTheme nor ReDefinition. Parallax and EVE are optional.

This release paints the snow on Kerbin's ground and has snow storms in flight. It does not
tint the orbit and map view.

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
| `stormSeed` | `1` | any whole number | The seed of the storm schedule. Another seed gives other storm times. |
| `stormVisibility` | `60` | 20 to 500 | How far you see in the strongest storm, in metres. |
| `stormVeil` | `0.85` | 0 to 0.95 | How white the screen gets in the strongest storm. |
| `stormSnowfall` | `True` | `True`, `False` | Draws falling snow in a storm. |
| `forceStorm` | `-1` | -1, or 0 to 1 | A diagnostics setting for screenshots: a fixed storm strength at the camera, whatever the place, season and `storms` switch. Leave it at -1 to play. |
| `winterSnowLine` | `42` | 30 to 60 | The latitude of the snow line in a hemisphere's midwinter, in degrees. |
| `groundRefreshMode` | `Live` | `Live`, `Rebuild`, `Off` | How ground that is already built follows the season (below). |
| `refreshSeconds` | `5` | 1 to 120 | Real seconds between two passes of the `Live` mode. |
| `forcePhase` | `-1` | -1, or 0 to 1 | A diagnostics setting for screenshots: a fixed season phase instead of the clock. 0 is the northern midwinter, 0.5 the northern midsummer. Leave it at -1 to play. |

## Snow on the ground

Kerbin's terrain gets its snow when the game builds it: the mod turns the colour of each
terrain vertex toward white by the snow cover for its latitude, height and a little noise, so
that the line is not straight. The terrain shaders of the game and of Parallax both read this
colour, so the snow shows with and without Parallax. The mod does not change a shader or a
texture of another mod. Rocks and the relief that Parallax draws on top still show through, and
the cover is flat white: there is no snow on slopes only, no sparkle and no deeper snow.

Terrain that is already built does not know that the season moved. The *ground refresh mode*
decides what happens:

* `Live` paints the built terrain again every `refreshSeconds`, a few quads per frame, and
  tells Parallax that the terrain changed, so that its terrain and its grass take the new
  colour. This is the default. Under high time warp the snow line visibly moves.
* `Rebuild` builds Kerbin's terrain anew when the season has moved by about a week of the
  year. It does so only where no vessel stands on the ground: in flight with the planet out of
  range, or in the tracking station. It never does in the space centre. A vessel on the ground
  keeps the terrain it has until it leaves.
* `Off` leaves built terrain alone; only terrain that is built later has the new season.

At the start the mod writes one line into `KSP.log`, beginning `[ReDefinition] Seasons:`. It
names the settings and whether Parallax and EVE were found.

## Snow storms

Storms follow a fixed schedule that depends on the game's time, your latitude, the season and
the `stormSeed`. They happen only where there is snow cover and the hemisphere is in its
winter: not near the equator and not in the summer, also not on the polar caps. In a real-length
year there are about six at 60 degrees latitude. A storm builds up over one to three Kerbin
hours, stays at its peak for two to eight, and fades over two and a half to six. Storms are
counted in Kerbin hours, so a shortened year has fewer of them. Higher up the storm fades, and it
is gone above 10 km.

What a storm does on screen:

* The view gets short. The strongest storm leaves about 60 m of sight (`stormVisibility`); a
  half-strength storm about 250 m. This comes from Unity's fog and a white veil in front of the
  camera.
* Snow falls around the camera, blown by a wind that turns slowly.

This works without EVE. It adds no screen effect of its own, so it does not interfere with the
upscalers or raytracing of ReDefinition. Fog changes the global fog setting of the game for the
length of the storm and puts it back when the storm ends or you leave the flight scene. `KSP.log`
has a line `Seasons: storm N begins` with the way it is drawn, and `storm settled` lines with the
visibility.

Limits: the fog is Unity's. The Parallax terrain shader applies it; whether the stock terrain,
the parts and Scatterer's sky do is not tested, which is why the veil is there: it lowers the
contrast of the whole picture, near and far. The sky and the light are not darkened. EVE's snow
particles are not used; EVE's own polar snow clouds keep their schedule. The falling snow has no
motion vectors, so an upscaler or frame generation may smear it. None of this has run in the game
yet.

## What works with what

| Other mod | What is known |
|---|---|
| Parallax | The ground snow is the vertex colour that Parallax's terrain reads; its terrain shader has no snow or season input. Parallax's licence is All Rights Reserved, so the seasons change none of its files; they call it by reflection, to tell it that a terrain mesh changed and to give its grass the new colours. If a Parallax build lacks what is called, `KSP.log` says so once and that part is left out. Without Parallax the stock terrain shaders show the snow. |
| EVE | Not needed for the storms. The installed configuration has polar snow clouds and snow particles scheduled by time and not by season; the seasons do not change them. Using EVE's snow particles for the storms is open. |
| ReDefinition | The storms add no image effect and no command buffer, so the upscalers, frame generation and raytracing run as before. The upscalers' masks may not catch the falling snow (not tested). |
| Kopernicus, ModuleManager | Not required. |
| Scatterer | The ocean is Scatterer's. A frozen sea is not planned. |

## Limits

Nothing of this has been run in the game yet. The year length, the snow line, the noise and the
repaint of a quad's colours are computed and tested outside the game. Whether the terrain
refresh reaches Parallax's terrain and grass in the game, and how the snow looks under the
upscalers and raytracing, is not verified. Not done:

* The orbit and map view of the planet keep their summer colours.
* Parallax's rocks and grass are not removed or changed in form. The grass takes the snow
  colour where it takes the colour of the ground.
* The ocean is not frozen.
* The snow storms are tested outside the game (schedule and numbers) but not yet in it.
* Rebuilding in `Rebuild` mode is not tested.
