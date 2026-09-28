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
| `src/Engineer/Planner/StageBudget.cs` | which stage pays for which step, on the mass the stages push, and the lander refilled from the rest. Free of Unity |
| `src/Engineer/Planner/PlannerBodies.cs` | the bodies of the loaded system, read from `FlightGlobals.Bodies` |
| `src/Engineer/Planner/PlannerState.cs` | one vessel's plan, and its file |
| `src/Engineer/Planner/PlannerView.cs` | the plan: loading, saving, working it out, the choices the rows make, the colours on the vessel |
| `src/Engineer/Planner/PlannerView.Rows.cs` | the planner's rows, built from KSP's dialog elements |
| `src/Engineer/Planner/PlannerWidgets.cs` | the symbols, the coloured bars, the pin marks, the stage colours, the list of bodies and the list that folds out under a button (`Dropdown`) |
| `src/Engineer/Planner/PartHighlight.cs` | colours parts on the vessel with KSP's part highlighter |
| `src/Engineer/PlannerPanel.cs` | the panel the rows stand in, made as KSP's crew panel is |

## How the planner is drawn

The planner uses no text to explain itself. From the top:

* Every choice carries a tooltip of one sentence. KSP's button prefab has no tooltip
  controller, so `DialogTooltips.Attach` adds the game's own to every element with a
  tooltip once the rows are created.
* The route is a line of stops, an orbit's altitude under it as a field
  (`DialogGUITextInput`); while it has the keyboard, the game's controls are locked
  (`PlannerView.Keep`), so that the digits do not reach the editor's shortcuts. Between
  two stops, each step in a block on a lighter background (`Tint`, from the timeline on),
  with a gap to the next, and in it one row in fixed columns: where it goes; who flies it,
  a button under which the list of who can folds out (`Dropdown`), where the vessel has a
  lander or a capsule, the player's own choice bold and underlined; its bar, a row of
  `DialogGUIImage`s whose widths and colours `SegmentBar` sets after they are created; its
  delta-v; and *...* for its further choices. Around it only where there is something: a
  grey line where something leaves at its start, *Docks with the rest* with
  *Refuel* where it docks at its end, the further choices in groups of their own (*Way
  down*, *Payload*), and a line for each problem that blocks it
  (`PlannerView.StepWarnings`), up to three, in red, shown only while there is one.
* One bar sums up the route, with the margin and the plane changes under it.
* The fuel plan is a table of stages by legs. Its columns share the room the stage and
  left columns leave, over the legs there are (`Sizer`), so the table fills the panel's width;
  a column with room shows the whole figure. A cell is a button of KSP's skin with the delta-v
  the stage gives to the leg, in the stage's colour. A pin is a symbol in the cell's corner
  (`IconMark`).
* The details of the selected leg or stage close the panel, one line to a row of a fixed
  height. A leg's details list each of its steps with its burns and the stages that pay
  for it, each with its TWR: the thrust in vacuum over the weight it pushes, at the
  surface gravity of the body the step starts from where it lifts off, of the one it
  goes to otherwise. A label of several lines whose text changes is not given its
  height by KSP's layout, and ran over the rows below it.

Choices are buttons of KSP's skin whose text is lit green while their option is chosen.
The skin's pressed state of a toggle button reads the other way round, so it is not
used. Symbols are `GameData/ReDefinition/Icons/Planner_*.png`, white on clear, drawn by
`tools/make_icon.py`. The body list is a small `PopupDialog` in the middle of the screen
(`BodyPicker`), shielded from the editor's clicks. KSP places a dialog elsewhere through
a formula that fails at a UI scale of 200 % (`PopupDialog.OnRectTransformDimensionsChange`)
**[src]**.

While the planner is open, every stage's engines and tanks show in the stage's colour on the
vessel, pushed to full saturation and brightness so it reads through KSP's soft highlight
(`PlannerColors.Vivid`). A selected leg leaves only the stages that pay for it, a selected
stage only that one.

KSP's layouts take no width of their own: `DialogGUILayoutBase` gives a row the width of
its elements, not the one it is handed **[src]**. So every row's elements are summed to
the room the scroll view leaves: it insets its content 4 units at each side, and its bar,
16 wide, stands 3 units in from the right edge (the prefab, read with UnityPy) **[src]**.
`PlannerWidgets.RowRoom` gives that room. The list reaches out from the crew panel's
content area to 2 units short of the panel's right edge, so that its bar sits there as the
parts list's does; in the 488 units that gives it is 465. A button hidden from a row
leaves a space of its width, so that the crosses of all stops line upin one column. While
any panel slides, every mode button is locked, since the planner's transitions pass KSP's
own lock by, and a mode chosen during one left the planner standing.

The scroll view's own background is cleared (`PlannerWidgets.Settle`), so the panel shows one
background, as the crew panel does. The planner takes itself out when KSP brings in another
panel, since KSP's cargo mode, which brings in the parts list and the cargo panel together,
left it standing.

The rows are laid out all at once, and the ones the plan does not need hide after. The scroll
list's content is therefore held at its top edge, so that it shrinks from the bottom. The
planner has room for 11 stops, so ten legs side by side in the fuel plan within the crew
panel's width, three steps per leg, and 30 stages. It is worked out again four times a second
while it is open. The free-of-Unity parts have unit tests in
`tests/ReDefinition.Tests/PlannerTests.cs`.

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
body ends in its air (`StopKind.Entry`), unless the stop has `OrbitFirst`.

Each step is flown by a `Vehicle`: `Ship`, the whole vessel; `Lander`, the lander alone;
`Rest`, the rest without the lander; or `Capsule`, the capsule alone. The capsule is the
stages from the one the player marks with *C*, which may be one without delta-v (the fuel
plan lists those after the last stage with delta-v); it leaves whatever flew the step
before for good, and a step of anything else after it is a `Problem`. The player sets it
per step (`RouteStop.By`, by role, on the stop the step leads to), or `RouteSteps.Build`
works it out: the capsule once it has left; the capsule for a landing on parachutes on the
last stop where what carries it is in the air; a landing short of the last stop the
lander; from a surface whoever stands on it; across to another body `Ship` where the
lander is in an orbit of the body the rest waits at; otherwise whoever flew the step
before. `Build` walks the steps and keeps where the rest and the lander are:

| Step flown by | While | Then |
|---|---|---|
| `Ship` | the lander is with the rest | the whole vessel flies (`LegMode.Whole`) |
| `Ship` | the lander is away, where the rest waits or in another orbit of that body | the lander's last step `Docks`, with the burn to the rest's orbit where it ended in another one, 50 m/s for the rendezvous where the rest is in orbit, and is refilled where its stop's `Refuel` is set for that step |
| `Ship` | the lander is away, elsewhere | a `Problem`: the lander is not where the rest waits |
| `Lander` | the lander is with the rest | it `Separates`, and the rest waits where it is |
| `Lander` | the lander is away | it flies on from where it is; a rest never met again stays where it waited |
| `Rest` | | the lander stays where it is for good (`LegMode.WithoutLander`); a later step of the lander is a `Problem` and costs nothing |

A lander that climbs from a surface while the rest waits in an orbit of that body climbs
to the rest's own orbit. A stop's `LeavesPayload` marks its last step `PayloadAfter`. A
step of the whole vessel or the rest that starts in the air and is no landing is a
`Problem`. Without a lander, every step is the whole vessel's.

The fuel plan's columns stay the legs between two stops; a pin on a leg is set on its
steps (`PlannerView.StepPins`): a stage of the lander on the lander's steps, a stage of the
rest on the others, and on a leg without a step of the lander on all of them.

The five missions of `PlannerTests` (a vessel to Minmus and home; a satellite left in a
synchronous orbit; a Duna lander flying home by itself; a lander hopping on Minmus from a
Mun base; a Jool mission with two landings, a refill and a capsule landing at home) are
set up as the player sets them, and check the steps that come out.

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

## How the stages pay for the steps

`StageBudget.Allocate` takes each stage from KSP's delta-v simulation: its start mass,
its end mass and its delta-v in vacuum (`DeltaVStageInfo.startMass`, `endMass`,
`deltaVinVac`) **[src]**. From these it works out:

| Value | From |
|---|---|
| propellant | start mass less end mass |
| exhaust velocity | delta-v ÷ ln(start mass ÷ end mass), which is Tsiolkovsky's equation solved for it |
| mass dropped before the next stage | start mass less propellant less the next stage's start mass |

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

### Pins

The player can pin a stage to one or more legs:

* A leg with pins burns its pinned stages only, in KSP's order. If they are not enough,
  the rest of the leg is short.
* When a pinned stage fires, the stages before it in its group are dropped with what
  they still hold. The result lists that as `Dropped`, in delta-v at the mass the stage
  pushes at that moment.
* A leg without pins burns in KSP's order and stops before a stage pinned to a later
  leg, and so before every stage after it, the lander's included. KSP can only fire
  stages in order.
* A pinned stage that is already dropped or burnt out when its leg comes goes into
  `Unreachable`, as stage and leg.
* While the lander flies alone, the rest waits and drops nothing. A pin of the rest's
  stage on such a leg is unreachable too.
* A leg of the whole vessel with a pinned stage of the lander stages the rest away
  first, with what it holds, as KSP's order has it. If the lander still flies alone
  later, the rest stays attached instead: the lander docks to it again.
* A leg stops at the first pins that pay for it. The pinned stages after them keep
  their fuel.

### A lander

The player can name the lander's highest stage. That stage and every stage after it
form the lander, and the stages before it form the rest. The player marks the lander's
first stage with delta-v. The stages without delta-v fired just before it count to the
lander (`PlannerView.LanderTop`), since the decoupler's stage is among them. The
lander's mass is its top stage's start mass: what KSP's staging leaves once the stages
before it are gone. So the lander's decoupler or docking port has to be in one of those
stages. If it is not, the lander's mass includes the rest, and the planner
underestimates the lander. The last
stage of the rest is not dropped when it runs empty, because the lander returns to it.
It goes when the whole vessel starts to burn the lander's stages and no leg of the
lander alone is ahead. KSP's own simulation drops it at that point.

Each step is flown in one of three ways (`LegMode`):

| Mode | Stages that burn | Mass they push |
|---|---|---|
| `Whole` | the rest first, then the lander's, unless a pinned stage of the rest holds them back | everything still attached |
| `Lander` | the lander's | the lander alone |
| `WithoutLander` | the rest's | the rest alone. The lander stays behind with what it holds, and no later step has it |

The first step of the capsule (`LegMode.Capsule`) leaves every other stage with what it
holds (`Dropped`); the capsule then pushes its own start mass, and only its stages burn.

After a step marked for it, a payload stays behind (`Payload`): what KSP drops when the
next stage without delta-v after the burning one fires, `drop` of the stage before it. The
burning stage burns on with what it holds, now lighter. KSP's simulation burns a stage
until the tanks that separate first are empty (`DeltaVStageInfo`), so an upper stage that
fires before the satellite's decoupler carries all its fuel in its own stage, with the
satellite on; the planner takes the satellite off at the stop instead.

After a step marked for it, the lander is refilled (`Refuelled`): each of its stages
still attached up to the propellant it held at the start, taken from the rest's stages
in KSP's order, the one burning first. The mass moves from the rest to the lander.

## Where the plans are kept

`PlannerState` keeps one plan per vessel name in
`GameData/ReDefinition/PluginData/planner.cfg`, at most 200 plans:

```
PLAN
{
    vessel = Mun Lander
    margin = 0.10
    planeChanges = False
    lander = 1
    capsule = 0
    STOP
    {
        body = Kerbin
        kind = Surface
        altitude = 0
        useAtmosphere = False
        by = Auto, Auto, Lander
        refuel = False, False, False
        orbitFirst = False
        leavesPayload = False
    }
    PIN
    {
        stage = 3
        leg = 2
    }
}
```

Bodies are kept by the name the game knows them by (`CelestialBody.bodyName`), which
does not change with the language. If a body of the plan is not in the loaded system,
its stop is left out. The pins then go too, because the legs are not the same. A stop
that is removed takes the pins of the legs into it and out of it along. The vessel's
name is kept escaped (`Uri.EscapeDataString`), because a config file reads `//` as a
comment. If there are 200 plans, the oldest goes.

A change is written 1.5 seconds after the last one, when the planner closes, and when
the editor closes. When KSP loads a vessel or starts a new one (`GameEvents.onEditorLoad`,
`onEditorRestart`) **[src]**, the planner loads that vessel's plan, or the default. A
craft merged into the vessel changes nothing. If the vessel is renamed, its plan goes
on under the new name.

The plan keeps a pin or the lander even while the vessel does not have that stage, for
example while the player takes the vessel apart. Such a setting counts for
nothing until the stage is back.

## Where the approach comes from

Two online planners were read for what a mission planner offers **[doc]**:

* The KSP Visual Calculator by Blaarkies (All Rights Reserved) plans a trip through
  checkpoints, with aerobraking and plane changes as choices, and shows the details of
  each step. The planner's route of stops follows this concept.
* The delta-v planner by Loïc Viennois (GPL-3.0) plans from one body to another on a
  fixed delta-v map.

Both work from a fixed map of the stock system. This planner works out the figures from
the bodies the game has loaded, and assigns them to the vessel's own stages.
