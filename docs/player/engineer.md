# The engineer view in the VAB and the SPH

**For:** players building a vessel in the VAB or the SPH.
**You need:** ReDefinition installed. No graphics profile is needed.
**You get:** how to open the engineer view, what each part of it shows, and its settings.

The engineer view shows the numbers you need while you build: delta-v, thrust-to-weight
ratio and burn time for every stage, RCS, the resources on board, and the part under the
mouse. It is a mode of the editor, next to KSP's own parts, action groups, crew and cargo
modes. The numbers come from KSP's own delta-v simulation, so they always match the
delta-v KSP shows in its staging stack.

The view has two tabs. *Engineer* is described on this page. *Planner* plans a route and
checks the fuel for it: see [planner.md](planner.md).

## Open the view

1. In the VAB or the SPH, look at the mode buttons above the parts list.
2. Click the button with *Δv* on it, to the right of the cargo button.

The view takes the place of the parts list. To go back, click KSP's parts button, or any
other mode button. If you set a key for *Engineer view (VAB / SPH)* in the *Controls*
tab, that key opens and closes the view as well.

While a part hangs on the mouse, the mode buttons are locked, as KSP's own are.

## Choose the situation

The top of the view says where the numbers are for:

| Control | Does |
|---|---|
| *<* and *>* | choose the body: Kerbin, the Mun, Duna and every other body |
| *Sea level* | the engines at the body's sea level, where the air pressure lowers their thrust and impulse |
| *Altitude* | the engines at the height the slider below sets, inside the body's atmosphere |
| *Vacuum* | the engines above the atmosphere |

A body without an atmosphere has only *Vacuum*. The situation is the same one KSP's own
delta-v app uses, and the delta-v in KSP's staging stack follows it. KSP's delta-v app
reads it when you open the app. While the app is open, it keeps showing its own
situation, and a change you make in the app sets that one for both.

## What the view shows

| Section | Shows |
|---|---|
| *Vessel* | total delta-v, the TWR at launch, the mass with and without propellant, the cost, the number of parts and stages. Mass and cost include the crew, as KSP's cost display and mass limit count them |
| *Stages* | one row per stage, stage 0 at the top: its delta-v, the delta-v left from that stage on, its TWR and its burn time |
| *RCS, last stage* | the delta-v, impulse, thrust, TWR and burn time of the RCS thrusters on what is left at the end |
| *Resources* | every resource on board, filled and in total, with its mass. Click the heading to open it |
| *Part under the mouse* | the part you point at on the vessel: mass, cost, engine, reaction wheel, decoupler, parachute, antenna and its resources as filled |

The *+* before a stage opens its details:

| Detail | Means |
|---|---|
| Isp | the engines' specific impulse in the chosen situation |
| Thrust | the engines' thrust in the chosen situation |
| TWR at start / end | the thrust-to-weight ratio when the stage fires, and when its propellant is burnt |
| Thrust torque | how much the engines' thrust turns the vessel. Near 0 means the thrust runs through the centre of mass |
| Mass at start / end | the vessel's mass when the stage fires, and when it ends |
| Parts dropped / attached | the parts the stage drops, and all parts attached while it burns |
| Cost dropped / attached | the same, as cost |

A TWR at launch below 1 is shown in orange: the vessel will not lift off in that
situation.

## The summary line

While the parts list is open, a line right of it, under the mode buttons, shows the
vessel's delta-v, its TWR at launch, its mass and its parts. It follows the situation
chosen in the view. Click it to open the view.

## Settings

The view's settings are in the settings window, in the *Mods / Toolbar* tab under
*Engineer (VAB / SPH)*:

| Setting | Does |
|---|---|
| Summary line | shows or hides the line beside the parts list |
| Stages without delta-v | also lists stages that burn nothing, such as a decoupler or parachute stage |
| Thrust along the vessel | shows each stage's thrust and TWR along the vessel's axis. Engines mounted at an angle lose part of their thrust. KSP's own delta-v app shows the full thrust |
| Part under the mouse | shows or hides the part details at the bottom of the view |

The key that opens the view is in the *Controls* tab, as *Engineer view (VAB / SPH)*. It
has no key until you set one.

## If Kerbal Engineer Redux is installed

The engineer view shows what Kerbal Engineer Redux shows in the editor, inside KSP's
own editor. Kerbal Engineer is no longer developed, and with both installed you see the
same numbers twice. When ReDefinition finds Kerbal Engineer, it recommends uninstalling
it: once per game start, and in a line at the bottom of the view. Uninstall it with CKAN,
or delete `GameData/KerbalEngineer`.
