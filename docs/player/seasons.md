# Seasons on Kerbin

ReDefinition Seasons is a mod of its own for a snow line on Kerbin that moves with the year.
It needs neither HarmonyKSP nor ZTheme nor ReDefinition. Parallax and EVE are optional.

This release paints the snow on Kerbin's ground, tints the planet in the orbit and map view
with the same season, and has snow storms in flight.

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

The file is `GameData/ReDefinition/Seasons/PluginData/settings.cfg`. The mod writes it with
the defaults the first time it starts and finds none. There is no settings window for it yet,
so edit the file with the game closed. A value outside its range is brought into it, and a
value that is not a number is ignored.

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
| `stormSkyDarkening` | `0.6` | 0 to 1 | How dark the sky and the ambient light get in the strongest storm. 0 leaves both alone; the sky has its full grey from 0.67. |
| `stormEveSnow` | `True` | `True`, `False` | Raises EVE's own snow particles with the storm where EVE has them. |
| `stormEveRainToSnow` | `True` | `True`, `False` | Where the ground is snow or ice, replaces EVE's rain (rain particles, droplets on the glass, wet ground, lightning, rain sound) by its snow. |
| `stormCellRadiusKm` | `12` | 5 to 25 | The typical radius of a storm cell in kilometres. Each cell is 0.6 to 1.4 times this, always between 5 and 25 km. |
| `stormVolume` | `True` | `True`, `False` | Draws a storm cell as a snow curtain under a dark cloud base that you can see from afar. |
| `stormVolumeMode` | `Auto` | `Auto`, `Raymarch`, `Simple` | `Auto` draws a cell as a raymarched volume where the shader bundle `Seasons/Shaders/seasons.shaders` loads, and as plain meshes where it does not. `Raymarch` draws no cell without the shader; `Simple` always draws the meshes. |
| `stormVolumeSteps` | `28` | 16 to 48 | How many steps a ray takes through a raymarched cell. More is smoother and costs more. |
| `stormMaxCells` | `6` | 1 to 12 | How many cells are drawn at once, the nearest ones. |
| `forceStormCell` | `off` | `off`, or `latitude,longitude,radiusKm` | A diagnostics setting for screenshots: one storm cell at that place at full strength, whatever the schedule, the season and the `storms` switch. Leave it at `off` to play. |
| `orbitSnow` | `True` | `True`, `False` | Tints the planet in the orbit and map view with the season. |
| `orbitSnowWidth` | `2048` | 1024 to 4096 | Width in pixels of the map the orbit snow draws with, never larger than the game's own map. 2048 needs 29 MB of video memory, 4096 needs 117 MB. |
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

A storm is a cell: a place on Kerbin with a radius of 5 to 25 km that lives for a few Kerbin
hours and drifts slowly with the wind. Cells follow a fixed schedule that depends on the game's
time, the season and the `stormSeed`, so the same cell stands at the same place for everyone
with the same seed, and you can see it come, from a distance and from the air. Cells happen
only where there is snow cover and the hemisphere is in its winter: not near the equator and
not in the summer, also not on the polar caps. Several cells stand on the planet at once. In a
winter you will find one within 60 km of you now and then, not all the time.

A cell builds up over one and a half to three Kerbin hours, stays at its peak for two to eight,
and fades over two and a half to six. It drifts east at 1.5 to 4 m/s, turned a little to either
side, so it travels 5 to 15 km an hour. Time is counted in Kerbin hours, so a shortened year
has fewer cells. The storm is full strength inside the cell, falls off over the outer fifth of
the radius, and is gone outside it. Higher up the storm fades: it is at full strength up to
4 km above sea level and gone from 10 km.

What you see of a cell from outside, within 60 km of the camera and while `stormVolume` is on:

* A dark layer of storm cloud 700 m thick on a base 1.5 to 2.5 km above the ground, darker at
  the bottom and lit on top.
* A curtain of falling snow from the cloud base down to the ground, in vertical streaks that
  move, denser toward the middle with a ragged edge.
* Where the shader bundle is missing, the cell is drawn as plain meshes instead: a flat cloud
  base with a few grey puffs and three layers of streaks.
* The cell fades in and out with the storm and is darker at night. It stands on the ground,
  turns with the planet and moves with its drift. Fly toward it and into it.

When you are inside a cell, the storm works on the whole picture. What a storm does on screen:

* The view gets short. The strongest storm leaves about 60 m of sight (`stormVisibility`); a
  half-strength storm about 250 m. This comes from Unity's fog and a veil in front of the
  camera.
* The sky turns grey and heavy. Toward the horizon and the ground the veil is light fog; toward
  the zenith it is a dark storm grey. The ambient light is lowered with it, by up to 60 % at
  full strength. `stormSkyDarkening` sets how much (0 leaves the sky and the light alone).
* Snow falls around the camera as soft round flakes of different size and brightness. They
  fall slowly, about a metre a second, and sway on their own. The wind drifts them sideways,
  turns slowly and gusts. A light fall has few small flakes; in a strong storm there are more
  and larger ones. Near flakes are large and translucent, far ones small, so the snow has
  depth. The flakes are never stretched into streaks.
* With EVE installed, its own snow particle field (`snow-Kerbin`) is raised with the storm too,
  so that the snow is also thick in the distance. `stormEveSnow` switches that off.
* With EVE installed, rain does not fall over snow and ice. Where the season's snow cover at
  your place is above one half (the polar region in winter, high mountains), EVE's rain
  particles, the droplets on the glass, the wet ground, the lightning and the rain sound are
  switched off. EVE's snow field and snow sound take their place with the strength the rain
  would have had. Below a cover of 0.45 the rain comes back. `stormEveRainToSnow` switches
  that off. This does not need a storm. It was read from the code of the installed EVE (the
  names and what each effect reads), but it has not been seen in the game yet. Water already
  on the glass dries within about twelve seconds, ground EVE has made wet stays wet until EVE
  dries it, and the rain sound in the IVA view is not replaced by snow sound.

This works without EVE. It adds no screen effect of its own, so it does not interfere
with the upscalers or raytracing of ReDefinition. The storm changes the global fog setting
and the ambient light of the game for its length and puts them back when it ends or you
leave the flight scene. `KSP.log` has a line `Seasons: storm N begins` with the way it is
drawn and what EVE offered (`EVE snow field: found ... / not found / not controllable`),
and `storm settled` lines with the visibility, the veil, the ambient light and the number
of flakes.

Limits of the cells: a raymarched cell is one transparent object with single scattering from
the sun and the sky, not a simulation, and it does not light anything else. EVE's clouds are
not hooked into it: a cell has its own cloud layer, and EVE's clouds above it are as EVE draws
them. A cell makes no sound. Two cells that overlap on the screen sort by object, so they can
show a seam. The shader reads the scene's depth to stop at the ground and at objects; if the
game does not provide it, the curtain shows through hills. The cells seen from outside, the
raymarched shader and its depth test have not run in the game yet.

Limits: the fog is Unity's. The Parallax terrain shader applies it; whether the stock
terrain, the parts and Scatterer's sky do is not tested, which is why the veil is there: it
lowers the contrast of the whole picture, near and far. The sun's own light is not dimmed,
only the sky and the ambient light. KSP may rewrite the ambient light every frame; the storm
writes its value just before the cameras render and takes a changed value as the new
starting point. EVE has no setting for its snow particles, so the storm writes the value
EVE keeps for its coverage; if a future EVE changes that, `KSP.log` says
`not controllable` and only the storm's own snow falls. The falling snow has no motion
vectors, so an upscaler or frame generation may smear it.

## Snow in orbit and in the map

From orbit and in the map view Kerbin is the scaled planet, drawn with one colour map. The
mod copies that map into a texture of its own, draws the snow of the season over it, and
gives the planet that texture. The snow is computed with the same function and the same
noise as the ground, so the line on the ground and in orbit is the same line, and it moves
with the year the same way. The game's own texture is not changed, and it is put back when
the game ends. With Parallax the map is its `_ColorMap`; without it, the stock scaled
planet's `_MainTex`. `KSP.log` names the shader and the property it found
(`Seasons: orbit snow: ...`).

* The map is composed again when the season has moved by about a week of the year, at most
  every 30 seconds, and sooner after a large jump. Under high time warp the line moves in
  the map as it does on the ground.
* The relief that raises the snow line comes from a coarse grid of Kerbin's heights
  (256 x 128 samples), read once in the first seconds.
* It costs video memory for two copies of the map: 117 MB at the full 4096 x 2048, 29 MB
  at 2048 (`orbitSnowWidth`). The work of the mask runs on a second thread.
* `orbitSnow` switches it off.

## What works with what

| Other mod | What is known |
|---|---|
| Parallax | The ground snow is the vertex colour that Parallax's terrain reads; its terrain shader has no snow or season input. Parallax's licence is All Rights Reserved, so the seasons change none of its files; they call it by reflection, to tell it that a terrain mesh changed and to give its grass the new colours. If a Parallax build lacks what is called, `KSP.log` says so once and that part is left out. Without Parallax the stock terrain shaders show the snow. |
| EVE | Not needed for the storms. The installed configuration has polar snow clouds and snow particles scheduled by time and not by season; the seasons do not change those schedules. During a storm the seasons raise the coverage value of EVE's snow particle field (`snow-Kerbin`) to the storm's strength, by reflection; `KSP.log` says what was found. EVE's own files are not changed. |
| Scatterer | The ocean is Scatterer's. A frozen sea is not planned. Scatterer switches the ambient light off for its own camera while it renders; the storm's lowered ambient light is written before that and is not touched by it. |
| ReDefinition | The storms add no image effect and no command buffer, so the upscalers, frame generation and raytracing run as before. The upscalers' masks may not catch the falling snow (not tested). |
| Kopernicus, ModuleManager | Not required. Kopernicus bodies keep the shader they have; if the scaled planet's material has no colour map the mod can read, the orbit snow stays off and the log says so. |

## Limits

What has been seen in the game: the snow on the ground in winter and in summer, the tint of the
planet in the orbit and map view, and a storm's fog, veil, falling snow, dark sky, lowered
ambient light and EVE's snow field. What has not been seen in the game yet: the storm cells
from outside (the cloud base and the snow curtain), the raymarched shader, the replacement of
EVE's rain by snow, and the `Rebuild` mode. The year length, the snow line, the noise, the
schedule and the numbers of the storms are computed and tested outside the game. How the snow
looks under the upscalers and raytracing, and whether the terrain refresh reaches Parallax's
grass, is not verified. Not done:

* The orbit snow assumes the layout of the colour map (south at the bottom, longitude as in the
  terrain); the test compares winter with summer in orbit and fails if the hemispheres are
  swapped.
* Parallax's rocks and grass are not removed or changed in form. The grass takes the snow
  colour where it takes the colour of the ground.
* The ocean is not frozen.
