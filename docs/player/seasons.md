# Seasons on Kerbin

ReDefinition Seasons is a mod of its own for a snow line on Kerbin that moves with the year.
It needs neither HarmonyKSP nor ZTheme nor ReDefinition. Parallax is optional. The snow storms
are made for EVE with its volumetric clouds (the StockVolumetricClouds pack): the cloud, its
swirl seen from orbit and the falling snow are EVE's. The snow on the ground and in the orbit
view does not need EVE.

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
| `stormEveSnow` | `True` | `True`, `False` | Raises EVE's stock snow particle fields with a storm that no cell drives (a forced storm), and lets the own flakes step aside where EVE's snow field falls. |
| `stormEveRainToSnow` | `True` | `True`, `False` | Where the ground is snow or ice, replaces EVE's rain (rain particles, droplets on the glass, wet ground, lightning, rain sound) by its snow. |
| `stormCellRadiusKm` | `12` | 5 to 25 | The typical radius of a storm cell in kilometres. Each cell is 0.6 to 1.4 times this, always between 5 and 25 km. |
| `stormVolume` | `True` | `True`, `False` | Draws the veil of falling snow under the cloud of a storm cell that you can see from afar. The cloud is EVE's. |
| `stormClouds` | `Existing` | `Existing`, `Own` | Where the EVE cloud of a cell lives. `Existing` paints the cell into the coverage and cloud-type maps of the layers EVE already has on Kerbin, so the cell is part of EVE's weather, and into the picture of EVE's 2D layer from orbit. `Own` is a layer of its own that the mod's config adds. Where EVE's layers cannot carry the cell (a different EVE, other layers), the own layer is used and `KSP.log` says so. |
| `stormOwnFlakes` | `False` | `True`, `False` | Draws the mod's own falling snow in a storm also where EVE's snow field is falling. By default the own flakes only fill in where EVE has no snow field. |
| `stormCullDistance` | `False` | `True`, `False` | While a storm runs, does not draw the terrain farther away than the fog shows (5 times the visibility, at least 300 m), to save frame time. In the harness it saved 0.6 to 1.2 ms of about 24 inside a cell at intensities 0.35 to 1; outside a cell it does nothing. Off by default. |
| `stormVolumeSteps` | `28` | 16 to 48 | How many steps a ray takes through the snow veil of a cell. More is smoother and costs more. |
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

* A snow cloud: a thick, lobed, wind-stretched mass in EVE's own weather layers (`stormClouds =
  Existing`). It grows out of the clouds that are there while the storm builds, up to a dense
  Cumulus mass in Kerbin-base-layer (from 1.75 km above sea level, taller than its Stratus deck)
  and, where EVE's weather layers are active, a Polar-snow cloud below it. Because it is EVE's cloud, it has the noise, the
  lighting and the colours of the clouds around it. Without EVE's volumetric clouds there are no
  storm cells at all: `KSP.log` says `storms: needs EVE with Volumetric Clouds` once, and the
  snow on the ground and in the orbit view go on as before.
* From 150 km up and in the map view the cell is a swirl on EVE's 2D cloud layer (below).
* A broad, soft veil of falling snow from the cloud base down to the ground. It is leaned by
  the wind, thicker toward the middle, fades in from the cloud base and is lit grey-white.
* Where the shader bundle is missing, the veil is not drawn; the cloud stays.
* The cell fades in and out with the storm and is darker at night. It stands on the ground,
  turns with the planet and moves with its drift. Fly toward it and into it.

When you are inside a cell, the storm works on the whole picture. What a storm does on screen:

* The view gets short. The strongest storm leaves about 60 m of sight (`stormVisibility`); a
  half-strength storm about 250 m. This comes from Unity's fog and a veil in front of the
  camera.
* The sky turns grey and heavy. Toward the horizon and the ground the veil is light fog; toward
  the zenith it is a dark storm grey. The ambient light is lowered with it, by up to 60 % at
  full strength. `stormSkyDarkening` sets how much (0 leaves the sky and the light alone).
* Snow falls around the camera. In a cell it is EVE's snow particle field (`ReDefinition-Seasons-snow`,
  the stock `snow-Kerbin` with 600 000 instead of 400 000 flakes, a little larger and faster),
  and EVE's snow sound plays. The field falls where the cell's coverage is: it thickens from the
  rim to the middle and stops outside the cell. It has its own layer from the ground to the
  top of the cloud, because a volume gives no coverage below its base. The mod's own flakes
  (soft round discs in three depth layers) are the fallback where EVE has no snow field;
  `stormOwnFlakes` draws them as well. The veil is a little heavier toward the horizon, so
  that the distant ground is whited out.
* Where a storm is forced without a cell (`forceStorm`), EVE's stock snow fields (`snow-Kerbin`)
  are raised with the storm instead, so that the snow is also thick in the distance.
  `stormEveSnow` switches that off.
* With EVE installed, rain does not fall over snow and ice. Where the season's snow cover at
  your place is above one half (the polar region in winter, high mountains), EVE's rain
  particles, the droplets on the glass, the wet ground, the lightning and the rain sound are
  switched off. EVE's snow field and snow sound take their place with the strength the rain
  would have had. Below a cover of 0.45 the rain comes back. `stormEveRainToSnow` switches
  that off. This does not need a storm. It was read from the code of the installed EVE (the
  names and what each effect reads), but it has not been seen in the game yet. Water already
  on the glass dries within about twelve seconds, ground EVE has made wet stays wet until EVE
  dries it, and the rain sound in the IVA view is not replaced by snow sound.

The storm adds no screen effect of its own, so it does not interfere
with the upscalers or raytracing of ReDefinition (more below). The storm changes the global fog setting
and the ambient light of the game for its length and puts them back when it ends or you
leave the flight scene. `KSP.log` has a line `Seasons: storm N begins` with the way it is
drawn and what EVE offered (`EVE snow field: found ... / not found / not controllable`),
and `storm settled` lines with the visibility, the veil, the ambient light and the number
of flakes.

The cloud in EVE's existing layers (`stormClouds = Existing`, the default): the mod reads the
installed StockVolumetricClouds layers of Kerbin. A layer with a Polar-snow type (Kerbin-Weather-1
and 2, low, faded in and out by EVE's schedule) and a layer with a low Stratus type
(Kerbin-base-layer, always there) carry the cell; the convective layer and the stock snow
particle layers are left alone. While a cell is within 150 km the mod keeps a working copy of
each of those layers' coverage and cloud-type maps with the cell painted into it (a screen blend
of the coverage, the type turned to Polar-snow, or to Cumulus in the base layer), puts the copies
in the layers, and puts EVE's own maps back 30 seconds after the last cell has gone, when the
setting goes off, or when the scene unloads. The cell is Cumulus in the base layer, EVE's dense
type (eight times the density of the Stratus deck, taller), so that it is a darker, heavier mass
than the overcast around it. Only the rectangle a cell covers is written, and only when a cell
has moved by a texel or changed its strength. This costs memory while a cell is near: 128 MB
for the 8192 x 4096 base layer (an earlier version took 256 MB and a brief extra 128 MB; `KSP.log`
says how much) and an upload of some hundred KB a paint instead of 64 MB a second. The log
lines `storm clouds in EVE's layers: ...` say which layers were found, what
EVE reads back from the painted cell (`SampleCoverage`, seen 0.952 in the base layer, 1 in
Kerbin-weather-2 and 0 in Kerbin-Weather-1 at the cell's centre; the log says EVE had Weather-1
switched off at that moment and does not update it then, which its own schedule (`timeSettings`) does
for the two weather layers in turn), and the ground height under a cell; a cell over ground
higher than the top of the layer (3450 m) is logged once, as its cloud is under the ground
there. If the lookup fails, the log says so and the own layer below is used.

The own layer (`stormClouds = Own`, and the fallback): the layers `ReDefinition-Seasons-Storm` (the
cloud) and `ReDefinition-Seasons-Snow` (the snow) are defined by a config of this mod that
ModuleManager turns into EVE's config when StockVolumetricClouds is installed. Their coverage map is
painted by the mod twice a second from the cells within 150 km of the camera, into a map of
4096 x 2048 texels (0.92 km each), and the mod checks that EVE reads the coverage where it was
painted (`KSP.log`: `storm layer calibration: ...`). The layer does not move with the wind: the
cell is a place on the ground. It is hidden while no cell is near. If EVE has only 2D cloud
layers there are no cells; if the layer is not found or if the calibration fails, the line
`storm layer: ...` in `KSP.log` says so and the cell has no cloud. How far away the cloud can be seen is
decided by EVE's own raymarching settings (the layer uses the step sizes of the installed
Weather layers); it has not been measured for a 25 km cell.

In orbit and in the map view EVE shows its 2D cloud layer, which fades in from 200 km up. With
`stormClouds = Existing` the mod swaps the picture of the base layer's 2D layer (16384 x 8192,
which EVE keeps) for a render texture of 4096 x 2048 (44 MB of video memory) that holds the
original picture with the swirl of every cell of the planet drawn over it, at least 40 km in
radius, with the layer's own rotation. The cause of the swirl being invisible before was found:
EVE's cloud shader multiplies the picture's colour by 2 and by its lighting, so every grey above
a quarter was full white. The swirl is now drawn by a shader of the mod's bundle that replaces the
picture's colour and opacity instead of mixing with them: it first clears the clouds of the
picture under the swirl, so that the ocean shows between the arms, then paints white arms
and a dark blue-grey centre. Seen at 1000 km over 55 N (a forced 25 km cell, picture
in the harness): a recognisable spiral, a white band curling round a blue centre with a dark
lane, about 50 pixels across. In the map view the same ring is visible, as large. Not seen: the
second arm clearly (the red marker of the harness shows one broad arm and a ring), and the
spiral over open ocean. Where the picture's own cloud is thick and white,
as west of the cell in that picture, the spiral blends into it. The swirl is the display size of
the storm system, not the 25 km cell's own size.

The own layer's version of this: the cloud's OBJECT has a 2D layer, and the mod paints every
cell of the planet into its picture (2048 x 1024, 1.8 km a texel) as a swirl: a closed core and two arms, white-grey, as
big as the cell and at least 4.6 km in radius, so that a small cell does not vanish. The
picture is drawn after EVE's other clouds (render queue 3000). Seen: the swirl is in the
right place and has the cell's size (checked with a solid red marker, 1000 km up, over 55 N;
the marker is the diagnostics switch of the harness, not a setting). Not seen: whether the
spiral reads well. From 1000 km a 25 km cell is a bright, soft patch that whitens the cloud
cover under it (the mean luminance within 10 pixels of its centre rose from 197 to 231), not a
visible spiral. From a lower orbit, or in the map view, it is bigger on the screen.

Limits of the cells: a raymarched cell is one transparent object with single scattering from
the sun and the sky, not a simulation, and it does not light anything else. EVE's cloud layer
takes the cells' places from the mod, but EVE does not know the cells: its rain and its other
layers are as EVE draws them. A cell makes no sound. Two cells that overlap on the screen sort
by object, so they can show a seam. The shader reads the scene's depth to stop at the ground
and at objects; if the game does not provide it, the veil shows through hills. The cloud base
of the EVE layer is a height above sea level (500 m), so over high ground the cloud is closer to
the ground than over low ground. The cells seen from outside, the
raymarched shader, its depth test and the EVE layer have run in the game in the harness (forced
cells, one place, Medium profile) but not in play. The veil is a translucent grey-white curtain
with soft vertical streaks under the cloud base, at most 0.65 opaque, so that trees
behind it show, with a thin mist between the streaks that veils the horizon. A dark band
at the cloud base darkens the cloud's underside; it grows with the storm's strength and is
absent in a weak one (harness picture at strength 0.3: a grey cell, a light curtain, sparse flakes
inside). The density (extinction 0.02 per metre) was chosen from pictures at 0.6, 1 and 1.5 times.
Seen in the harness: at 8 km the curtain is a pale, streaked veil under a dark cloud; at 25 km,
against a cleared sky, a pale band under the cloud that veils the trees behind it, with a thin dark
line at the cloud base that looks harder than it should. Against the overcast sky of the test
place the 25 km cell is still mostly told by the cloud, not by the curtain. By night the veil is
dark blue-grey and does not glow; at a low sun it is warm. The 35 degree sun that was asked for
cannot be reached at 62 N (Kerbin has no axial tilt, the noon sun is at 28 degrees), so the high
sun was 28 degrees.

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
| EVE | Needed for the look of the storms: EVE with its volumetric clouds (the StockVolumetricClouds pack). By default the snow cloud of a cell and its 2D swirl are painted into the layers EVE already has (`stormClouds = Existing`); with `Own` they are layers of this mod's config. The snow field is a layer of the mod's config in both. The installed configuration has polar snow clouds and snow particles scheduled by time and not by season; the seasons do not change those schedules. In a forced storm without a cell the seasons raise the coverage value of EVE's stock snow particle fields (`snow-Kerbin`) to the storm's strength, by reflection; `KSP.log` says what was found. EVE's own files are not changed. |
| Scatterer | The ocean is Scatterer's. A frozen sea is not planned. Scatterer switches the ambient light off for its own camera while it renders; the storm's lowered ambient light is written before that and is not touched by it. |
| ReDefinition | The storms add no image effect and no command buffer, so the upscalers, frame generation and raytracing run as before. What the upscalers do with the storm is in the next section. |
| Kopernicus, ModuleManager | Not required. Kopernicus bodies keep the shader they have; if the scaled planet's material has no colour map the mod can read, the orbit snow stays off and the log says so. |

## Seasons and the upscalers

The storm draws three kinds of things. Where each goes in ReDefinition's upscaler masks:

* The raymarched cell (the mod's own volume, shader `ReDefinition/SeasonsStormVolume`) is not
  in the reactive mask. The mask draws a flat copy of each transparent renderer's texture and
  tint, and the volume is a bounding cylinder whose shading is a ray march: a flat copy would mark
  the whole cylinder as one hard patch. It is soft and moves slowly, so the upscaler's own
  history handles it. The name of its shader contains `ReDefinition`, which the mask skips on
  purpose.
* The screen-sized whiteout veil uses the game's particle shader (`KSP/Particles/Alpha
  Blended`, queue 3000), but the mask skips it by the name of its object (`ReDefinition Seasons
  storm veil`, the list `NotReactiveObjects` in `UpscalerMasks.cs`): marked, it would put the whole
  picture into the mask. The mod's own flakes use the same shader and the mask does see them.
* EVE's snow particle field (`EVE/ParticleField`, queue 3200) is skipped by the reactive mask:
  its vertex shader places every flake and all four vertices of a quad share one position, so a
  flat copy would draw nothing and cost the vertices of 400 000 or more quads. This was read from
  EVE's code. A harness run with FSR 3 and the mask on (Medium profile, one place) logged, in
  front of a cell 8 and 25 km away, a mask that marks 0.02 % of the picture (51 draws from 154
  transparent renderers), so neither the cell, EVE's cloud nor EVE's snow is in it. Inside the
  cell the whiteout veil marked the whole picture at a mean weight of 0.88 before it was skipped;
  with the skip, a run with FSR 3, *Renderers* and frame generation off marked 0.02 % inside the
  cell too (48 draws from 151 renderers). Whether the mask's scan picks up EVE's
  2D cloud meshes (`EVE/Cloud`) is not checked.

The reactive mask source *Renderers* is Off by default in ReDefinition and is only drawn when the
upscaler is FSR (or the card is AMD's; `UpscalerRig.cs`). DLSS and XeSS take no reactive mask.
So, honestly: with DLSS or XeSS there is nothing to switch on, and the falling snow has no motion
vectors, so it can smear or ghost a little. The same goes for the veil and the cell's volume
under DLSS and XeSS: they have no mask there and are left to the upscaler's own history. How
that looks in motion has not been judged. With FSR and a storm, switch *Reactive mask* to
*Renderers* in the ReDefinition diagnostics window (page *Masks*) to give the
mod's own flakes a reactive weight; EVE's snow field gets none either way. How the storm looks in
motion under FSR with the mask has not been judged; the setting is the one to try first if the
storm ghosts under FSR.

## Limits

What has been seen in the game: the snow on the ground in winter and in summer, the tint of the
planet in the orbit and map view, and a storm's fog, veil, falling snow, dark sky, lowered
ambient light and EVE's snow field. Seen in the game with the cell inside EVE's existing layers: the layers are
found, EVE's `SampleCoverage` reads the painted cell (0.943 in the base layer), a cell 8 km away
is a dense, convoluted EVE cloud with EVE's texture, and inside it the sky is dark and white with
EVE's snow field. A cell 25 km away is not told from the overcast sky of that place, and the
veil, which a probe showed is drawn, blends with the sky. Seen in the game with the own cell layers: the layers are found
and bound, the calibration reads the painted coverage back (centre 0.735 against 0.817 painted),
a forced cell 8 km away is a bright white EVE cloud on the horizon, and inside it EVE's snow
particle field falls (small flakes and a few large ones, a moderate fall rather than a blizzard).
In orbit the cell is in the right place with the right size, as a bright soft patch (a 25 km cell at 1000 km
is about 60 pixels wide; its spiral is not resolved at that distance). What has not been seen
in the game yet: a cell from 25 km away (the cloud is not told from the sky there), the snow
veil and the raymarched shader next to EVE's cloud, the swirl from a lower orbit, the swirl
in the map view, the replacement of EVE's rain by snow, the sound of the snow, and the `Rebuild` mode. The year length, the snow line, the noise, the
schedule and the numbers of the storms are computed and tested outside the game. How the snow
looks under the upscalers and raytracing, and whether the terrain refresh reaches Parallax's
grass, is not verified. Not done:

* The orbit snow assumes the layout of the colour map (south at the bottom, longitude as in the
  terrain); the test compares winter with summer in orbit and fails if the hemispheres are
  swapped.
* Parallax's rocks and grass are not removed or changed in form. The grass takes the snow
  colour where it takes the colour of the ground.
* The ocean is not frozen.
