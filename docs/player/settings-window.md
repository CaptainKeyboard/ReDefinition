# The settings window

**For:** players.
**You need:** ReDefinition installed ([installing.md](installing.md)).
**You get:** how to find every setting, apply it, reset it, and put the mods back the
way they were.

The window holds the upscaler, frame generation, the graphics settings of the
installed graphics mods, one row per feature, and every setting of KSP's own settings
screen, sorted by what a setting does. A mod that is not installed has no rows.

The graphics mods' settings are active once you choose a graphics profile. Until then
the upscaler and frame generation are off, and every mod keeps its own settings. KSP's
own settings can always be changed here, as in KSP's screen.

## Open it

| Way | Where |
|---|---|
| ReDefinition's toolbar button | every scene of a game, the first button of the row; not in the main menu, which has the entry below |
| The *ReDefinition* entry in the main menu | under *Settings* |
| The *ReDefinition* entry in the pause menu | what Escape opens in flight and in the space centre, under *Settings*; the editors and the tracking station have no such menu |
| KSP's own settings dialog | *Settings* in the pause menu, at the end of its graphics part: the rows there, and *All settings* for this window |
| A hotkey | once you set one in the *Controls* tab; none is bound at first |

The four hotkeys you can set are in the *Controls* tab, in this order: *Upscaler on or
off*, *Settings window*, *Diagnostics window* and *Camera list to the log*.

The window is built from KSP's own dialog elements, so a UI theme such as ZTheme themes
it as well. It edits copies of the values. At the bottom right it shows *Close* while
nothing is changed. A change brings *Accept* and *Cancel* in its place: *Accept* sets
what you changed, *Cancel* puts every row back, and neither closes the window. Once
nothing waits any more, *Close* is back. Clicks on the window do not reach the scene
behind it.

*Reset* is at the bottom left, on every tab.

If a change takes effect only at a start, a button beside *Reset* sets it and
starts KSP again, after asking. In the main menu it is *Apply and restart*. When a game
is loaded it is *Save and restart*, and it saves the game first, as KSP saves it. In
flight that works only when KSP allows saving at that moment; otherwise it says why and
does nothing. In the editors the vessel you are building goes into KSP's *Auto-Saved Ship*
first.

A question the window asks, and NVIDIA's download, appear over the window with the
screen behind it darkened. The window behind it takes no clicks until you answer.

At a large UI scale, such as more than KSP's own 200 %, the window and its questions
would reach past the screen's edges. They are then drawn as large as the screen allows,
so that their buttons stay in reach. They grow back once the screen has room for them.

## The pause button

In flight, map view included, in the space centre and in the tracking station, the
button in the window's title row holds game time while the window is open. It shows two
bars while the game runs and a triangle while it is held, so it shows what a click
does.

Closing the window lets the game run again, whether you close it with *Close*, with
Escape or with the toolbar button. Escape and the toolbar button drop changes that
wait for *Accept*. Only KSP's own pause menu keeps it
paused, since that pause is not this window's. Your choice is kept, so the next time
you open the window it holds the flight again.

In the main menu and the editors no game time runs, and the button is dead.

While a flight runs behind the window, it is thinner than KSP's own dialogs, and the
rows are written with a shadow under them so that they read against a bright sky or
snow. Its background is nearly opaque wherever nothing moves behind it: while it holds
the flight, and in the main menu, the space centre and the editors.

## The tabs

| Tab | Rows |
|---|---|
| *Gameplay* | KSP's gameplay settings in groups: *General*, *Flight*, *Part highlighting*, *Camera*, *Navball*, *EVA*, *Map view*, *System* -- confirmations, debris, physics and logging -- and *Making History* where it is installed. The mods' settings that change how you play, not how it looks: Distant Object's flares and their names and Persistent Rotation under *Flight*, and there ReDefinition's own switches for the HUD, the staging and the world buttons ([below](#switch-redefinitions-flight-features)); Parallax's scatter collisions and Scatterer's waves that move vessels under *Physics* |
| *Audio* | KSP's volumes and its sound normalizer; EVE's cloud ambience and thunder volumes |
| *Display* | KSP's screen resolution, full screen, V-Sync, frame limit and UI scale; Upscaler, Technique, Mode, DLSS model, Sharpness, Frame generation; the raytracing rows ([raytracing.md](raytracing.md)); *Low latency*, *NVIDIA DLSS files* and *DLSS-G on RTX 30/20* where the GPU can use them ([upscaler-and-frame-generation.md](upscaler-and-frame-generation.md)); ambient light boosts |
| *Graphics* | a status line, and one button per graphics profile with a sentence about it; its tooltip says what it sets |
| *Graphics*, *Detail* | groups: *Quality* -- KSP's render quality, texture quality, pixel light count and anti-aliasing; *Shadows / Reflections*; *Planets*, with *Grass on the lawns* under *Space centre*; *Effects* -- the graphics mods' features and KSP's own |
| *Controls* | KSP's keyboard layout, then every key binding: ReDefinition's own, the bundled mods' and KSP's. KSP's keys take a controller's buttons too |
| *Controls*, *Axes* | KSP's joystick and gamepad axes, each with a primary and a secondary binding: the axis, invert, sensitivity and deadzone |
| *Controls*, *Devices* | KSP's mouse wheel, Track IR and 6-DOF device |
| *Mods / Toolbar* | *Replace original settings*, *Bundle other mods here*, the list of bundled mods, *Hide all from toolbar* with a switch per mod under *Per mod*, *Restore settings from before ReDefinition*, under *Scaling* whether other mods' windows follow the UI scale ([ui-scaling.md](ui-scaling.md)), and under *Main menu* the fireworks and the flight to the space centre ([below](#the-main-menu-and-the-space-centre)) |

*Detail* is indented under *Graphics*, and *Axes* and *Devices* under *Controls*. They
are shown only while their parent or one of its tabs is open. A line sets *Mods / Toolbar* and
*Diagnostics* apart.

The search field above the tabs searches every setting by its name and its mod. Each
word is looked for on its own, in any order: *wobble camera* finds *Camera wobble
external*. While it holds text, the window shows every tab with a match, each under its
name, and only the matching rows; *x* empties it.

A blue dot before a row's name marks a change that waits for *Accept*, and a blue dot
after a tab's name a tab with such a change, there or under it. A yellow dot before a
row's name marks a value other than the chosen profile's; its tooltip then ends in
*Default:* and the profile's value.

In *Gameplay*, *Detail*, *Axes* and *Devices* the rows are in groups that open and fold,
as the sections of *Controls* do. The first group of each tab is open at first.

The status line on *Graphics* names the chosen profile, shows *Custom* where rows were
changed since, and says what still waits for *Accept*.

Each row's tooltip says what the setting does, and the grey name at its right which
mod holds it. A change that does not take effect at once is said in yellow beside
*Reset*, from the moment you make it until it arrives: from the next scene
on, or after a restart. Its tooltip names each setting that waits.

The *Diagnostics* button below the tabs opens the diagnostics window.

The upscaler and frame generation can only be changed while a graphics profile is
chosen.

## Switch ReDefinition's flight features

Under *Gameplay*, *Flight*, each of ReDefinition's additions to the HUD, the staging and
the editors has a switch of its own. All of them are on at first, and none needs a
graphics profile.

| Row | Does | Takes effect |
|---|---|---|
| *Altimeter switches to the ground* | the altimeter shows the height over the ground below 5 km and above sea level from 6 km ([flight-panel.md](flight-panel.md#the-rest-of-the-hud)) | at once |
| *Flight panel follows the situation* | the flight panel switches its profile with what the vessel does ([flight-panel.md](flight-panel.md#the-profile-follows-the-situation)) | at once |
| *Start flight in maneuver mode* | a flight starts in KSP's maneuver mode rather than its staging mode | next scene |
| *Buttons over the buildings* | the buttons over the space centre's buildings, the launch pad and the runway ([launch-and-return.md](launch-and-return.md)) | at once |
| *Round gauge* | the digital gauge in place of KSP's vertical speed dial ([navball.md](navball.md#the-round-gauge)). It needs *Speeds at the navball*, which shows the vertical speed in its place, and is greyed out without it | next flight |
| *Speeds at the navball* | *V* and *H* under the navball's SAS modes | next flight |
| *Docking marker on the navball* | the ring on the navball with a docking port as the target | at once |
| *Navball moves by dragging* | dragging the navball's show and hide button moves it ([navball.md](navball.md)) | next flight |
| *Flight panel profiles* | the flight panel's profiles in place of the maneuver mode's orbit tabs ([flight-panel.md](flight-panel.md)) | next flight |
| *Stage details in two columns* | the stage details and part icons in two columns ([stage-details.md](stage-details.md)) | next scene |
| *TWR at the stage's start and end* | the stage's TWR as it lights and as it burns out | at once |
| *Delta-v tools in the staging stack* | the delta-v button opens every stage's details, and the editors' compact window | next scene |
| *Tooltips on the HUD's buttons* | tooltips for the staging stack's buttons, the alarm clock's, the altimeter's mode switch and the navball's speed field, in KSP's wording | next scene |
| *Ask before resetting the staging* | the editors' button that resets the staging asks first | next scene |
| *Crew slot buttons in the staging look* | the crew portraits' buttons that add and remove a slot look like the staging stack's buttons | next flight |
| *Better Time Warp in the time panel* | only with Better Time Warp installed: its button as a square button at the end of the time panel's warp row, rather than where the mod draws it | next scene |

A switch that takes effect later shows the yellow note beside *Reset* until it does.

## The main menu and the space centre

Under *Mods / Toolbar*, *Main menu*:

| Row | Does |
|---|---|
| *Fireworks* | small fireworks on Kerbin's night side behind the main menu |
| *Zoom to the space centre* | from the main menu to the space centre as one camera flight: in to Kerbin, then down to the space centre, with the sun where your save has it. A click or a key on the way down ends it at KSP's own view |

Under *Graphics*, *Detail*, *Planets*, *Space centre*, *Grass on the lawns* grows
Parallax's grass on the space centre's lawns as well, where Parallax grows it under
them. The row needs Parallax.

## Replace KSP's settings screen

*Replace original settings*, at the top of *Mods / Toolbar*, sends every *Settings* button of
KSP to this window: the main menu's and those of the pause menus in flight and in the
space centre. ReDefinition then adds no entry of its own there. With it off, KSP's
buttons open KSP's screens, and the *ReDefinition* entry is below them. It takes
effect the next time a menu is built.

## Bind an axis

Under *Controls*, *Axes*, click an axis's button and move the stick, trigger or wheel on the
controller: the first axis that moves more than half its range is taken. Escape
cancels, *x* clears the binding. Sensitivity and deadzone work as in KSP's own input
screen. *Bind keys to this layout*, at the top of *Controls*, sets every one of KSP's keys
to the chosen keyboard layout's preset at *Accept*.

## Reach a mod's own window

A tab ends with an *Advanced* row where a bundled mod puts its own window there. With
the mods bundled today that is *Planets*, for Scatterer, EVE and Parallax, and
*Effects*, for Firefly, TUFX and Distant Object.

A button opens that mod's own window where the mod places it, with an X at its top
right to close it. Firefly's opens at the left edge of the screen. A change you make
there shows up in ReDefinition's rows. A change you make here shows up there once you
apply it.

While their settings are bundled here, the toolbar buttons of those mods are hidden.
Waterfall's button opens its effect editor rather than settings, so it stays, and so do
buttons on Blizzy's toolbar.

## Set a key

The *Controls* tab holds every binding in one place, in sections. *Mods* holds
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
4. Press *Accept*.

A binding holds up to two modifiers. A switch is grey while no key is set, and where the
binding already holds as many modifiers as it can. The modifiers come from the switches
rather than from the keyboard, because Windows turns AltGr into Ctrl and Alt at once. A
binding with right Alt answers to AltGr.

*Reset* puts every binding back to its default: ReDefinition's own, the
mods', and KSP's to the keys KSP ships, the same ones KSP's own reset sets.

KSP keeps two keys per binding, a first and a second, each without a modifier. Its
modifier key is a binding of its own. The mods' bindings and ReDefinition's take
modifiers.

If two rows hold the same combination in the same situation, both are shown in yellow,
and both still work. What counts as the same situation:

* A binding counts where its section does: flying a vessel, map view included, on EVA,
  or in the editor. *Mods* and *General* count everywhere. B that brakes a vessel and B
  that boards one on EVA never meet, so neither is marked.
* Within flight, KSP's own modes count too: Space stages, and in docking mode the same
  key translates, so those two never meet.
* Against one of KSP's bindings only the key counts, since KSP's fire on their key
  whatever modifiers are held: a mod's Ctrl+U meets KSP's U.
* Keys KSP itself ships twice, such as W for pitch and for driving a rover, stay
  unmarked while both are at KSP's default.

## Find out why a row is missing

Three things leave a mod out of the window:

* the mod is not installed;
* the installed build is one ReDefinition does not bundle. Plain EVE Redux without the
  volumetric clouds is such a build;
* *Bundle other mods here*, under *Mods / Toolbar*, is switched off. Then only
  ReDefinition's own rows and KSP's are in the window.

Which of the three it is, the game says: *Diagnostics*, *Other mods*, *Registrations*.
It lists every mod that is loaded and not bundled, with the reason.

## Where your change goes

A setting you change here is saved in its mod, the way that mod's own window saves it:

| Mod | Kept |
|---|---|
| KSP | in `settings.cfg`, as KSP's own settings screens save it |
| Scatterer, EVE, Parallax, Firefly | in their own config files, through their own save routines |
| TUFX, Distant Object | per save. Your choice counts for every save: it goes into the loaded save, and into every other save as that save loads. A change in their own windows becomes your choice here |
| Persistent Rotation | in its own config file in flight; the default reference body per save, with its vessels' data. A change outside flight reaches it in the next flight |
| Deferred, Waterfall | they have no save routine, so ReDefinition keeps the value in `bundled.cfg` and sets it at every start and scene change |

A value a mod cannot take yet waits in `bundled.cfg` and reaches the mod once it can
take it. EVE before its cloud settings are loaded is such a case, and so is a mod whose
settings exist only in flight.

Where each mod keeps its settings:
[how-each-mod-keeps-its-settings.md](../reference/how-each-mod-keeps-its-settings.md).

## Choose a profile, or reset

Choosing a profile fills the rows of every tab with its values, and *Accept* sets
them. A row you change afterwards makes the choice *Custom*. A profile sets what
costs frame time, and switches the upscaler on. What each profile sets:
[graphics-profiles.md](graphics-profiles.md).

*Reset* asks first and names what it resets. It fills the rows with every
setting's default, the settings without a row included, and then chooses High over
them, so that choosing High afterwards changes nothing. Those defaults are what the
mods' releases ship. With Volumetric Clouds installed, the defaults of EVE, Scatterer
and TUFX are the ones its author ships. KSP's settings go back to what KSP ships, as
KSP's own *Reset* sets them, its keys and axes included, with these exceptions:

* ReDefinition's own defaults for KSP: *Advanced tweakables*, *Extended burn indicator*
  and *Always show closest approach* on, *Conic patch limit* at 6, and the UI scale for
  your screen: 100 % below 1440 lines, 150 % from 1440, 200 % from 2160;
* what an installed mod requires of KSP's settings;
* the screen resolution and full screen, which stay as they are: KSP's own reset would
  set 1280 x 720 in a window.

The question *Reset* asks names each of KSP's settings it sets to something other than
what KSP ships.

ReDefinition's own settings go back to their defaults, with frame generation off. A few
stay as they are, since they are your hardware's or your choice rather than a quality:
the upscaling technique, the raytracing mode, its light bounces and Ray Reconstruction,
*Replace original settings*, the window's pause button, the navball's place, the
diagnostics window's pins, and whether the upscaler is on once a profile is chosen.

After a reset, High is chosen: the upscaler runs at *AA only*, frame generation is
off, and KSP's settings are as above, except where High and the installed mods ask for
more: full textures, the highest terrain detail and shaders, and reflections at Low.
Every default and its source:
[reference/mod-defaults.md](../reference/mod-defaults.md).

## Put the mods back as they were

Before ReDefinition changes a setting for the first time, it writes that setting's
current value into `bundled.cfg`. For TUFX and Distant Object it does so once per save.

*Restore settings from before ReDefinition*, under *Mods / Toolbar*, asks first, puts
every one of those values back, saves each in its mod and clears the profile. A save
that is not loaded gets its values back the next time you load it. The button is greyed
out while changes wait for *Accept*.

When you choose another profile, a setting the previous profile set and the new one
does not goes back to its value from before ReDefinition, unless you changed it since.

*Bundle other mods here*, under *Mods / Toolbar*, switched off gives the mods back
their toolbar buttons, lets each mod's own window decide again, and clears the profile.
What was saved in the mods stays. What only ReDefinition kept is dropped: the choices
for every save, and Deferred's and Waterfall's settings.

### Which mods keep their toolbar button

*Hide all from toolbar*, under *Mods / Toolbar*, decides it for every mod at once, and
*Per mod* under it folds out one switch per mod. A mod is in that list where it has a
button of its own and ReDefinition's *Advanced* button opens its window in its place;
the others have nothing to hide and are not listed. Switched off, a mod keeps its button
and its settings stay in this window all the same. The choices wait for *Accept*, like
every other row, and the main menu's panel offers the same switches. Better Time Warp
Continued is in the list too, bundled or not: the arrow at the end of the time warp row
opens its window.

At the first start, the main menu explains ReDefinition and offers *Let ReDefinition
take control* or *Later*. Taking control chooses the High profile and bundles the mods,
switches *Replace original settings* on, sets KSP's UI scale for your screen where it is
still KSP's 100 %, switches on KSP's *Advanced tweakables*, *Extended burn indicator* and
*Always show closest approach*, sets *Conic patch limit* to 6, and loads the main menu
again, whose *Settings* then opens this window. *Later* leaves every mod as it is until
you choose a profile. A graphics mod installed later follows that choice, with a short
note when it is bundled.

## What the installed mods require

Some mods need another setting set a certain way and show an error or a warning
otherwise. ReDefinition keeps such a setting as the mod needs it, whatever a profile,
the reset or a row says. The row is locked or offers only the allowed values, and its
tooltip says why.

A value set elsewhere, in KSP's own settings screen for example, is corrected at the
next scene load or when KSP's settings are applied, with one message per run. The list,
with the source of each:
[reference/requirements.md](../reference/requirements.md).

## KSP's own graphics settings

The rows are: render quality, texture quality, V-Sync, frame limit, shadow cascades,
terrain detail, and aerodynamic FX while Firefly is not installed. The profiles also
set KSP's antialiasing off, and its pixel light count, terrain shader quality and
reflections. *Reset* sets those and its terrain scatter, planet shadows,
scatter density and surface FX.

Some of them another mod holds for itself:

| Setting | Held by |
|---|---|
| aerodynamic FX | Firefly, at its lowest, since it replaces KSP's effects |
| reflection refresh | Deferred, at Low, while its own cap for it is on |
| reflection resolution | Deferred, at 256 at most, while its own cap for it is on |
| terrain shader quality | Kopernicus, where its config enforces a level or warns about one |

## The section in KSP's settings dialog

It holds Upscaler, Technique, Mode with all six modes and their scale factors, DLSS
model, Sharpness and Frame generation. What the dialog shows is a copy, set on *Apply*
or *Accept*, as KSP's own graphics settings are. The dialog and the settings window use
the same settings file. The main menu's settings screen has no ReDefinition section.

## The diagnostics window

Open it with *Diagnostics* in the settings window, or with the hotkey you set for it. The
button opens it on *Upscaler inputs*. The hotkey opens it on the page it was left on, and
a new start of KSP opens it on *Overview* if that was *Previews*, which costs frame rate
while it is shown. The window is for finding out what ReDefinition does, and for bug
reports.

At the top, a strip shows what runs, for example *FSR 3 + XeSS-FG 2x + XeLL*: the
upscaler, the frame generation and what lowers the latency. Beside it are the rendered
frame rate, with the shown rate after an arrow while frame generation generates, and a
graph of the last 240 frames. Frames well over the usual time are drawn in the warning
colour. Under the strip are the median frame time with its 95th percentile and the worst
frame, the latency, and the load of the main thread, the render thread and the GPU. The
load is measured only while the window is open.

The window's pages are in a list on the left:

| Page | Holds |
|---|---|
| *Overview* | the status line; *Upscaler* and *Frame generation*; *Technique*, *Mode* and *Sharpness*, with the reason and *Try ... again* when the chosen technique cannot run; whether other visual mods run anything that conflicts with the upscaler; the frame rate with the upscaler on against off |
| *Upscaler inputs* | switches for a bug report: *Jitter*, *Vessel depth and motion vectors*, *Vessel shadow in frame generation*, *Skinned motion vectors*, *Motion vector repair*, *Render interpolation*, *EVE clouds: jitter and motion vectors*, *Mipmap bias*, *TUFX after upscaling*, *Auto exposure*, *Transparency mask* and *Reactive mask* |
| *Frame generation & latency* | *Frame generation* and *Multiplier*, *Motion vector check on fast turns*, and under *Running now* the frame generation that runs, what lowers the latency, and the presented frames per rendered frame |
| *Game settings* | *LOD bias compensation*, *MSAA forced off*, *Anisotropic filtering forced* |
| *Capture & log* | *Measurements into the log*, *Write diagnostics to log*, *Hook probe, into the log*, *Surface shine overlay*, *Without upscaler (bypass)*, *FSR's debug view* in development builds only, and *Record the screen in 5 s*, 120 frames into `ReDefinitionCaptures` beside `KSP_x64.exe`, with the dumps for analysis or without them |
| *Previews* | what the upscaler receives and returns: motion vectors, depth, the image at render resolution, the transparency and reactive masks, and the result |
| *Other mods* | three lists of lines: *Their state*, what ReDefinition sees of the other mods; *Registrations*, the bundled mods and the problems of their registrations; *Their code*, the places in other mods' code that ReDefinition relies on, with those that changed in the installed version first and coloured |

The motion vector, depth and mask previews read the image back from the GPU and cost
frame rate, so they are read only while *Previews* is open. *Hook probe, into the log*
registers a handler on each place a mod can add to the frame and writes one line per
scene into `KSP.log` for each. It draws nothing.

Each option ends in a marker. *saved* means it is kept for the next start, and *session*
that it goes back when KSP ends. A row that does not apply here is greyed out and says
why. A row away from its default stays usable, so that you can put it back.

The search field in the strip finds options on every page by their name, group,
tooltip, page title and choices. While it holds text, the window lists the matching
options from all pages under their page names, and each page in the list shows how many
match. On *Other mods* it filters the lines. *Changed* limits the options to those away
from their default, and *Reset debug switches* puts them back. *Upscaler*, *Frame
generation* and *Multiplier* are your settings, not debug switches, and stay as they are.

A star at the end of a row pins the option to the foot of the window, where it stays one
click away on every page. The pins are saved.

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

Each of them is still in that mod's own window. If ReDefinition can open that window,
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
