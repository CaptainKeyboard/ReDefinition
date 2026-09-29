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
digits. A lit arc shows how far along that figure is. The arc is white while all is well,
amber when it gets close and red when it is past the limit. Click the gauge for the next
figure, right-click for the one before. On *AUTO* it picks the figure for what the vessel
is doing, as the flight panel picks its profile. The gauge keeps your choice.

| Figure | Digits | Arc | Picked on *AUTO* |
|---|---|---|---|
| *STAGE* | burn time left in the stage at full thrust | that time against the stage's full burn time, so it runs out with the digits; amber below a quarter, red below a tenth | on the way up; in orbit without a maneuver |
| *BURN* | time until the maneuver's burn starts, then the delta-v left | fills over the last minute before the burn, amber in the last ten seconds; during the burn how much is done, amber in the last tenth | in orbit or on the way to a target, with a maneuver |
| *IMPACT* | time until the vessel's path meets the ground | how close it is to the last moment to brake; red when it is time to fire | coming down |
| *CLOSING* | how fast the vessel closes on its target, or on the target's port | half full at a safe speed for the distance: a hundredth of it per second, at least 0.2 m/s; amber above, red at one and a half times | docking; on the way to a target without a maneuver |
| *AOA* | angle of attack | against the angle where wings lift the most; amber past 60 %, red past 85 % | flying with wings |

The burn starts half its length before the maneuver, as KSP counts it. The time to impact
leaves drag out, so in air the vessel falls later than the gauge says.

## Where it starts

The navball starts in the centre. Where you drag it, it stays, in every flight and after
the next game start. With another screen resolution, it goes back to the centre.
