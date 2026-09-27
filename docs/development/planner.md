# The planner

**For:** anyone changing the planner in the VAB and the SPH.
**You need:** the repository, and `src/Engineer/Planner` open.
**You get:** how the planner works out a route's delta-v, how it assigns the vessel's
stages to the legs, and where its plans are kept.

The player's side is in [player/planner.md](../player/planner.md). How the planner's
panel sits in KSP's editor is in [engineer.md](engineer.md).

## The parts of the planner

| File | Does |
|---|---|
| `src/Engineer/Planner/RoutePlanner.cs` | the delta-v of each leg of a route, and where a lander meets the rest of the vessel. Free of Unity |
| `src/Engineer/Planner/StageBudget.cs` | which stage pays for which leg, on the mass the stages push. Free of Unity |
| `src/Engineer/Planner/PlannerBodies.cs` | the bodies of the loaded system, read from `FlightGlobals.Bodies` |
| `src/Engineer/Planner/PlannerState.cs` | one vessel's plan, and its file |
| `src/Engineer/Planner/PlannerView.cs` | the plan: loading, saving, working it out, the choices the rows make, the colours on the vessel |
| `src/Engineer/Planner/PlannerView.Rows.cs` | the planner's rows, built from KSP's dialog elements |
| `src/Engineer/Planner/PlannerWidgets.cs` | the symbols, the coloured bars, the pin marks, the stage colours and the list of bodies |
| `src/Engineer/Planner/PartHighlight.cs` | colours parts on the vessel with KSP's part highlighter |
| `src/Engineer/PlannerPanel.cs` | the panel the rows stand in, over the parts panel |

## How the planner is drawn

The planner uses no text to explain itself. From the top:

* The route is a line of stops with a bar for each leg. The bar's length is the leg's
  delta-v, on one scale for all legs, and its colours are the stages that pay for it. A
  bar is a row of `DialogGUIImage`s whose widths and colours `SegmentBar` sets after they
  are created.
* One bar sums up the route, with the margin and the plane changes under it.
* The fuel plan is a table of stages by legs. A cell is a button of KSP's skin with the
  delta-v the stage gives to the leg, in the stage's colour. A pin is a symbol in the
  cell's corner (`IconMark`).
* The details of the selected leg or stage close the panel.

Choices are buttons of KSP's skin whose text is lit green while their option is chosen.
The skin's pressed state of a toggle button reads the other way round, so it is not
used. Symbols are `GameData/ReDefinition/Icons/Planner_*.png`, white on clear, drawn by
`tools/make_icon.py`. The body list is a small `PopupDialog` in the middle of the screen
(`BodyPicker`), shielded from the editor's clicks. KSP places a dialog elsewhere through
a formula that fails at a UI scale of 200 % (`PopupDialog.OnRectTransformDimensionsChange`)
**[src]**.

While the planner is open, every stage's engines and tanks show in the stage's colour on
the vessel. A selected leg leaves only the stages that pay for it, a selected stage only
that one.

The planner has room for 10 stops, so nine legs side by side in the fuel plan, and 30
stages. It is worked out again four times a second while it is open. The two
free-of-Unity parts have unit tests in `tests/ReDefinition.Tests/PlannerTests.cs`.

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

## How a leg's delta-v is worked out

A leg goes from one stop to the next. `RoutePlanner.LegOf` adds its burns:

| Burn | Worked out as |
|---|---|
| launch | 1.07 × the low orbit's circular speed, less the ground's rotation speed. On a body with air, plus √(g × atmosphere depth) × (1.33 + 0.0964 × p²), with p the sea-level pressure in atmospheres. Without air, plus 10 m/s |
| transfer and capture | a Hohmann transfer between circular orbits around the common parent of the two bodies, with patched conics at each sphere of influence |
| orbit change around one body | a Hohmann transfer between the two orbits. When aerobraking down to a lower orbit, the burn down to half the atmosphere's depth, and the aerobraking share below |
| landing | the launch without the atmosphere's share. With parachutes, 0 on air of 0.2 atm or more, and a share of 30 % of the orbital speed that falls to 0 at 0.2 atm |
| aerobraking | 3 % of the circular speed at the arrival orbit, for raising the periapsis out of the air |
| plane change | 2 × v × sin(Δi / 2), with v the slower of the transfer's two ends. Δi is the tilt between the two orbits around the common parent. Between a body and its own moon, it is the moon's tilt |

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

## How the stages pay for the legs

`StageBudget.Allocate` takes each stage from KSP's delta-v simulation: its start mass,
its end mass and its delta-v in vacuum (`DeltaVStageInfo.startMass`, `endMass`,
`deltaVinVac`) **[src]**. From these it works out:

| Value | From |
|---|---|
| propellant | start mass less end mass |
| exhaust velocity | delta-v ÷ ln(start mass ÷ end mass), which is Tsiolkovsky's equation solved for it |
| mass dropped before the next stage | start mass less propellant less the next stage's start mass |

It then walks the route leg by leg and carries the vessel's mass along. For each burn,
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

Each leg is flown in one of three ways (`LegMode`):

| Mode | Stages that burn | Mass they push |
|---|---|---|
| `Whole` | the rest first, then the lander's, unless a pinned stage of the rest holds them back | everything still attached |
| `Lander` | the lander's | the lander alone |
| `WithoutLander` | the rest's | the rest alone. The lander stays behind with what it holds, and no later leg has it |

`RoutePlanner.Meetings` checks that the route fits the modes. The rest waits at the
stop where the lander left it. The next leg the rest flies has to start at that stop.
It counts as the same stop if it is the same body and kind, and an orbit's altitude is
within 10 % or 1 km.

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
    STOP
    {
        body = Kerbin
        kind = Surface
        altitude = 0
        useAtmosphere = False
    }
    PIN
    {
        stage = 3
        leg = 2
    }
    MODE
    {
        leg = 2
        mode = Lander
    }
}
```

Bodies are kept by the name the game knows them by (`CelestialBody.bodyName`), which
does not change with the language. If a body of the plan is not in the loaded system,
its stop is left out. The pins and the modes then go too, because the legs are not the
same. A stop that is removed takes the pins and the mode of the legs into it and out of
it along. The vessel's name is kept escaped (`Uri.EscapeDataString`), because a
config file reads `//` as a comment. If there are 200 plans, the oldest goes.

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
