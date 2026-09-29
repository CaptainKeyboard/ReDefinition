# Readable on large screens

**For:** players on a 1440p or 4K screen, or a TV, who find parts of KSP's interface too
small.
**You need:** ReDefinition installed, and the UI scale in KSP's settings set to what you
want for the rest of the interface.
**You get:** what ReDefinition sizes that KSP leaves small, and how to set it.

KSP's UI scale (*Settings*, *General*, *UI Scale*) sizes its windows, buttons and panels.
Some things it leaves at a fixed number of pixels, so that on a 4K screen they are half
the size they have on 1080p. ReDefinition gives them the UI scale too:

| What | Where |
|---|---|
| the icons on the map, such as Ap, Pe, nodes, vessels and bodies, with their labels | map view, tracking station |
| the orbit lines, the target's orbit and CommNet's lines | map view, tracking station |
| the labels over vessels, with their name and distance | flight |
| the maneuver node's gizmo, its handles and its centre ring | flight, map view |
| the gizmos that move and rotate parts | VAB, SPH, EVA construction |
| other mods' windows | everywhere |

In the R&D building, the tech tree opens at one and a half times its size and zooms in
to two and a half. Its nodes grow with the UI scale and the zoom, to twice their size at
150 %, and the tiles under *Available Parts* are 1.2 times their size.

With a UI scale above 1, KSP counts room for more crew portraits along the bottom of the
screen than fit there. ReDefinition counts them at the size they are drawn.

## Set it

The UI scale itself is under *Display*, *Screen*: from 80 to 400 %, in steps of 10 %.
KSP's own settings screen offers up to 200 %; a larger value stays until its slider is
moved there.

The map's icons, the labels, the lines and the gizmos follow it. A new UI scale shows at
once on the map and the labels; the orbit lines take it after the next scene change, the
editor's gizmos the next time they appear.

Open the settings window and its *Mods / Toolbar* tab. Under *Scaling*, *Other mods'
windows* draws other mods' windows at KSP's UI scale. Off, they keep their own size.

## Mods that do this already

Where one of these is installed, ReDefinition turns off its scaling of the same thing, so
that nothing grows twice. The rest of the mod works as before:

| Mod | Its scaling turned off |
|---|---|
| 4kSP or 4kSP Expanded | the map's icons and labels, and its toolbar button for them |
| 4kSP Expanded's R&D part | the tech tree, its larger parts list and its toolbar window |
| 4kSP Expanded's window part | other mods' windows |
| Maneuver Node Evolved | the maneuver gizmo's size and its own size settings; *Force Maneuver Node Open*, snapping and input stay |

Mods that size their own windows keep their size and their own setting for it: MechJeb,
Kerbal Alarm Clock, RCSBuildAid, KSP Craft Organizer, Kopernicus and others.
