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

In the R&D building, the tech tree zooms in to twice its size, not only to its own.

With a UI scale above 1, KSP counts room for more crew portraits along the bottom of the
screen than fit there. ReDefinition counts them at the size they are drawn.

## Set it

Open the settings window and its *Interface* tab. Under *Scaling*:

| Row | Does |
|---|---|
| *Map, labels and gizmos* | how large the map's icons, the labels, the lines and the gizmos are, as a share of KSP's UI scale. At 100 % they have the UI scale; lower it if they feel too large on a screen you sit close to |
| *Other mods' windows* | draws other mods' windows at KSP's UI scale. Off, they keep their own size |

A change to *Map, labels and gizmos* shows at once on the map and the labels. The orbit
lines take it after the next scene change, the editor's gizmos the next time they appear.

## Mods that do this already

Where one of these is installed, ReDefinition leaves that part to it:

| Mod | Takes over |
|---|---|
| 4kSP or 4kSP Expanded | the map's icons and labels |
| 4kSP Expanded's R&D part | the tech tree's zoom |
| 4kSP Expanded's window part | other mods' windows |
| Maneuver Node Evolved | the maneuver gizmo |

Mods that size their own windows keep their size and their own setting for it: MechJeb,
Kerbal Alarm Clock, RCSBuildAid, KSP Craft Organizer, Kopernicus and others.
