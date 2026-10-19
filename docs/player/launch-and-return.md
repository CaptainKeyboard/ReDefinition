# Launch from the editor, and back

**For:** players who go between the VAB or the SPH and the launch pad or the runway.
**You need:** ReDefinition installed, and the space centre shown once in this game.
**You get:** buttons in the world that launch a vessel, take it back to its building, and
leave it for another building before the launch.

The buttons look like the space centre's own buttons for the building or the launch site.
ReDefinition takes their pictures from the space centre as you see it there, a UI theme's
too, and keeps them. Until the space centre has been shown once, the buttons are round
plates.

The buttons are part of the world: a vessel, a building or the ground before them hides
them, and a click there goes to what stands before them. They grow smaller as the camera
draws away, and go once they would be too small to see.

## Launch from the VAB or the SPH

In the VAB, a button stands over the launch pad you see through the open door. In the SPH,
it stands over the runway. A click launches the vessel there, as KSP's launch button does.
It shows while KSP's launch button can be pressed.

## Back to the building

While the vessel stands on the ground within 3 km of the space centre's VAB or SPH, a
button stands on the middle of that building. Its tooltip says what a click does, and a
click asks first:

| Tooltip | When | A click |
|---|---|---|
| *Revert to Vehicle Assembly*, *Revert to Spaceplane Hangar* | KSP's pause menu would offer the revert; the button is on the building the vessel was built in | reverts to that editor, as the pause menu does |
| *Recover vessel and go to Vehicle Assembly*, *... Spaceplane Hangar* | no revert, but the vessel can be recovered; the button is on the hangar where the vessel stands on the runway, else on the VAB | recovers the vessel as KSP's recover button does; once you close KSP's report in the space centre, that editor opens with the vessel |

The editor opens with the vessel as it was launched where that is the same vessel, else
with its craft file of the same name. Where there is neither, it opens empty. The button
does not show in the map view, nor where neither a revert nor a recovery is possible.

## Go to another building before the launch

While the vessel stands on the launch pad or the runway and has not moved yet, a button
stands on each of the space centre's other buildings within 3 km: the Tracking Station,
Mission Control, the Astronaut Complex, Research and Development and the
Administration. Its tooltip names the building. A click asks first, then saves the game
and leaves the flight as KSP's pause menu does, with the vessel left where it stands.
The Tracking Station opens as itself; for the others, the space centre opens with that
building's window, as a click on the building there opens it.

A button shows only where the game allows leaving the flight for that place. Where the
game cannot be saved at that moment, the click shows KSP's reason at the top of the
screen and leaves the flight as it is.

## Switch the buttons off

*Buttons over the buildings*, under *Gameplay*, *Flight* in the settings window, switches
every one of these buttons off and on again.
