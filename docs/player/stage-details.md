# The delta-v tools in the staging stack

**For:** players who look at the stages' details in KSP's staging stack.
**You need:** ReDefinition installed.
**You get:** how the delta-v tools and the stage details work with ReDefinition.

## Open the tools

Press KSP's *Δv* button at the bottom of the screen. Every stage opens its details, and
in the VAB and the SPH the *Δv Tools* window slides in with them, from the side and from
below, under the lowest stage and above the vessel's total delta-v; the stages above move
up with it. Press the button again, and the details and the window slide out.

The window has what the numbers are worked out for, in three rows and a slider:

| Row | Sets |
|---|---|
| the body's list | the body the vessel starts from |
| *Sea Level*, *Altitude*, *Vacuum* | where the engines burn |
| the altitude, and the air pressure there | the height for *Altitude*, typed or on the slider below |

The window stays at the bottom when the stages scroll, and is as wide as the stages with
their details. In flight KSP works the stages out for where the vessel is, so the button
opens and closes the details only.

A click on a stage's number still opens or closes that stage's details alone. A stage
added while the tools are open opens with the others, and so do the stages of a craft
you load. A click on the vessel's total delta-v at the bottom does the same as the *Δv*
button.

## The details

Each stage shows its Isp, thrust, TWR, start and end mass, and burn time, always all of
them, in two columns side by side. The TWR gives two values, such as *1.45-2.80*: as the
stage lights, full, and as it burns out, at its lightest. Its delta-v stands under the
stage's number, as in KSP. While the details are out, the stage's part icons are in
two columns as well, so that a stage with many parts stays short. They move into their
places as the details slide in, and back as they slide out. They stay in two columns
while you drag a part or a stage; a dragged part drops into the row you let go on.

## Switch them off

Each part has its own switch under *Gameplay*, *Flight* in the settings window
([settings-window.md](settings-window.md#switch-redefinitions-flight-features)):

| Switch | Off |
|---|---|
| *Delta-v tools in the staging stack* | KSP's *Δv* button and *Δv Tools* window as KSP has them, from the next scene |
| *Stage details in two columns* | the details in one column and the part icons as KSP lays them out, from the next scene |
| *TWR at the stage's start and end* | the TWR as the stage lights only, at once |

*Ask before resetting the staging*, beside them, makes the editors' button that resets
the staging ask first.
