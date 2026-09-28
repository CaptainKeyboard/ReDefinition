# Plan a mission in the VAB and the SPH

**For:** players building a vessel for a mission to one or more destinations.
**You need:** ReDefinition installed, and a vessel in the VAB or the SPH.
**You get:** how to lay out a route, see what each step costs, check that the stages
carry enough fuel, and set yourself which stage pays for which leg.

The planner lays out a route the way a map app lays out a trip: A → B → C → D. For each
destination you say who flies there, the whole vessel or its lander. The planner works
out the steps that follow from it, what each costs, and shares the vessel's stages out
over them. You can leave that to the planner, or pin stages to legs yourself. It works
with the bodies the game has loaded, so it also works with planet packs.

## Open the planner

Click the button with *Δv* on it, to the right of KSP's mode buttons above the parts
list. The planner slides in where the parts list was, as KSP's crew panel does. To go
back, click KSP's parts button, or any other mode button.

While the planner is open, the vessel's engines and tanks show in the colours of their
stages. The plan belongs to the vessel. It is saved under the vessel's name and comes
back when you load the vessel again.

## Lay out the route

A new vessel starts with two stops: *A* on the surface of Kerbin, *B* in a low orbit
around it.

| Control | Does |
|---|---|
| the body's name | opens the list of bodies, each planet with its moons under it |
| *Surface* / *Orbit* | land on the body, or stay in orbit around it. The chosen one is lit green |
| *-* and *+* | under an orbit: lower or raise it by 10 km, down to the lowest safe one |
| *Chutes* | on a surface with air: land with a heat shield and parachutes. On thin air, such as Duna's, the engines still slow the last part |
| *Aerobrake* | on an orbit around a body with air: slow down in the atmosphere instead of with the engines |
| cross | remove the stop |
| *Ship* / *Lander* | with a lander set (see below): who flies to this stop |
| *+ Add destination* | add a stop at the end of the route, up to nine stops |

Between two stops, the steps of the way from one to the next, a bar each. A bar's
length is the step's delta-v, on the same scale for every step. Its colours are the
stages that pay for it. Red is what no stage covers. The figure beside the bar is the
step's delta-v with the margin. It turns red if something blocks the step, and a red
line under the step says what: too little fuel, too little thrust to lift off
or land, no parachutes or no heat shield on what lands. Where nothing is wrong, there is
no such line.

## Check the fuel

Under the route, one bar sums up the mission: the stages' colours for what they burn,
grey for what is left over, red for what is missing. Under it, green with a *+* is what
is left at the end, orange with a *-* is what is missing. Then come what the route
needs and what the vessel has.

| Setting | Does |
|---|---|
| *Margin* | adds a share to every leg, for steering, corrections and a late burn. 10 % is a common choice |
| *Plane changes* | adds the burn that matches the tilt of the target's orbit. Leave it off if you launch into the right plane or correct on the way |

## Read the fuel plan

The fuel plan is a table. Each row is a stage with delta-v, in the order KSP fires
them, marked with its colour. Each column is a leg. A cell shows how much delta-v the
stage gives to that leg. The last column shows what the stage has left at the end, or,
in orange, what it held when it was dropped.

| Click | Does |
|---|---|
| a leg's name at the top of a column, a step's bar or its figure | shows each step of the leg with its burns, the stages that pay for it with their TWR at that body, and any warning, and colours only those stages on the vessel |
| a stage's name | shows its engines and the legs it pays for, and colours only that stage on the vessel |
| the same again | shows every stage in its colour again |

The burns of a leg are:

| Burn | Is |
|---|---|
| *Launch to … orbit* | from the surface into the low orbit, with the losses to gravity and air |
| *Transfer burn* | leaving the orbit toward the next body |
| *Capture into orbit* | slowing down at the next body into its orbit |
| *Aerobraking, then circularise* | the small burn left after the atmosphere has slowed the vessel |
| *Raise or lower the orbit* | from one orbit to another around the same body |
| *Landing* | from the low orbit down to the surface |
| *Plane change* | turning the orbit to the tilt of the target's orbit |
| *Deorbit into the air* | from an orbit down into the atmosphere, for a landing on parachutes |
| *Rendezvous and docking* | the lander finding the rest in its orbit, 50 m/s over the climb |

## How the planner shares out the stages

By itself, the planner burns the stages in the order KSP fires them. A stage burns until
it is empty, and what it has left after one leg goes on into the next. The same engines
and tanks can serve the launch, the transfer and the landing. The planner counts the
mass each stage pushes at that moment, so a stage that burns later, when the vessel is
lighter, goes further.

Which tank feeds which engine is set by the vessel's staging and fuel lines, and the
planner follows it. To give a tank to other engines, change the staging.

## Pin a stage to a leg

Click a cell of the fuel plan to pin that stage to that leg. A pin shows in the cell's
corner. Click the cell again to take the pin away.

* A leg with pins burns its pinned stages only, in KSP's order. If they are not enough,
  the leg runs short.
* A stage can be pinned to several legs. Pin a lander's descent stage to the landing and
  to the climb back, and both burn from it.
* The legs without pins burn in KSP's order and stop before a stage pinned to a later
  leg, since KSP cannot fire the stages after it either.
* When a pinned stage fires, the stages before it are dropped with what they still hold.
  The last column shows that in orange.
* A pin to a stage that is already dropped when its leg comes turns the leg orange. The
  leg's details name the stage.

A mission to Eve needs a large stage for the climb back to orbit. Pin it to the leg from
Eve's surface to its orbit, and the planner does not burn it on the way there.

## Plan a lander that is used more than once

A vessel can carry a lander that flies down and back up alone while the rest waits in
orbit. The lander then docks again and goes on to the next destination.

1. Put the lander's decoupler or docking port in the stage right before the lander's
   first engines fire, so that KSP's staging separates it there.
2. In the fuel plan, click *L* on the lander's first stage with engines. It lights up on
   that stage and every stage after it. The stage with the decoupler counts to the lander
   as well. Click it again to have no lander.
3. Under each destination, *Ship* or *Lander* now sets who flies there. The steps in
   between follow from it:

   | Step | Is |
   |---|---|
   | *Ship* | the whole vessel: the lander rides along, its fuel kept for its own steps |
   | *Lander* | the lander alone, from where the rest waits |
   | *Dock* | the lander, away, back up to the rest and docked |
   | *Rest* | the rest on its own, once the lander stays behind |

   Where the lander lands on another body, the whole vessel carries it into an orbit of
   that body first, and waits there. For a landing on parachutes it carries it straight
   into the air instead, and is left there.

Some steps offer a choice on a line under them:

| Choice | Does |
|---|---|
| *Dock* / *Stays* | the lander comes back and docks, or stays where it is while the rest flies on |
| *Refuel* | once docked, the lander is filled up from the rest's tanks |
| *Direct* / *Orbit first* | for a landing on parachutes: the vessel goes straight into the air, or brakes into an orbit before the lander leaves |

A mission from Kerbin to the Mun's surface and back, the lander landing at home on its
parachutes:

| Destination | Flown by | Steps before it |
|---|---|---|
| A Kerbin, surface | | |
| B Kerbin, orbit | *Ship* | *Ship*: the launch |
| C Mun, orbit | *Ship* | *Ship*: transfer and capture |
| D Mun, surface | *Lander* | *Lander*: the landing, the rest waiting in the Mun's orbit |
| E Kerbin, surface, *Chutes* | *Lander* | *Dock*: up to the rest; *Ship*: home into Kerbin's air; *Lander*: down on its parachutes |

The lander pushes only its own mass while it flies alone. When the whole vessel flies
on, it carries the lander with the fuel the lander has left, or refilled.

## What the numbers are

* Launches and landings are estimates from the body's gravity, its rotation and its
  atmosphere. They land within a few per cent of the usual delta-v maps. A steep or
  flat ascent costs more.
* Transfers are the ideal ones, launched in the transfer window. Out of the window a
  transfer costs more, so the margin matters.
* The planner counts every stage in vacuum. The launch burns in air, where the engines
  give less. To see a stage's delta-v at sea level, choose *Sea level* in the
  [engineer window](engineer.md).
* The TWR in the details, and the warnings on a launch or a powered landing, compare
  the thrust in vacuum with the body's surface gravity. Check the launch stage's TWR at
  sea level in the engineer window as well.
* Refuelling counts only between the lander and the rest. Mining and fuel from other
  vessels do not count.
