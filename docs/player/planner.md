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
around it.

| Control | Does |
|---|---|
| *<* and *>* | choose the body of the stop |
| *Surface* / *Orbit* | land on the body, or stay in orbit around it |
| *-* and *+* | lower or raise the orbit by 10 km. The lowest is just above the atmosphere or the highest terrain |
| *Aerobrake* | on an orbit: slow down in the atmosphere instead of with the engines, when arriving from another body or from a higher orbit |
| *Chutes* | on the surface: land with a heat shield and parachutes. On thin air, such as Duna's, the engines still slow the last part |
| *x* | remove the stop |
| *+ Add destination* | add a stop at the end of the route |

A mission to the Mun and back looks like this:

1. *A*: Kerbin, *Surface*
2. *B*: Kerbin, *Orbit*
3. *C*: Mun, *Orbit*
4. *D*: Mun, *Surface*
5. *E*: Mun, *Orbit*
6. *F*: Kerbin, *Surface*, with *Chutes*

## Read the legs

Between two stops, a line shows the leg's delta-v and the stages that pay for it. If
the planner adds a margin, the line shows the delta-v with the margin as well. Click the
line to see the burns the leg is made of:

| Burn | Is |
|---|---|
| *Launch to … orbit* | from the surface into the low orbit, with the losses to gravity and air |
| *Transfer burn* | leaving the orbit toward the next body |
| *Capture into orbit* | slowing down at the next body into its orbit |
| *Aerobraking, then circularise* | the small burn left after the atmosphere has slowed the vessel |
| *Raise or lower the orbit* | from one orbit to another around the same body |
| *Landing* | from the low orbit down to the surface |
| *Plane change* | turning the orbit to the tilt of the target's orbit |

Under the burns, the details show how much each stage gives to the leg. They also warn
you if a stage is too weak to launch or land on that body. A leg that no stage can pay
for in full shows *short* and the missing delta-v in orange.

## Set the margin and plane changes

| Setting | Does |
|---|---|
| *Margin* | adds a share to every leg, for steering, corrections and a late burn. 10 % is a common choice |
| *Count plane changes* | adds the burn that matches the tilt of the target's orbit. Leave it off if you launch into the right plane or correct on the way |

Below them, the planner sums up: what the route needs, what the vessel has, and whether
it is enough. If it is not enough, it names the first leg that runs short.

## See which stage pays for what

The *Stages* list shows every stage that has delta-v, in the order KSP fires them. For
each stage it shows:

* its delta-v in vacuum, and its engines
* the legs it pays for, and how much for each
* what it still has at the end of the route, or what it held when it was dropped

Stages burn in KSP's order, and a stage burns until it is empty. The fuel a stage has
left after one leg goes on into the next. The same engines and tanks can serve the
launch, the transfer and the landing. The planner counts the mass each stage pushes at
that moment. A stage that burns later in the flight, when the vessel is lighter, goes
further.

### See the parts on the vessel

Click a leg, and the engines and tanks that pay for it light up green on the vessel.
Click a stage in the *Stages* list, and its engines and the tanks it burns light up
instead. Click it again, or close the leg, to clear the mark. The mark goes when you
switch to the engineer tab or close the view.

### Why tanks go with stages

The planner assigns fuel by stage, as KSP burns it. Which tank feeds which engine is set
by the vessel's staging and fuel lines, and the planner follows it. To give a tank to
other engines, change the staging.

### Keep a stage for a leg

The button beside a stage says *Auto*: the stage burns when its turn comes. Click it to
keep the stage for one leg, for example *For C → D* for the descent stage of a lander.
The legs before that one do not touch the stage, nor any stage KSP fires after it.
When the kept stage takes over, the stages before it are dropped, with whatever they
still hold. The planner shows that as *dropped with …*.

This is how a vessel with its own ascent stage looks. A mission to Eve needs a large
stage for the climb back to orbit. Keep it for the leg from Eve's surface to its orbit,
and the planner does not burn it on the way there.

### Plan a lander that is used more than once

A vessel can carry a lander that flies down and back up alone while the rest waits in
orbit. The lander then docks again and goes on to the next destination.

1. Put the lander's decoupler or docking port in the lander's highest stage, so that
   KSP's staging separates it there.
2. In the *Stages* list, click *Lander* until it names that stage, for example
   *S2 to S0*.
3. Under each leg, set who flies it:

   | Choice | Means |
   |---|---|
   | *the whole vessel* | the lander rides along; the rest of the vessel burns first |
   | *the lander alone* | the lander flies; the rest waits where the lander left it |
   | *the rest, the lander stays* | the rest flies on without the lander, which stays behind for good |

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
vessel does not start where the rest waits, the leg shows a *!*. Its details name the
stop where the rest is.

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
