# Plan a mission in the VAB and the SPH

**For:** players building a vessel for a mission to one or more destinations.
**You need:** ReDefinition installed, and a vessel in the VAB or the SPH.
**You get:** how to lay out a route, see what each step costs, check that the stages
carry enough fuel, and set yourself which stage burns on which step.

The planner lays out a route the way a map app lays out a trip: A → B → C → D. Between
two destinations it lists the steps the way needs, up into an orbit, across to the next
body, down onto its surface, and for each who flies it: the whole vessel or its lander.
It works out what each step costs and shares the vessel's stages out over them. You can
leave that to the planner, or save a stage for later or drop it after a step yourself. It
works with the bodies the game has loaded, so it also works with planet packs.

## Open the planner

Click the button with *Δv* on it, to the right of KSP's mode buttons above the parts
list. The planner slides in where the parts list was, as KSP's crew panel does. To go
back, click KSP's parts button, or any other mode button.

While the planner is open, the vessel's engines and tanks show in the colours of their
stages. The plan belongs to the vessel. It is saved under the vessel's name and comes
back when you load the vessel again.

## Name the lander and the capsule

The row *Vessel* at the top names the vessel's lander and capsule. They decide who can
fly a step.

| Button | Offers |
|---|---|
| *Lander* | *Automatic*, *None* or a stage: that stage and every stage after it are the lander, which can fly alone while the rest waits |
| *Capsule* | *Automatic*, *None* or a stage: that stage and every stage after it are the capsule, which comes home alone at the end |

*Automatic* shows in brackets what the planner found on the vessel:

* The capsule is the command part that stays to the end, with everything that stays with
  it, where that carries parachutes.
* The lander is the part before the capsule with engines and landing legs, separated by a
  decoupler or a docking port from a rest that has a command part of its own to wait.

A plain rocket with a pod and parachutes on top, such as the Kerbal X, has its capsule on
the parachutes' stage and no lander. A stage you pick yourself is bold and underlined.

## Lay out the route

A new vessel starts with two stops: *A* on the surface of Kerbin, *B* in a low orbit
around it.

| Control | Does |
|---|---|
| the body's name | folds out the list of bodies under it, each planet with its moons under it |
| *Surface* / *Orbit* | land on the body, or stay in orbit around it. The chosen one is lit green |
| *-*, the field and *+* | under an orbit: its altitude in kilometres. Type it, or lower or raise it by 10 km. It stays between the lowest safe orbit and the edge of the body's sphere of influence |
| *Chutes* | on a surface with air: land with a heat shield and parachutes. On thin air, such as Duna's, the engines still slow the last part. On the last stop it is set by itself where the vessel, or its capsule, carries parachutes |
| *Aerobrake* | on an orbit around a body with air: slow down in the atmosphere instead of with the engines |
| cross | remove the stop |
| *+*, after the next letter under the last stop | add a stop at the end of the route, up to eleven stops |

Every button says in its tooltip what it does.

Between two stops come the steps of the way from one to the next, each on a lighter
block of its own: a row with where it goes, who flies it, its bar, its delta-v with the
margin, and *...* where it has further choices, and the lines that belong to it. The steps are:

| Step | Comes |
|---|---|
| *To … orbit*, first | after a surface: the launch into the body's low orbit, or straight into the next stop's orbit around the same body |
| *To … orbit* | across to another body's orbit, or from one orbit to another around the same body |
| *To …'s air* | across to a body where the next stop is a landing on parachutes: straight into its atmosphere, with no orbit |
| *To … surface* | the landing on the next stop |

The bar's length is the step's delta-v, on the same scale for every step. Its colours
are the stages that pay for it. Red is what no stage covers. The figure turns red if
something blocks the step, and a red line under it says what: too little fuel, too
little thrust to lift off or land, no parachutes or no heat shield on what lands, or a
lander that is not where it has to be. Where nothing is wrong, there is no such line.

*...* shows and hides the step's further choices, each group on a line of its own:

| Line | Choice | Does |
|---|---|---|
| *Way down* | *Straight in* / *Orbit first* | on the way to a landing on parachutes on another body: straight into its atmosphere, or into a low orbit first |
| *Payload* | *Leave payload* | on the last step to a stop: what the vessel separates next, with no engines of its own, such as a satellite, stays there. The stage carrying it burns on with the fuel it has left. If there is no such payload, a red line says so |

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

With no step selected, the fuel plan lists every stage with delta-v in the order KSP
fires them, marked with its colour: what it gives over the whole route, and what it has
left at the end. In orange is where it was dropped and what it held then; in grey where
it stays, such as a lander left on a surface.

| Click | Does |
|---|---|
| a step's bar or its figure | shows the fuel plan of that step, and colours only its stages on the vessel |
| a stage's name | shows its engines and the steps it pays for, and colours only that stage on the vessel |
| *Whole route*, or the same step again | shows every stage again |

For a selected step, the fuel plan lists the stages it still has with fuel: what each
gives here and what it has left after the step. A stage burnt out or dropped before does
not show; a stage saved for later does. Under them come the step's burns, the stages
that pay for it with their TWR at that body, and any warning.

The burns of a step are:

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

## Save a stage or drop it after a step

Select a step, and each of its stages offers three choices:

| Choice | Does |
|---|---|
| *Burns* | the stage burns as KSP's staging comes to it. This is where every stage starts |
| *Save* | the stage does not burn on this step, and nor do the stages after it, since KSP cannot fire them first. If the step then runs short, it turns red |
| *Drop after* | the stage goes at the end of this step with what it still holds, and so do the stages KSP fires before it. The last stage cannot be dropped |

A mission to Eve needs a large stage for the climb back to orbit. Select each step on the
way there and click *Save* on that stage, and the planner keeps it for the climb. A
booster you drop early to lighten the vessel gets *Drop after* on the launch.

## Plan a lander and a capsule

A vessel can carry a lander that flies alone while the rest waits. The lander then docks
again and goes on, or flies on by itself and leaves the rest behind. A capsule is what
comes home at the end: it leaves the rest of the vessel for good, usually for the
landing on its parachutes.

1. Put the lander's decoupler or docking port in the stage right before the lander's
   first engines fire, so that KSP's staging separates it there.
2. Check *Lander* and *Capsule* in the row *Vessel*. Where *Automatic* found the wrong
   stage or none, pick the lander's first stage, or the capsule's, from the list. The
   list offers the lander's stages with the decoupler's stage before them, as that counts
   to the lander as well.
3. Every step now shows on a button who flies it, green. The planner sets it itself. A
   click on the button folds out the list of who can: *Ship*, *Lander*, *Without lander*,
   *Capsule*. What you pick there is bold and underlined; *Planner's choice* in the list
   gives the step back to the planner.

What happens follows from who flies one step after another:

| From one step to the next | Happens |
|---|---|
| *Ship*, then *Lander* | the lander leaves the rest, which waits where it is. A grey line says where |
| *Lander*, then *Ship* | the lander docks with the rest at the end of its step: *Docks with the rest*, with 50 m/s for the rendezvous, and the burn to the rest's orbit where it ended in another orbit of that body. Anywhere else a red line says it is not where the rest waits |
| *Lander*, and no *Ship* after | the lander flies on by itself; the rest stays where it waited |
| *Without lander* | the vessel flies on without the lander, which stays where it is for good |
| *Capsule* | the capsule leaves whatever flew the step before, for good. No step of the ship or the lander can follow |

Where the lander climbs from a surface and the rest waits in an orbit of that body, the
lander climbs to the rest's own orbit. *Refuel* on the line *Docks with the rest*
fills the lander up from the rest's tanks as it docks.

By itself, the planner has the lander land wherever the route goes on after the landing,
whoever stands on a surface fly up from it, the whole vessel fly on across to another
body where the lander has come back to the rest, and otherwise whoever flew the step
before. A landing on parachutes on the last stop is the capsule's where what carries it
is in the air. So a lander lands, climbs back, docks and is carried on without a click,
and the capsule comes home by itself.

For a landing on parachutes on another body, the step across ends in its air, and the
vessel goes straight in. *...* on that step offers *Orbit first* on the line *Way down*:
brake into a low orbit before the landing.

### Examples

A vessel to Minmus and home, without a lander:

1. *B*: pick Minmus, click *Surface*.
2. *+* under the last stop, pick Kerbin, click *Surface*. *Chutes* comes on by itself, as it
   does on every last stop with air where the vessel carries parachutes.

A satellite left in a synchronous orbit, the upper stage with the capsule landing at home.
Stage the satellite's decoupler right after the upper stage's engines:

1. *B*: type 2863 into the altitude field.
2. On the step to *B*, click *...* and *Leave payload*.
3. *+* under the last stop, click *Surface*; *Chutes* comes on by itself.

A lander that lands on Duna and flies home by itself, leaving the transfer stage in
Duna's orbit:

1. *B*: pick Duna, *Orbit*. *C*: Duna, *Surface*. *D*: Kerbin, *Surface*. *Chutes* comes
   on by itself on both.
2. Check that *Lander* names the lander. The landing on Duna is the lander's by itself.
3. On the step *To Kerbin's air*, pick *Lander* from its button. Without that the lander
   would dock and the whole vessel fly home.

A mission from Kerbin to Jool with one lander for Laythe and Vall, refilled after
Laythe, and the capsule landing at home:

1. Stops: Kerbin *Surface*; Kerbin *Orbit*; Jool *Orbit* with *Aerobrake*; Laythe
   *Orbit*; Laythe *Surface*; Vall *Orbit*; Vall *Surface*; Tylo *Orbit*; Kerbin
   *Surface*. *Chutes* comes on by itself on Laythe and Kerbin, each the last stop when
   it is set. A Laythe *Orbit* and a Vall *Orbit* after the landings
   can be stops as well; the lander climbs back to the rest either way.
2. Check *Lander* and *Capsule* in the row *Vessel*. The landings on Laythe and Vall are
   the lander's by itself; the last step, home, is the capsule's.
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
* The TWR under a step's stages, and the warnings on a launch or a powered landing, compare
  the thrust in vacuum with the body's surface gravity. Check the launch stage's TWR at
  sea level in the engineer window as well.
* Refuelling counts only between the lander and the rest. Mining and fuel from other
  vessels do not count.
