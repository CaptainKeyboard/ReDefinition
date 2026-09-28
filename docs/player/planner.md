# Plan a mission in the VAB and the SPH

**For:** players building a vessel for a mission to one or more destinations.
**You need:** ReDefinition installed, and a vessel in the VAB or the SPH.
**You get:** how to lay out a route, see what each step costs, check that every step has
enough fuel, and set yourself which stage burns on which step.

The planner lays out a route the way a map app lays out a trip: A → B → C → D. Between
two destinations it lists the steps the way needs, up into an orbit, across to the next
body, down onto its surface, and for each who flies it: the whole vessel or one of its
sections, such as a lander.
It works out what each step costs and shares the vessel's stages out over them. You can
leave that to the planner, or save a stage for later or drop it after a step yourself. It
works with the bodies the game has loaded, so it also works with planet packs.

## Open the planner

Click the button with *Δv* on it, to the right of KSP's mode buttons above the parts
list. The planner slides in where the parts list was, as KSP's crew panel does. To go
back, click KSP's parts button, or any other mode button.

The plan belongs to the vessel. It is saved under the vessel's name and comes
back when you load the vessel again.

## Add the vessel's sections

*Vessel* at the top lists the vessel's sections: the parts of it that fly on their own.
They decide who can fly a step. *+* adds one, and the list of its parts folds out right
after:

| Section | Is | Row under it |
|---|---|---|
| *Capsule* | what comes home alone at the end, usually on its parachutes | a red line where it has no parachutes |
| *Lander* | what lands while the rest waits | none: whether it docks again or flies on alone, the step after it says |
| *Satellite* | what stays at a destination | *Deployed at*: the destination where it stays; the rest flies on without it |

A vessel can have several of each, such as two landers or three satellites; they are
numbered then. Capsules come first, then the others in the order you added them. The parts
in a section's list are what a decoupler or a docking port separates, named after their
command part or engine, with their number of parts. While the pointer is on an entry, its
parts light up on the vessel. A section can be part of another, such as a capsule on a
lander, but two sections cannot share some parts. The cross removes a section.

The planner adds three sections by itself, shown as *Automatic* with what it found:

* On a crewed vessel, the capsule: the smallest part with a crewed command part and
  parachutes, else the smallest crewed one.
* On an uncrewed vessel, the satellite, where one part with a probe core and no landing
  legs leaves the launch stage behind. With several, you add them yourself.
* The lander: the smallest part with a command part, engines and landing legs whose
  engines do not fire at launch, such as the Kerbal X's upper stage. A booster with legs
  that lands by itself is none, and a lander gives way to the sections you set. A second
  lander you add yourself.

A lander with a docking port docks again after it lands, where the rest has a port as
well; one without flies on alone. The steps after it change either (see *Plan with
sections*). *Deployed at* is by itself the first orbit the route reaches before it lands
or ends. What you pick is bold and underlined; picking it again gives it back to the
planner. A section the planner added and you removed stays away.

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
| *To … transfer orbit* | where the way across is split: the burn that leaves; the next *To … orbit* is the one that arrives |
| *To …'s atmosphere* | across to a body where the next stop is a landing on parachutes: straight into its atmosphere, with no orbit |
| *To … surface* | the landing on the next stop |

The bar's length is the step's delta-v, on the same scale for every step. Its colours
are the stages that pay for it. Red is what no stage covers. The figure turns red if
something blocks the step, and a red line under it says what: too little fuel, too
little thrust to lift off or land, no parachutes or no heat shield on what lands, a
lander that is not where it has to be, or a satellite not carried to where it stays.
Where nothing is wrong, there is no such line. A grey line says what leaves at the start
of a step, and which satellite stays at its end.

*...* shows and hides the step's further choices, each group on a line of its own:

| Line | Choice | Does |
|---|---|---|
| *Burns* | *Together* / *Split* | on the way across to an orbit: one step, or two, the burn that leaves and the one that arrives, each with who flies it. So the upper stage can make the transfer burn and the satellite circularise by itself |
| *Way down* | *Straight in* / *Orbit first* | on the way to a landing on parachutes on another body: straight into its atmosphere, or into a low orbit first |

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

The fuel plan starts folded. The button before *Fuel plan*, or a click on its heading,
shows and hides it; beside the title it says whether every step has enough fuel. It lists
every step of the route in its order:

| Column | Shows |
|---|---|
| *Step* | where the step goes |
| *Flown by* | the whole vessel, or the section that flies it |
| *Needs* | the step's delta-v with the margin |
| *Left after* | what what flew the step has left once it is over, green: its own stages and those of the sections it carries, burning on in order. Red with a *-*: what the step lacks. Red: the step has a problem, named when it is open |

So every step has enough fuel where no figure in *Left after* is red.

*+* at the end of a step's row opens it: the stages it has with fuel as it starts, then
the burns it is made of with the margin, what is refilled there and what stays there,
and in red what blocks it. A stage used up, or dropped on a step before, does not
show any more. The same stage can show on several steps, as it burns on from one into
the next.

| Column | Shows |
|---|---|
| the colour and *S…* | the stage, in the colour it has on the step's bar |
| *Gives* | what it gives to this step |
| *Keeps* | what it has left after the step; *empty* where it is used up; orange *dropped*: what it held as it was dropped |
| *TWR* | on a launch or a powered landing, its thrust against the weight it pushes at the body's surface, in the air where the body has any; red where the stage that lifts off (the first of a launch) or sets down (the last of a landing) is below 1.2 on a body with air, below 1 otherwise |

## Check the thrust

*Thrust* under the fuel plan, folded and shown as it is, says beside its title whether the
thrust is enough everywhere, and lists every step that burns. On a launch and a landing on
the engines, the thrust has to lift the vessel. On every other burn it has to be done in
time: the vessel goes on round its orbit while it burns, so the longer a burn takes
against the orbit, the more of it pushes the wrong way. That delta-v is lost, and a
transfer burn that takes too long can miss its target.

| Column | Shows |
|---|---|
| *Needs* | on a launch or a landing on the engines, the TWR it needs: 1.20 to lift off from a body with air, 1.00 otherwise. On other steps, the share of the orbit a burn may take: 17 % |
| *Has* | on a launch or a landing, the stage that decides and its TWR at the surface, in the air where the body has any: the first stage of a launch, the last of a landing; red below what it needs. On other steps, the burn that takes the largest share of its orbit, with its time and share: orange from 11 %, where it loses 2 % of its delta-v, red from 17 %, where it loses 5 % |

*+* opens a step:

| Row | Shows |
|---|---|
| a stage | its TWR as it starts its part and as it ends it, in vacuum; *In air* on a launch or a landing where the body has air; its thrust in vacuum; how long it burns in this step |
| a burn in orbit | its name; how long it takes, its share of the orbit and the orbit's time, and about how much delta-v it loses; red with a *+* where the stages run out before it ends or a stage has no thrust |
| the body | the body the TWR is against, its surface gravity and its air |

A burn in red also shows as a line under its step on the route. More thrust or less mass
shortens a burn. A long burn out of a low orbit can also be split over several passes,
each at the same point of the orbit and raising the far side further; the planner counts
it as one.

## How the planner shares out the stages

By itself, the planner burns the stages in the order KSP fires them. A stage burns until
it is empty, and what it has left after one step goes on into the next. The same engines
and tanks can serve the launch, the transfer and the landing. The planner counts the
mass each stage pushes at that moment, so a stage that burns later, when the vessel is
lighter, goes further.

Which tank feeds which engine is set by the vessel's staging and fuel lines, and the
planner follows it. To give a tank to other engines, change the staging.

## Keep a stage for later, or drop it

Each stage of an open step has two choices:

| Choice | Does |
|---|---|
| *Use* | on where it starts: the stage burns on this step as KSP's staging comes to it. Off: it does not burn here, and nor do the stages after it, since KSP cannot fire them first, so theirs is dimmed too. If the step then lacks fuel, it turns red |
| *Drop after* | the stage goes at the end of this step with what it still holds, and so do the stages KSP fires before it. It is not there on the steps after. The last stage cannot be dropped |

A mission to Eve needs a large stage for the climb back to orbit. Open each step on the
way there and switch *Use* off on that stage, and the planner keeps it for the climb. A
stage you drop once in orbit, to lighten the vessel on the way to the Mun, gets *Drop
after* on *To Kerbin orbit*; its fuel is then gone for the steps after.

## Plan with sections

Every step shows on a button who flies it, green, as soon as the vessel has a section.
The planner sets it itself. A click on the button folds out the list of who can: *Ship*,
each section by its name, and for a lander *Without lander*. What you pick there is bold
and underlined; *Planner's choice* in the list gives the step back to the planner.

What happens follows from who flies one step after another:

| From one step to the next | Happens |
|---|---|
| *Ship*, then a section | the section leaves the vessel, which waits where it is, and takes the sections that are part of it along. A grey line says where |
| a section, then *Ship*, with steps of other sections in between or not | the section docks with the rest at the end of its step: *Docks with the rest*, with 50 m/s for the rendezvous, and the burn to the rest's orbit where it ended in another orbit of that body; a lander climbing from a surface climbs to the rest's own orbit. Anywhere else a red line says it is not with the rest of the vessel, and one where it has no docking port |
| a section, then the same section | it flies on by itself, and climbs to its own destination; the rest stays where it waited. Where the whole vessel flies on later without leaving it behind, a red line says it is not with the rest of the vessel |
| *Without lander* | the vessel flies on without the lander, which stays where it is for good; one back where the rest waits docks first, so its climb counts the rendezvous |
| the capsule | it leaves whatever flew the step before, for good. Nothing else can fly after it |
| a satellite's destination reached | the satellite stays there, with what it weighs; the rest flies on lighter |

So whether a lander docks again or flies on alone is the choice on the step after it, and
can differ from one landing to the next. *Refuel* on the line *Docks with the rest* fills
it up from the rest's tanks as it docks; the button beside it picks the stage of the rest
the refill comes from first, among those that still hold fuel there, or the rest in the
order KSP fires its stages.

By itself, the planner has a lander land wherever the route goes on after the landing,
whoever stands on a surface fly up from it, the whole vessel fly on across to another body
where a lander that can dock has come back to the rest, so that it docks again, and
otherwise whoever flew the step before. A landing on parachutes on the last stop is the
capsule's where what carries it is in the air. So a lander lands, climbs back, and docks
and is carried on or flies on by itself without a click, and the capsule comes home by
itself.

For a landing on parachutes on another body, the step across ends in its air, and the
vessel goes straight in. *...* on that step offers *Orbit first* on the line *Way down*:
brake into a low orbit before the landing.

### Examples

A vessel to Minmus and home, without a lander:

1. *B*: pick Minmus, click *Surface*.
2. *+* under the last stop, pick Kerbin, click *Surface*. *Chutes* comes on by itself, as it
   does on every last stop with air where the vessel carries parachutes.

A satellite left in a synchronous orbit by an uncrewed rocket:

1. *B*: type 2863 into the altitude field.
2. Check that *Vessel* shows the satellite; *Deployed at* is *B* by itself.

A satellite with an engine of its own that circularises by itself, the upper stage left
in the transfer orbit:

1. *B*: *Orbit* at 80 km. *+*, *Orbit*, type 2863.
2. On the step to *C*, click *...* and *Split*. On *To Kerbin orbit* after *To Kerbin
   transfer orbit*, pick the satellite.

The Kerbal X to the Mun and home, its upper stage with the legs landing:

1. *B*: pick the Mun, *Surface*. *+*, Kerbin, *Surface*.
2. Check that *Vessel* shows the lander, the pod with the upper stage, and the capsule.
   Without a docking port, the lander flies home by itself; the capsule lands at home.

A lander that lands on the Mun and docks again, the capsule on the mothership coming
home, the lander left in the Mun's orbit:

1. Stops: Kerbin *Surface*, Kerbin *Orbit*, Mun *Orbit*, Mun *Surface*, Mun *Orbit*,
   Kerbin *Surface*.
2. Check the lander and that the capsule is the command module.
3. On the step *To Kerbin's atmosphere*, pick *Without lander*. The landing is the capsule's by
   itself.

A lander that lands on Duna and flies home by itself, leaving the transfer stage in
Duna's orbit:

1. *B*: pick Duna, *Orbit*. *C*: Duna, *Surface*. *D*: Kerbin, *Surface*. *Chutes* comes
   on by itself on both.
2. Check the lander. The landing on Duna is its own by itself.
3. Where it has a docking port, pick *Lander* on the step *To Kerbin's atmosphere*:
   without that, it would dock with the transfer stage and the whole vessel fly home.

A mission from Kerbin to Jool with one lander for Laythe and Vall, refilled after
Laythe, and the capsule landing at home:

1. Stops: Kerbin *Surface*; Kerbin *Orbit*; Jool *Orbit* with *Aerobrake*; Laythe
   *Orbit*; Laythe *Surface*; Vall *Orbit*; Vall *Surface*; Tylo *Orbit*; Kerbin
   *Surface*. *Chutes* comes on by itself on Laythe and Kerbin, each the last stop when
   it is set. A Laythe *Orbit* and a Vall *Orbit* after the landings can be stops as
   well; the lander climbs back to the rest either way.
2. Check the lander and the capsule. The landings on Laythe and Vall are the lander's
   by itself; the last step, home, is the capsule's.
3. *Refuel* on the line *Docks with the rest* after Laythe.

A section pushes only its own mass while it flies alone. When the whole vessel flies
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
* The TWR compares the thrust with the body's surface gravity: in the air there, the
  thrust KSP's engines give at that body's sea level; otherwise in vacuum.
* Burn times are for the thrust in vacuum. A burn's share is of the circular orbit it
  starts from, and what it loses is an estimate for a burn held in one direction and
  centred on its point.
* Refuelling counts only between a docked section and the rest, as mass: it does not tell
  liquid fuel from oxidizer. A tank that no engine of its own burns belongs to the stage
  that burns it, so it cannot be picked as the tank a refill comes from. Mining and fuel
  from other vessels do not count.
