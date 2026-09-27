# Plan a mission in the VAB and the SPH

**For:** players building a vessel for a mission to one or more destinations.
**You need:** ReDefinition installed, and a vessel in the VAB or the SPH.
**You get:** how to lay out a route, see what each leg costs, and check that the
vessel's stages carry enough fuel for all of it.

The planner is the second tab of the [engineer view](engineer.md). You lay out a route
the way a map app lays out a trip: A → B → C → D. The planner works out the delta-v of
every leg. It then shows which of the vessel's stages pays for it, and whether the fuel
is enough. It works with the bodies the game has loaded, so it also works with planet
packs.

## Open the planner

1. Open the engineer view with the *Δv* button above the parts list.
2. Click *Planner* at the top of the view.

The plan belongs to the vessel. It is saved under the vessel's name and comes back
when you load the vessel again.

## Lay out the route

A new vessel starts with two stops: *A* on the surface of Kerbin, *B* in a low orbit
around it. The route reads from top to bottom, one dot per stop.

| Control | Does |
|---|---|
| the body's name | opens the list of bodies to choose from, each planet with its moons under it |
| flag | land on the body |
| ring | stay in orbit around it. Below the stop, *-* and *+* lower or raise the orbit by 10 km, down to the lowest safe one |
| parachute | on a surface with air: land with a heat shield and parachutes. On thin air, such as Duna's, the engines still slow the last part |
| dipping path | on an orbit around a body with air: slow down in the atmosphere instead of with the engines |
| cross | remove the stop |
| *+ Add destination* | add a stop at the end of the route |

A mission to the Mun and back looks like this:

1. *A*: Kerbin, flag
2. *B*: Kerbin, ring
3. *C*: Mun, ring
4. *D*: Mun, flag
5. *E*: Mun, ring
6. *F*: Kerbin, flag, with the parachute

## Read the legs

Between two stops, a bar shows the leg. Its length is the leg's delta-v, on the same
scale for every leg. Its colours are the stages that pay for it, in the colours of the
*Stages* list. Red is what no stage covers. The figure beside the bar is the leg's
delta-v with the margin. It turns orange if the leg runs short or starts in the wrong
place.

Click the bar or the figure to open the leg. It shows the burns the leg is made of,
what each stage gives to it, and a warning where a stage is too weak to launch or land:

| Burn | Is |
|---|---|
| *Launch to … orbit* | from the surface into the low orbit, with the losses to gravity and air |
| *Transfer burn* | leaving the orbit toward the next body |
| *Capture into orbit* | slowing down at the next body into its orbit |
| *Aerobraking, then circularise* | the small burn left after the atmosphere has slowed the vessel |
| *Raise or lower the orbit* | from one orbit to another around the same body |
| *Landing* | from the low orbit down to the surface |
| *Plane change* | turning the orbit to the tilt of the target's orbit |

## Check the fuel

Under the route, one bar sums up the whole mission: the stages' colours for what they
burn, grey for what is left over, red for what is missing. Under it, green with a *+*
is what is left over at the end, orange with a *-* is what is missing. Then come what
the route needs and what the vessel has.

| Setting | Does |
|---|---|
| *Margin* | adds a share to every leg, for steering, corrections and a late burn. 10 % is a common choice |
| *Plane changes* | adds the burn that matches the tilt of the target's orbit. Leave it off if you launch into the right plane or correct on the way |

## See which stage pays for what

The *Stages* list shows every stage that has delta-v, in the order KSP fires them, each
in its colour. A row shows the stage's delta-v in vacuum, what it has left at the end of
the route or whether it was dropped, and its engines.

Stages burn in KSP's order, and a stage burns until it is empty. The fuel a stage has
left after one leg goes on into the next. The same engines and tanks can serve the
launch, the transfer and the landing. The planner counts the mass each stage pushes at
that moment. A stage that burns later in the flight, when the vessel is lighter, goes
further.

### See the parts on the vessel

Open a leg, and the engines and tanks that pay for it light up green on the vessel.
Click a stage's name in the *Stages* list, and its engines and the tanks it burns light
up instead. Click it again, or close the leg, to clear the mark. The mark goes when you
switch to the engineer tab or close the view.

### Why tanks go with stages

The planner assigns fuel by stage, as KSP burns it. Which tank feeds which engine is set
by the vessel's staging and fuel lines, and the planner follows it. To give a tank to
other engines, change the staging.

### Keep a stage for a leg

An open leg shows the stages as buttons beside a pin. Press a stage there to keep it for
this leg, for example the descent stage of a lander for the landing. The legs before do
not touch the stage, nor any stage KSP fires after it. When the kept stage takes over,
the stages before it are dropped, with whatever they still hold. Press it again to let
it burn in its turn.

A mission to Eve needs a large stage for the climb back to orbit. Keep it for the leg
from Eve's surface to its orbit, and the planner does not burn it on the way there.

### Plan a lander that is used more than once

A vessel can carry a lander that flies down and back up alone while the rest waits in
orbit. The lander then docks again and goes on to the next destination.

1. Put the lander's decoupler or docking port in the stage right before the lander's
   first engines fire, so that KSP's staging separates it there.
2. In the *Stages* list, press the lander symbol on the lander's first stage with
   engines. It lights up on that stage and every stage after it. The stage with the
   decoupler counts to the lander as well.
3. Beside each leg, three symbols now set who flies it:

   | Symbol | Means |
   |---|---|
   | lander on a rocket | the whole vessel: the lander rides along, the rest burns first |
   | lander | the lander alone: the rest waits where the lander left it |
   | rocket with an open top | the rest without the lander, which stays behind for good |

A mission to the Mun and Minmus with one lander:

| Leg | Flown by |
|---|---|
| Kerbin orbit → Mun orbit | the whole vessel |
| Mun orbit → Mun surface | the lander alone |
| Mun surface → Mun orbit | the lander alone |
| Mun orbit → Minmus orbit | the whole vessel |
| Minmus orbit → Minmus surface | the lander alone |
| Minmus surface → Minmus orbit | the lander alone |
| Minmus orbit → Kerbin surface | the whole vessel |

The lander pushes only its own mass while it flies alone. When the whole vessel flies
on, it carries the lander with the fuel the lander has left. If a leg of the whole
vessel does not start where the rest waits, its figure turns orange. The open leg names
the stop where the rest is.

## What the numbers are

* Launches and landings are estimates from the body's gravity, its rotation and its
  atmosphere. They land within a few per cent of the usual delta-v maps. A steep or
  flat ascent costs more.
* Transfers are the ideal ones, launched in the transfer window. Out of the window a
  transfer costs more, so the margin matters.
* The planner counts every stage in vacuum. The launch burns in air, where the engines
  give less. To see a stage's delta-v at sea level, choose *Sea level* in the engineer
  tab.
* A launch warning compares the thrust in vacuum with the body's surface gravity. Check
  the launch stage's TWR at sea level in the engineer tab as well.
* The planner does not count refuelling, mining or fuel moved between vessels.
