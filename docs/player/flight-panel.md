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

| Profile | Shows | Because |
|---|---|---|
| Ascent | apoapsis with the time to it, periapsis with the time to it, TWR now and at full throttle, the stage's delta-v, dynamic pressure | the gravity turn is flown by the time to apoapsis, about 40 s, pitching over when it grows beyond a minute; the periapsis says when the orbit closes; TWR and the stage's delta-v say when to throttle and stage; dynamic pressure warns in the thick air |
| Orbit | apoapsis and periapsis with their times, period, inclination, eccentricity, the vessel's delta-v | the orbit's shape and when to burn, and what is left for the next manoeuvres |
| Rendezvous | closest approach with its distance and time, the relative speed there, the relative speed now, the distance to the target, the transfer angle with the time to the window, the phase angle, the relative inclination, the time to the relative nodes | the approach is made by killing the relative speed at the closest approach; before it, the window and the planes are matched |
| Landing | the suicide burn's height and the time to it, height over the ground, vertical and horizontal speed, the stage's delta-v, the suicide burn's delta-v, time to impact, the slope under the vessel, the biome | first the horizontal speed goes, then the vertical below about 10 m/s near the ground; the two delta-v figures say whether the fuel lasts; slope and biome say whether the spot will do |
| Docking | the distance to the target port, the speed closing in and sideways, the angle between the ports, the RCS's delta-v | the last metres are flown below about 0.5 m/s, sideways drift killed and the ports lined up, on RCS propellant that often runs short |
| Air | height over the sea, vertical speed, glide slope, angle of attack, Mach, dynamic pressure | a landing comes in at no more than about 15 degrees with the nose at most about 10 up; Mach and dynamic pressure tell speed and load |

A value that needs a target, or a docking port as the target, shows only while there
is one.

## Change a profile

The pencil after the tabs opens the profile of the tab shown:

1. *Next* steps to the next profile; its name can be typed over.
2. Under *Shown, in this order*, *Up* and *Down* move a value, *Remove* takes it out.
3. Under *Add*, grouped as Orbit, Surface, Vessel, Air, Rendezvous and Docking, a click
   adds a value at the end.
4. *New profile* adds an empty one of your own, *Delete profile* removes one of your own,
   *Defaults* puts the six situations' profiles back as ReDefinition ships them and
   leaves your own. The six profiles for the situations cannot be deleted: the panel
   always has one to switch to.

A change shows in the panel at once and is kept for every save; a new profile opens in
its own tab. A profile of your own shows only when you pick it. While the window is open,
the keyboard does not steer the vessel.

## The profile follows the situation

With *Flight panel follows the situation* on, under *Mods / Toolbar*, the panel switches
once a situation has held for a second:

| The vessel | Profile |
|---|---|
| has a docking port as its target, within 2 km | Docking |
| comes down without wings, below 20 km over the ground or on a path into it | Landing |
| has another target and is not on the ground | Rendezvous |
| flies on wings: wings that lift at least as much as two wing panels, and 0.1 of one per tonne, not counting fins and control surfaces | Air |
| stands on the ground or climbs | Ascent |
| is anywhere else | Orbit |

A profile you pick by its tab stays until the situation changes.

## The rest of the HUD

| Where | Shows |
|---|---|
| the altimeter, top centre | the height above the ground below 5 km over it and on it, above sea level from 6 km; its switch still changes it by hand (*Altimeter switches to the ground*) |
| the round gauge beside it | a digital gauge in place of KSP's vertical speed dial, with a lit arc: the stage's burn time, the next burn, the time to impact, the closing speed or the angle of attack, for the situation or as you click ([the navball page](navball.md#the-round-gauge)); the vertical speed stands at the navball |
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
