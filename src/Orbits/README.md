# Orbits

The orbit maths the planner's nodes rest on: free of Unity and KSP, so that it is tested outside the game.

## Start here

| File | Read it for |
|---|---|
| `src/Orbits/Kepler.cs` | Moving a state along its conic by universal variables, for any time and any orbit. |
| `src/Orbits/Conic.cs` | An orbit read off a state: its shape and plane, and the times to its apsides and to a true anomaly. |
| `src/Orbits/Lambert.cs` | The orbit that joins two positions in a given time (Izzo's formulation), half a turn included. |
| `src/Orbits/Maneuvers.cs` | Burns within one orbit: circularise, change the other apsis, match a plane; and the node frame's conversion. |
| `src/Orbits/Transfers.cs` | The search over departure time and time of flight for the cheapest transfer between two orbits. |

## What it does

- A `State` is a position and a velocity in a right-handed frame, a `Burn` a delay and a change of velocity in it. KSP's own frame is left-handed
  and swapped; the conversion belongs to the code that reads KSP's orbits, not here.
- The tests (`tests/ReDefinition.Tests/OrbitsTests.cs`) check the propagator against the universal Kepler equation, Lambert against Curtis's
  worked example and by flying its answers, and the transfer search against the Hohmann transfer.

## Rules of the area

- Nothing here may use Unity, KSP or another area (`ArchitectureTests`).
