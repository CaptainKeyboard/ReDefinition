# Move the navball

**For:** players who want the navball somewhere other than the bottom centre.
**You need:** ReDefinition installed. No graphics profile is needed.
**You get:** how to move the navball sideways, and how to put it back.

The navball can stand anywhere along the bottom of the screen. Everything that belongs
to it moves with it: the SAS and RCS switches, the SAS modes, the throttle and G gauges,
the speed and heading, and the delta-v gauge.

## Move it

1. In flight, point at the small button above the navball that shows and hides it.
2. Hold the left mouse button and drag left or right.
3. Let go where you want the navball.

A click without dragging still shows and hides the navball. The navball stays between
the flight mode buttons on the left and the crew portraits on the right. Where either
grows, because you choose another mode or add a portrait slot, the navball moves aside,
and back to where you put it once there is room. The same holds while the burn gauge of a
maneuver shows beside the navball. The stage list does not push it aside: the navball is
drawn over it. It keeps its place when you hide and show
it, in the map view and in every other flight mode. The SAS modes stand on its left, as in
KSP. On its right, *V* and *H* give the vessel's vertical and horizontal speed over the
surface, in fields like the heading's.

ReDefinition keeps the place for the next flight and the next game start.

## The round gauge

The round gauge beside the altimeter, KSP's vertical speed dial, shows one figure in
digits, and the same figure as a lit arc on a scale that stays fixed for it. Amber and red
bands on the scale mark where it gets close to a limit and where it is past it; a limit
that moves with the flight is a red tick. The arc is white while the figure is fine, and
takes the band's colour inside one. Where a figure spans seconds to minutes or tenths to
hundreds, the scale is logarithmic: fine near zero, compressed higher up.

Click the gauge for the next figure, right-click for the one before; the face lights up
under the mouse. On *AUTO* it picks the figure for what the vessel is doing, as the flight
panel picks its profile. The gauge keeps your choice.

| Figure | Digits and arc | Scale | Bands | Picked on *AUTO* |
|---|---|---|---|---|
| *STAGE* | burn time left in the stage at full thrust; the arc its tanks | empty to full as built | amber below a quarter, red below a tenth | on the way up; in orbit without a maneuver |
| *BURN* | time until the maneuver's burn starts | 0 to 10 min, logarithmic | amber in the last ten seconds | in orbit or on the way to a target, with a maneuver |
| *BURN*, burning | delta-v left | 0 to the maneuver's delta-v as the burn started | amber in the last tenth | |
| *IMPACT* | time until the vessel's path meets the ground | 0 to 10 min, logarithmic | the tick: the time left at the last moment to brake; red below it, amber a third above it | coming down |
| *CLOSING* | how fast the vessel closes on its target, or on the target's port | 0 to 100 m/s, logarithmic | the tick: a safe speed for the distance, a hundredth of it per second, at least 0.2 m/s; amber to one and a half times it, red beyond | docking; on the way to a target without a maneuver |
| *AOA* | angle of attack, either way | 0 to 40° | red past the angle where wings lift the most (30°), amber from four fifths of it | flying with wings |

The burn starts half its length before the maneuver, as KSP counts it. The time to impact
leaves drag out, so in air the vessel falls later than the gauge says.

## Where it starts

The navball starts in the centre. Where you drag it, it stays, in every flight and after
the next game start. With another screen resolution, it goes back to the centre.
