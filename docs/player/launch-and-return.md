# Launch from the editor, and back

**For:** players who go between the VAB or the SPH and the launch pad or the runway.
**You need:** ReDefinition installed, and the space centre shown once in this game.
**You get:** buttons in the world that launch a vessel and take it back to its building.

The buttons look like the space centre's own buttons for the building or the launch site.
ReDefinition takes their pictures from the space centre as you see it there, a UI theme's
too, and keeps them. Until the space centre has been shown once, the buttons are round
plates.

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
