# Read your flight at a glance

**For:** players in flight who want the numbers for what they are doing in front of them.
**You need:** ReDefinition installed, and a vessel in flight.
**You get:** where each figure stands, what the flight panel's profiles show, and how
the panel picks one.

## The flight panel

KSP's maneuver mode shows a panel at the bottom left. ReDefinition puts its profiles
there, in place of KSP's three orbit tabs: one tab per profile, in the same row, with
the same look. A flight starts in the maneuver mode (*Start flight in maneuver mode*
under *Mods / Toolbar*). The tabs for planning a maneuver node stay as KSP has them.

Each value has a field like KSP's own: its title above, the value below, and a
second value under it where there are two, such as a height and the time until it.
The fields fill columns of the panel's height, and the panel grows to the right when a
profile holds more than two columns. The mode buttons beside it move along.

| Profile | Shows |
|---|---|
| Ascent | apoapsis with the time to it, periapsis with the time to it, TWR now and at full throttle, the stage's delta-v, dynamic pressure, Mach |
| Orbit | apoapsis and periapsis with their times, period, inclination, eccentricity, orbital speed |
| Rendezvous | closest approach with its distance and time, the transfer angle with the time to the window, the distance and relative speed to the target, the phase angle, the relative inclination, the time to the relative nodes |
| Landing | the suicide burn's height and the time to it, height over the ground, vertical and horizontal speed, time to impact, the suicide burn's delta-v, the slope under the vessel, the biome |
| Docking | the distance to the target port, the speed closing in and sideways, the angle between the ports, the relative speed |
| Air | height over the sea, vertical speed, Mach, dynamic pressure, angle of attack, sideslip |

A value that needs a target, or a docking port as the target, shows only while there
is one.

## Change a profile

The pencil after the tabs opens the profile of the tab shown:

1. *Next* steps to the next profile; its name can be typed over.
2. Under *Shown, in this order*, *Up* and *Down* move a value, *Remove* takes it out.
3. Under *Add*, grouped as Orbit, Surface, Vessel, Air, Rendezvous and Docking, a click
   adds a value at the end.
4. *New profile* adds an empty one, *Delete profile* removes the one shown, *Defaults*
   brings back the six profiles as ReDefinition ships them.

A change shows in the panel at once and is kept for every save. A new or deleted
profile gets or loses its tab with the next flight scene. A profile of your own shows
only when you pick it. While the window is open, the keyboard does not steer the vessel.

## The profile follows the situation

With *Flight panel follows the situation* on, under *Mods / Toolbar*, the panel switches
once a situation has held for a second:

| The vessel | Profile |
|---|---|
| has a docking port as its target, within 2 km | Docking |
| has another target and is not on the ground | Rendezvous |
| comes down on engines, below 20 km over the ground or on a path into it | Landing |
| flies on wings | Air |
| stands on the ground or climbs | Ascent |
| is anywhere else | Orbit |

A profile you pick by its tab stays until the situation changes.

## The rest of the HUD

| Where | Shows |
|---|---|
| the altimeter, top centre | the height above the ground below 5 km over it and on it, above sea level from 6 km; its switch still changes it by hand (*Altimeter switches to the ground*) |
| the vertical speed gauge beside it | its needle as KSP has it; while the vessel comes down on a path into the ground, *IMPACT* and the time left |
| the navball, right | *V* and *H*: the vertical and horizontal speed over the surface |
| the navball's ball | with a docking port as the target, an orange ring with a cross where the nose has to point for the two ports to face each other |

## How the values are worked out

* The time to impact follows the orbit against the ground's height under each point as
  the body turns. Drag is not counted, so in air the vessel falls later than it says.
* The suicide burn is the last moment to light the engines at full thrust and stop
  over the ground, at the speed over the surface now.
* The transfer angle is the phase a target must lead by for a transfer from a circular
  orbit; the window is when the phase angle reaches it. Where the target goes round
  another body than the vessel, such as a planet from Kerbin's orbit, both are taken
  round the Sun.
* The closest approach looks one turn of the orbit ahead, ten days at most.
