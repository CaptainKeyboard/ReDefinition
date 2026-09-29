# The engineer window in the VAB and the SPH

**For:** players building a vessel in the VAB or the SPH.
**You need:** ReDefinition installed. No graphics profile is needed.
**You get:** what the engineer window shows while you build, how to move and fold it,
and its settings.

The engineer window shows the numbers of every stage while you build: delta-v, thrust,
TWR, burn time, mass and cost. It stands at the bottom of the screen while the parts
list is shown, and follows every part you add. The numbers come from KSP's own delta-v
simulation, so they match the delta-v in KSP's staging stack.

To plan a mission and its fuel, use the [planner](planner.md).

## Move, fold and resize the window

| Do | Result |
|---|---|
| drag the window by its background | moves it; it stays there the next time |
| *-* in its top right corner | folds it to one line with the delta-v and the TWR at launch |
| *+* on the folded line | opens it again |
| *Compact* | shows four columns: stage, delta-v, TWR and burn time |
| *Full* | shows all columns again |

If you set a key for *Engineer window (VAB / SPH)* in the *Controls* tab, that key folds
and opens the window.

## Choose the situation

The top row sets where the numbers are for:

| Control | Does |
|---|---|
| the body's name | opens the list of bodies to choose from |
| *Sea level* | the engines at the body's sea level, where the air pressure lowers their thrust and impulse |
| *Altitude* | the engines at the height the slider below sets, inside the body's atmosphere |
| *Vacuum* | the engines above the atmosphere |

The chosen one is lit green. A body without an atmosphere has only *Vacuum*. The
situation is the one KSP's own delta-v app uses, and KSP's staging stack follows it.
While KSP's delta-v app is open, it keeps showing its own situation. A change you make
in the app sets that one for both.

## Read the table

Under the top row, one line sums up the vessel: its delta-v, its TWR at launch, its mass
with and without propellant, its cost and its parts. The table below has one row per
stage:

| Column | Shows |
|---|---|
| *Stage* | the stage's number, as KSP's staging stack numbers it |
| *Parts* | the parts attached while the stage burns, and in brackets the parts it drops |
| *Cost* | the cost of what the stage drops |
| *Mass* | the vessel's mass when the stage fires |
| *Isp* | the engines' specific impulse |
| *Thrust* | the engines' thrust |
| *Torque* | how much the engines' thrust turns the vessel. Near 0 means the thrust runs through the centre of mass |
| *TWR* | the thrust-to-weight ratio when the stage fires, and in brackets when its propellant is burnt |
| *Δv* | the stage's delta-v |
| *Burn* | how long the stage burns |

A TWR at launch below 1 is orange: the vessel will not lift off in that situation. Click
a row, and the stage's engines and the tanks it burns light up green on the vessel.
Click it again to clear the mark. While the planner is open, the planner colours the
parts instead.

*RCS* adds a line with the RCS thrusters of the last stage. *Resources* adds every
resource on board, filled and in total, with its mass.

## See a part's details

Rest the pointer on a part of the vessel, and a card beside the pointer shows its mass,
cost, engine, reaction wheel, decoupler, parachute, antenna and resources. The card
does not show over a window or while a part hangs on the mouse.

## Settings

The window's settings are in the settings window, in the *Mods / Toolbar* tab under
*Engineer (VAB / SPH)*:

| Setting | Does |
|---|---|
| Engineer window | shows or hides the window |
| Stages without delta-v | also lists stages that burn nothing, such as a decoupler or parachute stage |
| Thrust along the vessel | shows each stage's thrust and TWR along the vessel's axis. Engines mounted at an angle lose part of their thrust. KSP's own delta-v app shows the full thrust |
| Part under the mouse | shows or hides the card with the part's details |

## If Kerbal Engineer Redux is installed

Kerbal Engineer Redux is no longer needed: the engineer window and the planner show the
same information in the VAB and the SPH. Where it is installed, ReDefinition says so once
per game start.
