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

*Sections* at the top lists the parts of the vessel that fly on their own. They decide
who can fly a step. *+* adds one, and the list of its parts folds out right after:

| Section | Is | Row under it |
|---|---|---|
| *Capsule* | what comes home alone at the end, usually on its parachutes | a red line where it has no parachutes |
| *Lander* | what lands while the rest waits | *Afterwards*: *Docks again*, it comes back to the rest and docks, and the whole vessel flies on; or *Flies on alone*, it goes on by itself and leaves the rest behind |
| *Satellite* | what stays at a destination | *Deployed at*: the destination where it stays; the rest flies on without it |

A vessel can have several of each, such as two landers or three satellites; they are
numbered then. The parts in a section's list are what a decoupler or a docking port
separates, named after their command part or engine, with their number of parts. While
the pointer is on an entry, its parts light up on the vessel. A section can be part of
another, such as a capsule on a lander, but two sections cannot share some parts. The
cross removes a section.

The planner adds two sections by itself, shown as *Automatic* with what it found:

* On a crewed vessel, the capsule: the smallest part with a crewed command part and
  parachutes, else the smallest crewed one.
* On an uncrewed vessel, the satellite, where one part with a probe core and no landing
  legs leaves the launch stage behind. With several, you add them yourself.

A lander you always add yourself. *Afterwards* is *Docks again* by itself where a docking
port separates the lander, *Flies on alone* where a decoupler does. *Deployed at* is by
itself the first orbit the route reaches before it lands or ends. What you pick is bold
and underlined; picking it again gives it back to the planner. A section the planner
added and you removed stays away.

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
| *To …'s air* | across to a body where the next stop is a landing on parachutes: straight into its atmosphere, with no orbit |
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

The fuel plan lists every step of the route in its order:

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
| *TWR* | on a launch or a powered landing, its thrust in vacuum against the weight it pushes, at the body's surface; red where the stage that lifts off (the first of a launch) or sets down (the last of a landing) is below 1.2 on a body with air, below 1 otherwise |

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
| a lander that docks, then *Ship* | the lander docks with the rest at the end of its step: *Docks with the rest*, with 50 m/s for the rendezvous, and the burn to the rest's orbit where it ended in another orbit of that body. Anywhere else a red line says it is not where the rest waits |
| a section, and no *Ship* after | it flies on by itself; the rest stays where it waited |
| *Without lander* | the vessel flies on without the lander, which stays where it is for good |
| the capsule | it leaves whatever flew the step before, for good. Nothing else can fly after it |
| a satellite's destination reached | the satellite stays there, with what it weighs; the rest flies on lighter |

Where a lander that docks climbs from a surface and the rest waits in an orbit of that
body, it climbs to the rest's own orbit. *Refuel* on the line *Docks with the rest* fills
it up from the rest's tanks as it docks; the button beside it picks the stage of the rest
the refill comes from first, among those that still hold fuel there, or the rest in the
order KSP fires its stages. A lander that docks and is then left, *Without lander*,
docks first, so its climb counts the rendezvous.

By itself, the planner has a lander land wherever the route goes on after the landing,
whoever stands on a surface fly up from it, the whole vessel fly on across to another body
where a lander that docks again has come back to the rest, and otherwise whoever flew the
step before. A landing on parachutes on the last stop is the capsule's where what carries
it is in the air. So a lander lands, climbs back, and docks and is carried on or flies on
by itself without a click, and the capsule comes home by itself.

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
2. Check that *Sections* shows the satellite; *Deployed at* is *B* by itself.

A satellite with an engine of its own that circularises by itself, the upper stage left
in the transfer orbit:

1. *B*: *Orbit* at 80 km. *+*, *Orbit*, type 2863.
2. On the step to *C*, click *...* and *Split*. On *To Kerbin orbit* after *To Kerbin
   transfer orbit*, pick the satellite.

The Kerbal X to the Mun and home, its upper stage with the legs landing:

1. *B*: pick the Mun, *Surface*. *+*, Kerbin, *Surface*.
2. *+* under *Sections*, *Lander*, and pick the pod with the upper stage. *Afterwards* is
   *Flies on alone*; the capsule, found by itself, lands at home.

A lander that lands on the Mun and docks again, the capsule on the mothership coming
home, the lander left in the Mun's orbit:

1. Stops: Kerbin *Surface*, Kerbin *Orbit*, Mun *Orbit*, Mun *Surface*, Mun *Orbit*,
   Kerbin *Surface*.
2. Add the lander; check that the capsule is the command module.
3. On the step *To Kerbin's air*, pick *Without lander*. The landing is the capsule's by
   itself.

A lander that lands on Duna and flies home by itself, leaving the transfer stage in
Duna's orbit:

1. *B*: pick Duna, *Orbit*. *C*: Duna, *Surface*. *D*: Kerbin, *Surface*. *Chutes* comes
   on by itself on both.
2. Add the lander; *Afterwards* is *Flies on alone* where a decoupler separates it. The
   landing on Duna and the way home are then the lander's.

A mission from Kerbin to Jool with one lander for Laythe and Vall, refilled after
Laythe, and the capsule landing at home:

1. Stops: Kerbin *Surface*; Kerbin *Orbit*; Jool *Orbit* with *Aerobrake*; Laythe
   *Orbit*; Laythe *Surface*; Vall *Orbit*; Vall *Surface*; Tylo *Orbit*; Kerbin
   *Surface*. *Chutes* comes on by itself on Laythe and Kerbin, each the last stop when
   it is set. A Laythe *Orbit* and a Vall *Orbit* after the landings can be stops as
   well; the lander climbs back to the rest either way.
2. Add the lander; check the capsule. The landings on Laythe and Vall are the lander's
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
* The TWR under a step's stages, and the warnings on a launch or a powered landing, compare
  the thrust in vacuum with the body's surface gravity. Check the launch stage's TWR at
  sea level in the engineer window as well.
* Refuelling counts only between a docked section and the rest, as mass: it does not tell
  liquid fuel from oxidizer. A tank that no engine of its own burns belongs to the stage
  that burns it, so it cannot be picked as the tank a refill comes from. Mining and fuel
  from other vessels do not count.
