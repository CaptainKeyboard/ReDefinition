# The planner

**For:** anyone changing the planner in the VAB and the SPH.
**You need:** the repository, and `src/Engineer/Planner` open.
**You get:** how the planner turns a route into steps and works out their delta-v, how
it assigns the vessel's stages to them, and where its plans are kept.

The player's side is in [player/planner.md](../player/planner.md). How the planner's
panel sits in KSP's editor is in [engineer.md](engineer.md).

## The parts of the planner

| File | Does |
|---|---|
| `src/Engineer/Planner/RoutePlanner.cs` | the delta-v of the way from one point to another. Free of Unity |
| `src/Engineer/Planner/RouteSteps.cs` | the steps between the destinations, who flies each, and where the lander leaves and docks. Free of Unity |
| `src/Engineer/Planner/StageSim.cs` | the vessel's stages worked out as KSP's simulation does, with the air-breathing engines' part of the launch (`AirLaunch`), where an engine breathes air. Free of Unity |
| `src/Engineer/Planner/ClampedLaunch.cs` | the stages lit while launch clamps hold the vessel, burning together with the stage that releases them. Free of Unity |
| `src/Engineer/Planner/BurnCheck.cs` | how long each burn in orbit takes with the stages that fly it, against its orbit. Free of Unity |
| `src/Engineer/Planner/StageBudget.cs` | which stage pays for which step, on the mass the stages push, the stages saved or dropped on a step, and the lander refilled from the rest. Free of Unity |
| `src/Engineer/Planner/VesselShape.cs` | the parts of the vessel that can fly apart, the lander and the capsule found among them, and the stages shared out over them. Free of Unity |
| `src/Engineer/Planner/PlannerBodies.cs` | the bodies of the loaded system, read from `FlightGlobals.Bodies` |
| `src/Engineer/Planner/PlannerState.cs` | one vessel's plan, and its file |
| `src/Engineer/Planner/PlannerView.cs` | the plan: loading, saving, working it out, the choices the rows make, the parts marked on the vessel |
| `src/Engineer/Planner/PlannerView.Sections.cs` | the vessel's sections: found, resolved against the parts, named, handed to the route and the budget |
| `src/Engineer/Planner/PlannerView.Rows.cs` | the planner's rows, built from KSP's dialog elements |
| `src/Engineer/Planner/PlannerWidgets.cs` | the symbols, the coloured bars, the stage colours and the list that folds out under a button (`Dropdown`) |
| `src/Engineer/Planner/PartHighlight.cs` | marks parts on the vessel with KSP's part highlighter, one marker at a time |
| `src/Engineer/PlannerPanel.cs` | the panel the rows stand in, made as KSP's crew panel is |

## How the planner is drawn

The planner uses no text to explain itself. From the top:

* Every choice carries a tooltip of one sentence. KSP's button prefab has no tooltip
  controller, so `DialogTooltips.Attach` adds the game's own to every element with a
  tooltip once the rows are created.
* *Craft*: on its header bar the craft as built (`PlannerView.VesselText`: `EditorReadout`
  mass, fuel mass without crew and the stages' delta-v in vacuum), then up to six section
  rows, each with a dot as a stop has, its name in a column of 90 (`SectionName`), each
  with what it is in the column of a step's place, its parts on a button from the column
  of who flies a step, and its cross where a stop's cross is; under a satellite *Deployed
  at*, and a red line for a capsule without parachutes or a section overlapping another.
  *+* opens the list of kinds, and the new section's list of parts right after. A list of
  parts folds out as wide as its button: *Automatic* where the planner finds that kind,
  with what it found in brackets, and the parts that can fly apart. An entry lights its
  parts on the vessel while the pointer is on it (`Choice.Hover`, an `EventTrigger` on the
  entry's button).
* The route is a line of stops, an orbit's altitude under it as a field
  (`DialogGUITextInput`); while it has the keyboard, the game's controls are locked
  (`PlannerView.Keep`), so that the digits do not reach the editor's shortcuts. Between
  two stops, each step in a block on a lighter background (`Tint`, from the timeline on),
  with a gap to the next, and in it one row in fixed columns: where it goes; who flies it,
  a button under which the list of who can folds out (`Dropdown`), where the vessel has a
  section, the player's own choice bold and underlined; its bar, a row of
  `DialogGUIImage`s whose widths and colours `SegmentBar` sets after they are created; its
  delta-v; and *...* for its further choices. Around it only where there is something: a
  grey line where something leaves at its start, *Docks with the rest* with *Refuel* where
  it docks at its end, a grey line for the satellites that stay there, the further choices
  in groups of their own (*Burns*, *Way down*), and a line for each problem that blocks it
  (`PlannerView.StepWarnings`), up to three, in red, shown only while there is one.
* Where KSP's delta-v simulation is off (`DELTAV_CALCULATIONS_ENABLED` in `settings.cfg`,
  or another mod through `DeltaVGlobals`), KSP makes no stages. An orange line at the top
  then says so (`PlannerView.StockOffRow`, `EditorReadout.StockOff`).
* Each part (*Craft*, *Route*, *Fuel plan*, *Thrust*) is a header bar, lighter, rounded at
  the top (all round while folded, `ShapeHeaders`), and a box, darker, rounded at the
  bottom (`Rounded`: a 16-pixel white shape with 5-pixel corners, 9-sliced), holding its
  rows (`PlannerView.Section`, colours drawn by `Tint.Behind`); a click anywhere on the
  bar folds the part (`CollapsibleHeading`, its background answering the pointer), *+* or
  *-* at its right; the box of a folded part hides. The rows stand 8 in from the box's
  edges on each side (`BoxMargin`: `width` is the rows', `outer` the boxes'), so that the
  buttons at their right end line up; the bar's sign stands 6 in from its edge. *Route*
  starts with *Fuel margin* and *Plane changes* and ends with one bar summing up the
  route.
* The header bars carry a summary: Fuel plan and Thrust always, the steps short of fuel,
  the steps with too low a TWR and the burns too long; Craft always, its mass, fuel and
  delta-v; Route while folded, its balance.
* The fuel plan: a row per step, up to 30, in the columns of the step rows (where it
  goes, who flies it), with what it needs and what what flew it has left after
  (`StageBudget.Result.LeftAfter`), in red what it lacks or where it has a problem; *+*
  where a step's "..." is opens it to a row per stage it has with propellant as it
  starts (`Result.Stages`), up to ten and a line for more: a swatch, a `SegmentBar` of one
  piece coloured after the rows are created; what it gives there and keeps after, or held
  as it was dropped or kept as it stayed; its TWR on a launch or a powered landing, red
  only on the stage the warnings judge; *Use*, dimmed behind a stage kept for later, and
  *Drop after*, which end where a stop's cross does; then the step's burns and margin, a
  refill (`Refuelled`) and what stays (`Payload`), and its warnings. A closed step writes
  no texts for its stage rows. The rows above select nothing: the fuel plan shows what it
  shows without a click on the route.
* *Thrust*: a row per step that burns (`PlannerView.UpdateThrust`, worked out with the
  budget, before the warnings), up to 30. On a launch or a landing on the engines, what it
  needs and the deciding stage's TWR (`PlannerView.TwrAt`); on other steps the largest
  share of its orbit a burn takes (`BurnCheck`). Open, each stage it burns with its TWR as
  it starts and ends (`StageUse.Mass`, `EndMass`), in the air, its thrust and burn time;
  each burn in orbit; notes (`AddNotes`): the RCS of a section that docks
  (`PartFacts.RcsThrust` at the thrust limit, `RcsThrustOverIsp`, `RcsBurns`,
  `RcsResources` from `ModuleRCS`: the resources its own switched-on thrusters burn, in
  the parts they draw them from as `Part.GetConnectedResourceTotals` does, the editor's
  crossfeed set for a Vernor's liquid fuel and oxidizer and the whole vessel for
  monopropellant, no more than the section's mass after the step holds over their dry
  mass); the body. The Isp column is the stage's exhaust speed in the budget over g0. In
  the air, the thrust at the body's sea level (`StageRow.ThrustAtSurface`:
  `ModuleEngines.MaxThrustOutputAtm` at each body's `atmPressureASL`,
  `atmosphereTemperatureSeaLevel` and `atmDensityASL`, as KSP's `DeltaVEngineInfo` does
  for its delta-v app's body); the warnings of a launch and a landing use the same, and
  the step's longest burn losing more than 5 % adds a warning, after the others.

Choices are buttons of KSP's skin whose text is lit green while their option is chosen.
The skin's pressed state of a toggle button reads the other way round, so it is not
used. Symbols are `GameData/ReDefinition/Icons/Planner_*.png`, white on clear, drawn by
`tools/make_icon.py`. The list of bodies folds out under the body's button (`Dropdown`).

The planner marks parts on the vessel only while the pointer is on an entry of a
section's list: that entry's parts, green (`PartHighlight`). With the pointer on a
part of the vessel nothing is marked, so that KSP highlights it as it does without the
mod.

KSP's layouts take no width of their own: `DialogGUILayoutBase` gives a row the width of
its elements, not the one it is handed **[src]**. So every row's elements are summed to
the room the scroll view leaves: it insets its content 4 units at each side, and its bar,
16 wide, stands 3 units in from the right edge (the prefab, read with UnityPy) **[src]**.
`PlannerWidgets.RowRoom` gives that room. The list reaches out from the crew panel's
content area to 2 units short of the panel's right edge, so that its bar sits there as the
parts list's does; in the 488 units that gives it is 465. A button hidden from a row
leaves a space of its width, so that the crosses of all stops line up in one column. While
any panel slides, every mode button is locked, since the planner's transitions pass KSP's
own lock by, and a mode chosen during one left the planner standing.

The scroll view's own background is cleared (`PlannerWidgets.Settle`), so the panel shows one
background, as the crew panel does. The planner takes itself out when KSP brings in another
panel, since KSP's cargo mode, which brings in the parts list and the cargo panel together,
left it standing.

The rows are laid out all at once, and the ones the plan does not need hide after. The scroll
list's content is therefore held at its top edge, so that it shrinks from the bottom. The
planner has room for 11 stops, four steps between two of them, and 30 stages. It is
worked out again four times a second while it is open. The free-of-Unity parts have unit
tests in `tests/ReDefinition.Tests/PlannerTests.cs`.

## Where the bodies come from

`PlannerBodies.Read` takes every body from `FlightGlobals.Bodies`: its gravitational
parameter, radius, rotation period, atmosphere depth and sea-level pressure, and its
orbit's semi-major axis and inclination **[src]**. So the planner works for whatever
system is loaded, the stock one or one a Kopernicus planet pack builds. The list of
bodies shows the star, then each body followed by its moons, by the size of their
orbits.

A body's low orbit is 10 km above its atmosphere. On a body without air it is 5 km
above the highest terrain, `pqsController.radiusMax` **[src]**. If a body has no
terrain, it is a twentieth of its radius plus 5 km. The altitude is kept inside the
sphere of influence and rounded up to whole kilometres. The first read writes every
body's low orbit to `KSP.log`, in a line that starts with `Planner low orbits:`.

## How the route becomes steps

`RouteSteps.RolesBetween` gives the steps between two stops (`StepRole`): `Launch` after a
surface, into the low orbit or straight into the next stop's orbit around the same body;
`Transfer` across to another body, or from one orbit to another around the same one;
`Land` onto the next stop's surface. A transfer to a landing on parachutes on another
body ends in its air (`StopKind.Entry`), unless the stop has `OrbitFirst`. Where the stop
has `SplitBurns` and the way across has a burn that leaves and one that arrives
(`RouteSteps.Splits`), the transfer ends on the way across (`StopKind.Transfer`, "…
transfer orbit") with the burn that leaves (`RoutePlanner.LeavingBurn`), and `Arrive`
follows with the other burns: `LegOf` takes each part from the leg of the whole way
(`RouteStop.Origin`, `Final`). A change of orbit around one body is a *Transfer burn* and
a *Circularise* for that.

Each step is flown by a `Flyer`: the whole vessel (`Ship`); one section alone, with the
sections that are part of it (`Alone`); or the vessel leaving a section behind for good
(`Without`). A section (`RouteSection`) is part of another or of the rest (`Parent`),
docks again by itself or not (`Docks`: a lander with a docking port where the rest has
one, `VesselShape.CanDock`; a section docking without one is a problem, `CanDock`), and a
satellite stays at a destination (`DeployAt`). The player sets who flies per step
(`RouteStop.By`, by role, on the stop the step leads to, as `Flyer.Code`), or
`RouteSteps.Build` works it out: the capsule once it has left; a capsule for a landing on
parachutes on the last stop where what carries it is in the air; for a landing short of
the last stop the lander that flew last, else the first; from a surface whoever stands on
it; across to another body `Ship` where a lander that docks is in an orbit of the body the
rest waits at; otherwise whoever flew the step before. `Build` works out who flies each
step first, then walks the steps again with those, as a section's climb goes to the rest's
own orbit only where it docks after it: where the next step of the whole vessel, or the
one that leaves it behind, comes before it flies again itself or a capsule leaves
(`Rejoins`), steps of other sections in between. It keeps where the rest and each section
are:

| Step flown by | While | Then |
|---|---|---|
| `Ship` | every section is with the rest | the whole vessel flies |
| `Ship` | a section is away, where the rest waits or in another orbit of that body | its last step `Docks`, with the burn to the rest's orbit where it ended in another one, 50 m/s for the rendezvous where the rest is in orbit, and is refilled where its stop's `Refuel` is set for that step |
| `Ship` | a section is away, elsewhere | a `Problem`: it is not with the rest of the vessel |
| `Alone` | the section is with what carries it | it `Separates` (`Leaver`, `LeavesFrom`), and the rest waits where it is |
| `Alone` | the section is away | it flies on from where it is; a rest never met again stays where it waited |
| `Alone` | a capsule, for the first time | everything else is gone for good (`Gone`): dropped where it was with the capsule, left where it waited |
| `Without` | | the section stays where it is for good (`Gone`, `Stays`); where it flew the step before and is back where the rest waits, it docks first; a later step of it is a `Problem` and costs nothing |

At the end of the last step to a satellite's `DeployAt`, the satellite stays there
(`Deploys`), carried there or flown there itself, with the sections part of it; a lander
part of it leaves it there and flies on its own. Where it was not carried, a `Problem`
says so. A section that docks does so where it is part of the rest, or of a section
still with the rest (`Carrier`). A section that
docks and climbs from a surface while the rest waits in an orbit of that body climbs to
the rest's own orbit. A step that starts in the air and is no landing is a `Problem`.
Without sections, every step is the whole vessel's. Each step names what flies it
(`Flying`: 0 the rest, else the sections' ids), for the budget and the warnings.

The missions of `PlannerTests` are set up as the player sets them, and check the steps
that come out: a vessel to Minmus and home; a satellite left in a synchronous orbit, and
one that circularises by itself after the upper stage's transfer burn; a Duna lander
flying home by itself; a lander hopping on Minmus from a Mun base; a Mun lander under a
mothership whose capsule comes home; Jool missions with two landings, a refill and a
capsule landing at home, the capsule part of the lander or of the mothership; a satellite
deployed, and one not carried to where it stays; two landers.

## How a step's delta-v is worked out

`RoutePlanner.LegOf` adds the burns from one point to the next:

| Burn | Worked out as |
|---|---|
| launch | 1.07 × the low orbit's circular speed, less the ground's rotation speed. On a body with air, plus √(g × atmosphere depth) × (1.33 + 0.0964 × p²), with p the sea-level pressure in atmospheres. Without air, plus 10 m/s |
| transfer and capture | a Hohmann transfer between circular orbits around the common parent of the two bodies, with patched conics at each sphere of influence |
| orbit change around one body | a Hohmann transfer between the two orbits. When aerobraking down to a lower orbit, the burn down to half the atmosphere's depth, and the aerobraking share below |
| landing | the launch without the atmosphere's share. With parachutes, 0 on air of 0.2 atm or more, and a share of 30 % of the orbital speed that falls to 0 at 0.2 atm |
| aerobraking | 3 % of the circular speed at the arrival orbit, for raising the periapsis out of the air |
| plane change | 2 × v × sin(Δi / 2), with v the slower of the transfer's two ends. Δi is the tilt between the two orbits around the common parent. Between a body and its own moon, it is the moon's tilt |
| into the air | from another body, the transfer without a capture; from an orbit of the body, the burn down to half the atmosphere's depth, also before a landing on parachutes from that orbit. The landing from there is the landing's share alone |

Each burn in a circular orbit keeps its body and the orbit's radius (`LegLine.Body`,
`Radius`): a transfer burn where it leaves, a capture and a circularisation where it
arrives, a deorbit where it starts; an aerobraking to a lower orbit of one body is the
deorbit and the circularisation, and a climb to the rest's orbit its two burns.
`BurnCheck.Burns` takes the step's burns in their order from its stages in theirs, each at
its vacuum thrust and its exhaust speed (the stage's delta-v over ln(start mass / end
mass)): a burn of Δv from mass m takes m (1 − e^(−Δv/v_e)) v_e / F. Against the orbit's
period T, the burn spans the arc a = 2π t / T; held in one direction and centred on its
point, it keeps sin(a/2) / (a/2) of its delta-v (`Burn.Loss`). Above 2 % lost it is
orange, above 5 % red and a warning, at about 11 % and 17 % of the orbit.

The launch figure is fitted, not simulated. The factors come from the stock system's
known launch figures: Kerbin 3,400 m/s, Duna 1,450, Eve 8,000, Laythe 2,900, the Mun
580 and Tylo 2,270. Each lies within 4 % **[meas]**, measured in the unit tests against
the stock bodies' values.

The transfers follow the patched-conic model KSP itself flies. A burn in a low orbit
leaves the body with the excess speed the transfer around the common parent needs.
From a moon, the burn leaves the moon and its planet in one go. `Leave` works this out
level by level, from the child of the common parent down to the start body. The capture
at the far end is the same burn the other way round. For the stock system this gives
Kerbin to the Mun as 856 + 310 m/s, and Kerbin to Duna as 1,072 + 616 m/s. That matches
the community delta-v maps **[meas]**.

## Engines that breathe air

KSP's delta-v simulation gives an engine that takes in `IntakeAir` no thrust in vacuum
(`ModuleEngines.MaxThrustOutputVac`), so a stage of jets has a vacuum delta-v of 0; it
ignores `IntakeAir` and `ElectricCharge` as propellants (`DeltaVGlobals.PropellantsToIgnore`)
and lets the jets drain the liquid fuel they reach, which leaves the rockets sharing those
tanks nothing. A RAPIER counts in its running mode, air-breathing by default
(`MultiModeEngine.runningPrimary`) **[src]**.

Where an engine breathes air (a propellant `IntakeAir` or what the vessel's
`ModuleResourceIntake`s gather), `EditorReadout.SimOf` reads the parts for `StageSim`,
and `AirLaunch.Plan` takes its stages instead of KSP's:

* `StageSim.Run` follows `tools/craft_stages.py`: a stage drops what it decouples, lights
  the rocket engines activated by then that reach every propellant, and burns until they
  are dry or the engines the next stage drops are. A resource comes from its own part
  (`NO_FLOW`), the parts reached through crossfeed and fuel lines (`STACK_PRIORITY_SEARCH`,
  `STAGE_STACK_FLOW`, `STAGE_STACK_FLOW_BALANCE`) or any part, from those dropped soonest,
  evenly; worked out from one tank running dry to the next. Only rocket modes burn there:
  a RAPIER's closed cycle; of another part, the first engine, as KSP. A locked tank's
  resources are dead weight. `StockCraftTests` checks it against the offline model for
  every stock craft without air-breathing engines, within 0.2 % of the masses and 1 % of
  the delta-v, which the model steps in 0.05 s.
* Where the route starts on a surface of a body with oxygen (`PlannerBody.Oxygen`), the
  air-breathing engines lit first, with the first stage that lights any engine, burn
  before any stage, as the row *Air*, numbered one above the highest stage
  (`StageRow.Air`, `BudgetStage.FirstStepOnly`: what it has not burnt after the first
  step counts no more). Their share of the launch is `RoutePlanner.Launch` times the
  speed they reach over the orbital speed of the low orbit less the ground's own,
  at most all of it (`AirLaunch.Share`). The speed is the Mach number where the thrust
  over Mach (`velCurve`) falls to half its peak past the peak, or where the curve ends,
  Mach 1 at most for an
  engine whose peak lies below Mach 1.5 (`AirLaunch.MachLimit`), times the speed of sound
  where the pressure is 5 % of the sea level's (`PlannerBody.SpeedOfSound`, from
  `GetPressure`, `GetTemperature`, `GetDensity`, `GetSpeedOfSound`). On the keys joined by
  straight lines that gives about Mach 4.5 for the Whiplash, 5.2 for the RAPIER, 2.8 for
  the Panther in wet mode. The propellant
  for it, `m (1 - e^(-Δv / v_e))` at the engines' standing thrust and Isp at the body's
  sea level, is drawn from the tanks they reach as the stages draw theirs.
* A launch whose first stage is *Air*, on a vessel with wings (`PartFacts.Lift`: a
  `ModuleLiftingSurface` on no command part or heat shield, which carry one for their
  body's lift) and landing gear (`Wheels`: a `ModuleWheelBase` that is no leg), needs no
  TWR: it lifts off along a runway (`VesselShape.Runway`). The stages after it light in
  the air, so `ClampedLaunch` leaves them apart.
* `AirLaunch.Plan` runs only when the readout or the launch body changes.

`StockCraftTests` flies the stock spaceplanes: the Dove (a RAPIER) and the Aeris 4A (two
Whiplashes and an aerospike) reach a low Kerbin orbit, as their descriptions say; the
Ravenspear Mk3, jets only, does not.

## How the stages pay for the steps

Before the budget, where the route starts on a surface, `ClampedLaunch.Parallel` rewrites
the stages lit while launch clamps hold the vessel. From an orbit nothing is held, so a weak
first stage there, an ion engine before a Spark, burns on its own. The clamps release at
their `ActivatesAt`; without clamps, a first stage with engines whose thrust in the launch
body's air (`StageRow.ThrustAtSurface`, the vacuum thrust where the body has none) cannot
lift its start mass at the surface is held until the next stage with engines; every stage
with delta-v and a higher number (the core) burns together with the releasing stage (the
boosters). At the summed thrust and flow (flow = vacuum thrust over exhaust speed, the
exhaust speed from the stage's delta-v and masses), both burn from the full mass until the
first is empty: the lift-off row, numbered as the last held stage. What is left burns on
alone in a second row, numbered as the releasing stage, so that the order by number stays
the burn order: the core after the boosters' mass is dropped where its engines decouple
later than theirs, else the boosters. `PlannerView.StageLabel` names them *S4+3* and *S4* or
*S3*. The *Air* row takes no part. It applies only where nothing is dropped on the pad from
the first held stage to the release, and where the core's tanks are dry as KSP starts the
releasing stage; stages on the pad between them that burn nothing go. `VesselShape.Split`
still shares the rows out by KSP's stage numbers, which is exact while the core and the
boosters belong to the same group, as launch stages do; a stage kept or dropped is saved by
the row's number.

`StageBudget.Allocate` takes each stage from KSP's delta-v simulation: its start mass,
its end mass and its delta-v in vacuum (`DeltaVStageInfo.startMass`, `endMass`,
`deltaVinVac`) **[src]**. From these it works out:

| Value | From |
|---|---|
| propellant | start mass less end mass |
| exhaust velocity | delta-v ÷ ln(start mass ÷ end mass), which is Tsiolkovsky's equation solved for it |
| mass dropped before the next stage | start mass less propellant less the next stage's start mass |

A stage whose engines all burn out once lit (`ModuleEngines.throttleLocked` or
`!allowShutdown`, `StageRow.Solid`) gives all it has to the step it starts on.

It then walks the steps one by one and carries the vessel's mass along. For each burn,
the propellant a stage spends is the mass it pushes × (1 − e^(−Δv ÷ exhaust velocity)).
A stage burns until its propellant is gone. What it has left after one leg goes on into
the next. When a stage is empty, the next one fires, and the mass KSP drops with it
goes. If a route burns every stage empty, the delta-v the stages give adds up to KSP's
own figure. Tests check this, with and without a lander that never flies alone.

Stages burn in KSP's order, the highest number first. That is how KSP stages, and it is
also how it decides which tank feeds which engine. The planner cannot move a tank to
another stage's engines. The staging and the fuel lines set that.

### The parts of a stage

`EditorReadout.BurningParts` lists, per stage, the parts that burn in it. These are the
engines active in the stage (`DeltaVStageInfo.enginesActiveInStage`). They are also the
parts that hold less propellant at the stage's end than at its start. KSP's simulation
keeps that per part and stage in `DeltaVPartInfo.stageFuelMass` **[src]**. `PartHighlight`
colours them with KSP's own part highlighter, always on in a colour per part, and back
to its default when the colour goes (`Part.SetHighlightType`, `SetHighlightColor`,
`SetHighlightDefault`) **[src]**.

### Saving and dropping a stage

The player sets per step, by stage number (`StagePlan`, kept on the stop the step leads
to, by role):

* *Save* (*Use* off): the step stops before the stage, and so before every stage after it,
  the lander's included. KSP can only fire stages in order. What the step then lacks is
  `Short`.
* *Drop after*: at the step's end, after a refill, the stage goes with what it holds,
  and so does every stage before it on what flies the step, in KSP's order. The result
  lists each as `Dropped`, in delta-v at the mass the stage pushes then, with the step in
  `GoneAfter`. The last stage of what flies the step cannot be dropped
  (`StepStage.CanDrop`).

Per step, `Result.Stages` lists the stages of what flies it that hold propellant as it
starts. Each has what it gives to the step, and what it still gives after:
burning on in order from there, as at the route's end, or what it held where it was
dropped.

### The rest and the sections

The vessel falls into groups (`VesselSplit`): group 0 the rest, and one per section, less
the sections inside it, each part in the smallest section holding it. Each stage belongs
to the group whose parts it fires, its engines first; its propellant and the mass KSP
drops after it share out over the groups by the parts that burn and go there
(`VesselShape.Split`, from `DeltaVPartInfo.stageFuelMass` and `decoupleStage`). The
groups' masses are the parts' masses, scaled to KSP's start mass of the whole vessel,
launch clamps left out. So a lander's stages can lie between the rest's, as they do where
it hangs under a mothership by a docking port. What KSP's mass holds over the parts' is the
crew's, with their inventory (`ShipConstruct.GetShipMass` with
`ShipConstruction.ShipManifest`) **[src]**. It goes to the parts the crew sit in, by head
(`PartFacts.Aboard`), before the scaling.

The route hands each step to the budget as a `BudgetStep`: what it needs, the groups
that fly it (`Side`), the groups gone for good as it starts (`Gone`: left as a vessel of
its own, or dropped with what flew, or left where it waited), the group refilled after
it, and the groups deployed after it. A group gone for good shows where it last flew
to. What flies a step burns its own groups' stages in
KSP's order, and what KSP drops after a stage goes; a section alone pushes only its own
mass and that of the sections part of it. A deployed group stays with what it weighs
(`Payload`); where it flew the step itself, what the step leaves is what it holds as it
stays.

Where the vessel has no parts to go by, as in the unit tests, the lander and the capsule
are each a stage and every stage after it (`VesselSplit.ByStages`). What KSP drops as the
whole vessel starts the lander's stages is then the rest itself: it stays while a step of
the lander alone is ahead, because the lander returns to it (`RestStaysForLander`).

### Finding the sections

`EditorReadout.PartFacts` gives each part its parent in the part tree, its
`DeltaVPartInfo.decoupleStage`, the stage it fires in (`Part.inverseStage`), its masses,
its seats for crew (`Part.CrewCapacity`), and whether it has a `ModuleCommand`, a
`ModuleEngines`, a landing leg (`ModuleWheelBase` of `WheelType.LEG`), a parachute, a
heat shield, or separates the vessel: `ModuleDockingNode`, `ModuleAnchoredDecoupler` or
`ModuleDecouple` **[src]**.

`VesselShape.Candidates` cuts the part tree at each of those parts, into the side under
it and the side over it; a decoupler stays with the side over it, a radial decoupler and
a docking port go with the side under them. A separator with nothing on one side, such
as a docking port on a pod's nose, cuts nothing; of two docking ports joined in the
editor only the lower one cuts, as that one undocks. Each side loses its launch clamps
(`LaunchClamp`, which KSP's simulation leaves out as well) and the parts KSP stages away
before its command parts, legs and parachutes: those with a higher `decoupleStage`,
unless its own engines still carry them when they fire (`Part.inverseStage`), such as a
lander's drop tanks. Of what is left, only the parts still joined in the part tree to
its heaviest command part stay. So a lander under a mothership is the lander without
the transfer stage under it. Sides with the same parts are one choice, a docking port
among their separators whatever the order, named after its command part, else its
engine. The plan keeps a choice as its separator's `persistentId` and side.

The planner shows a section of its own where the plan has none of that kind, the player
has not removed the last of that kind (`PlannerState.Dismissed`, cleared for a new craft),
and the list has room (`PlannerView.Found`). It is the plan's only once the player sets
something on it (`PlannerState.Keep`), under a fixed id (`FoundCapsule`, `FoundSatellite`,
`FoundLander`), so that a readout of the vessel before a craft loaded writes nothing:

* On a crewed vessel (a command part with seats), the capsule: the lightest choice with
  a crewed command part and parachutes, else the lightest crewed one
  (`VesselShape.AutoCapsule`).
* On an uncrewed vessel, the satellite: of the choices with a command part and no legs
  that leave the launch stage's engines behind, the one inside no other, where there is
  exactly one (`VesselShape.AutoSatellite`).
* The lander: the lightest choice with a command part, an engine and legs whose engines do
  not fire with the launch stage (`PartFacts.ActivatesAt`, as for the satellite), so that
  a booster with legs that lands by itself is none; inside the capsule and the satellite
  found or apart from them (`VesselShape.AutoLander`).

A found section gives way to the sections the plan has: it is not shown where it overlaps
one. A second lander is the player's. The rows show capsules first, then the other
sections as the player added them, what the planner found first of each. A satellite's
*Deployed at* is by itself the first orbit the route reaches before it lands or ends. A
section overlapping one before it counts for nothing; the lists offer only parts that fit
the other sections, a lander's only with engines. A list closes when the vessel's groups
change under it, as with undo. What was found goes to `KSP.log` once per change, in a line
that starts with `Planner found from`.

After a step marked for it, a section that docks is refilled (`Refuelled`): each of its
stages still there up to the propellant it held at the start, taken from the stage the
player names first (`RouteStop.RefuelFrom`, `BudgetStep.RefuelFrom`; the list offers the
rest's stages that hold fuel then, `Result.Tankers`), then from the rest's stages in KSP's
order. The mass moves from the rest to it, whatever the propellant.

## Checking the planner against the stock crafts

`tools/craft_stages.py` works out the stages of the stock crafts as KSP's delta-v
simulation gives them, without the game. It reads the craft files (`Ships/VAB/*.craft`:
each part's `istg`, links, fuel lines and resources) and the parts as the game loads them
(`GameData/ModuleManager.ConfigCache`, and `Squad/zDeprecated` for parts old crafts still
use). A part drops when the first decoupler between it and the root fires. Liquid fuel
flows through crossfeeding parts and fuel lines, the tanks dropped soonest first; solid
fuel stays in its part. A stage ends when its engines are dry, or when the engines the
next stage drops are. Air-breathing engines are left out, as KSP leaves them out in
vacuum; a RAPIER counts in its air-breathing mode, so not at all. For the Kerbal X and the
Learstar A1 its figures match the game's within 0.2 % **[meas]**. It also writes each
part as `StageSim` takes it, with its rocket and air-breathing modes, from the VAB or the
SPH.

```
python tools/craft_stages.py "<KSP folder>" tests/ReDefinition.Tests/StockCrafts.cs "Kerbal X" "Learstar A1" ...
```

`StockCraftTests` flies each craft through the planner's model as `PlannerView.Evaluate`
does, with the sections found by themselves and a 10 % margin, and checks it against its
description: the orbital crafts reach a low Kerbin orbit and lift off at a TWR of 1.2 or
more, the trainers do not, the AeroEquus reaches the Mun, the PT Munsplorer lands on the
Mun and on Minmus and comes home, and the Kerbal X does so only without a margin. The
flights are written to the test log.

## Where the plans are kept

`PlannerState` keeps one plan per vessel name in
`GameData/ReDefinition/PluginData/planner.cfg`, at most 200 plans:

```
PLAN
{
    vessel = Mun Lander
    margin = 0.10
    planeChanges = True
    nextSection = 3
    SECTION
    {
        id = 1
        kind = Capsule
        group = Automatic
        deployAt = -1
    }
    SECTION
    {
        id = 2
        kind = Lander
        group = 1822461803:under
        deployAt = -1
    }
    STOP
    {
        body = Kerbin
        kind = Surface
        altitude = 0
        useAtmosphere = False
        by = Auto, Auto, Auto, Section 2
        refuel = False, False, False, False
        refuelFrom = -1, -1, -1, -1
        orbitFirst = False
        splitBurns = False
        STAGE
        {
            step = Launch
            stage = 3
            choice = DropAfter
        }
    }
}
```

A `SECTION`'s `group` is a part choice's key, `Automatic`, or empty where none is picked;
`deployAt` a stop, -1 for the planner's.
`dismissed` lists the kinds the planner added and the player removed. `by` is `Auto`,
`Ship`, `Section` or `Without` with a section's id; `refuelFrom` a stage, -1 for KSP's
order; `by`, `refuel` and `refuelFrom` are per
step in the order launch, transfer, arrival, landing. A `STAGE` is a stage saved on a step
(`Save`) or dropped after it (`DropAfter`); a stage left to burn has none.

Bodies are kept by the name the game knows them by (`CelestialBody.bodyName`), which
does not change with the language. If a body of the plan is not in the loaded system,
its stop is left out, and the stages chosen on the stops after it go too, because
their steps are not the same. A stop that is removed takes the stages chosen on the steps
into the stop after it along (`PlannerState.RemoveStop`). The vessel's
name is kept escaped (`Uri.EscapeDataString`), because a config file reads `//` as a
comment. If there are 200 plans, the oldest goes.

A change is written 1.5 seconds after the last one, when the planner closes, and when
the editor closes. When KSP loads a vessel or starts a new one (`GameEvents.onEditorLoad`,
`onEditorRestart`) **[src]**, the planner loads that vessel's plan, or the default. A
craft merged into the vessel changes nothing. If the vessel is renamed, its plan goes
on under the new name.

The plan keeps a stage's choice or the lander even while the vessel does not have that
stage, for
example while the player takes the vessel apart. Such a setting counts for
nothing until the stage is back.
