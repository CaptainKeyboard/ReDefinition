# Plan a mission in the VAB and the SPH

**For:** players building a vessel for a mission to one or more destinations.
**You need:** ReDefinition installed, and a vessel in the VAB or the SPH.
**You get:** how to lay out a route, see what each step costs, check that the stages
carry enough fuel, and set yourself which stage pays for which leg.

The planner lays out a route the way a map app lays out a trip: A → B → C → D. Between
two destinations it lists the steps the way needs, up into an orbit, across to the next
body, down onto its surface, and for each who flies it: the whole vessel or its lander.
It works out what each step costs and shares the vessel's stages out over them. You can
leave that to the planner, or pin stages to legs yourself. It works with the bodies the
game has loaded, so it also works with planet packs.

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
| *-*, the field and *+* | under an orbit: its altitude in kilometres. Type it, or lower or raise it by 10 km. It stays between the lowest safe orbit and the edge of the body's sphere of influence |
| *Chutes* | on a surface with air: land with a heat shield and parachutes. On thin air, such as Duna's, the engines still slow the last part |
| *Aerobrake* | on an orbit around a body with air: slow down in the atmosphere instead of with the engines |
| cross | remove the stop |
| *+ Add destination* | add a stop at the end of the route, up to eleven stops |

Between two stops come the steps of the way from one to the next, each on a row that
names where it goes:

| Step | Comes |
|---|---|
| *… orbit*, first | after a surface: the launch into the body's low orbit, or straight into the next stop's orbit around the same body |
| *… orbit* | across to another body's orbit, or from one orbit to another around the same body |
| *…'s air* | across to a body where the next stop is a landing on parachutes: straight into its atmosphere, with no orbit |
| *… surface* | the landing on the next stop |

Under each step, a bar. Its length is the step's delta-v, on the same scale for every
step. Its colours are the stages that pay for it. Red is what no stage covers. The
figure on the step's row is its delta-v with the margin. It turns red if something
blocks the step, and a red line under it says what: too little fuel, too little thrust
to lift off or land, no parachutes or no heat shield on what lands, or a lander that is
not where it has to be. Where nothing is wrong, there is no such line.

A step with *...* on its row has further choices; *...* shows and hides them. The last
step to a stop offers *Leaves payload*: what the vessel separates next, with no engines
of its own, stays at that stop, such as a satellite. The vessel's burning stage goes on
with the fuel it has left. If the planner finds no such payload, a red line says so.

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

A vessel can carry a lander that flies alone while the rest waits. The lander then docks
again and goes on, or flies on by itself and leaves the rest behind.

1. Put the lander's decoupler or docking port in the stage right before the lander's
   first engines fire, so that KSP's staging separates it there.
2. In the fuel plan, click *L* on the lander's first stage with engines. It lights up on
   that stage and every stage after it. The stage with the decoupler counts to the lander
   as well. Click it again to have no lander.
3. Every step now shows *Ship* and *Lander*: who flies it, lit green. The planner sets
   them itself; a click sets one yourself, and it is then underlined. Clicking the
   underlined one again gives it back to the planner.

What happens follows from who flies one step after another:

| From one step to the next | Happens |
|---|---|
| *Ship*, then *Lander* | the lander leaves the rest, which waits where it is. A grey line says where |
| *Lander*, then *Ship* | the lander docks with the rest at the end of its step: *Docks with the rest*, with 50 m/s for the rendezvous, and the burn to the rest's orbit where it ended in another orbit of that body. Anywhere else a red line says it is not where the rest waits |
| *Lander*, and no *Ship* after | the lander flies on by itself; the rest stays where it waited |
| *Rest*, behind *...* | the rest flies on without the lander, which stays where it is for good |

Where the lander climbs from a surface and the rest waits in an orbit of that body, the
lander climbs to the rest's own orbit. *Refuel* on the line *Docks with the rest* fills
the lander up from the rest's tanks as it docks.

By itself, the planner has whoever stands on a surface fly up from it, the whole vessel
fly on across to another body where the lander has come back to the rest, and otherwise
whoever flew the step before. So a lander clicked to land somewhere climbs back, docks
and is carried on without further clicks.

For a landing on parachutes on another body, the step across ends in its air, and the
vessel goes straight in. *...* on that step offers *Orbit first*: brake into a low orbit
before the landing, with the atmosphere where the stop has *Chutes*.

### Examples

A vessel to Minmus and home, without a lander:

1. *B*: pick Minmus, click *Surface*.
2. *+ Add destination*, pick Kerbin, click *Surface* and *Chutes*.

A satellite left in a synchronous orbit, the upper stage with the capsule landing at home.
Stage the satellite's decoupler right after the upper stage's engines:

1. *B*: type 2863 into the altitude field.
2. On the step to *B*, click *...* and *Leaves payload*.
3. *+ Add destination*, click *Surface* and *Chutes*.

A lander that lands on Duna and flies home by itself, leaving the transfer stage in
Duna's orbit:

1. *B*: pick Duna, *Orbit*. *C*: Duna, *Surface*, *Chutes*. *D*: Kerbin, *Surface*,
   *Chutes*.
2. Click *L* on the lander's first stage, then *Lander* on the step *Duna surface*.
3. On the step *Kerbin's air*, click *Lander*. Without that click the lander would dock
   and the whole vessel fly home.

A mission from Kerbin to Jool with one lander for Laythe and Vall, refilled after
Laythe, and the capsule landing at home:

1. Stops: Kerbin *Surface*; Kerbin *Orbit*; Jool *Orbit* with *Aerobrake*; Laythe
   *Orbit*; Laythe *Surface* with *Chutes*; Vall *Orbit*; Vall *Surface*; Tylo *Orbit*;
   Kerbin *Surface* with *Chutes*. A Laythe *Orbit* and a Vall *Orbit* after the landings
   can be stops as well; the lander climbs back to the rest either way.
2. *L* on the lander's first stage. *Lander* on the steps *Laythe surface*, *Vall
   surface* and the last *Kerbin surface*.
3. *Refuel* on the line *Docks with the rest* after Laythe.

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
