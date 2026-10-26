# Menu

What ReDefinition adds to the main menu scene: fireworks on Kerbin's night side, and the camera
flight from the menu down to the space centre.

## Start here

| File | Read it for |
|---|---|
| `src/Menu/MenuFireworks.cs` | The fireworks: finding the menu's Kerbin and sun, the pool of bursts, and the failure path. |
| `src/Menu/KscArrival.cs` | The way in: `KscArrival.Install`, the menu half `KscArrivalMenu` and the space centre half `KscArrivalDescent`. |

## What it does

- `MenuFireworks` is a `[KSPAddon(KSPAddon.Startup.MainMenu, false)]`. It shoots bursts from points
  of the sphere where the sun is below the horizon, in a pool of particle systems.
- `KscArrival` holds the shared state and a prefix on `HighLogic.LoadScene`. The prefix holds
  back the menu's load of the space centre until the camera has flown in.
- `KscArrivalMenu` flies the menu's camera in and leaves the last picture on screen over KSP's
  loading. `KscArrivalOverlay` shows that picture.
- `KscArrivalDescent` fades the picture and brings the camera down to KSP's own view in the space
  centre.
- `MenuSun` turns every directional light of the menu, so that the menu's sun stands over the space
  centre as the save's sun does.
- `KscArrivalCheck` and the `-redefinitionKscArrivalTest` switch run the way in by itself with
  screenshots, for tests.

The area is a stop on [The features on screen](../../docs/development/flow-game-ui.md) and on
[Startup and scenes](../../docs/development/flow-startup.md).

## What it uses and what uses it

It uses Core, Modules, Probe and Upscaler. No other area names it; KSP starts its add-ons.
`OwnSettings.MenuFireworks` and `OwnSettings.MenuKscZoom` switch the two features.

## Rules of the area

- Both features read their switch through `ReDefinitionAddon.Live` and are on by default
  (`MenuFireworks.Enabled`, `KscArrival.Enabled`).
- Anything that fails ends the fireworks with one line in `KSP.log` and leaves the menu as KSP built
  it (`MenuFireworks.Fail`).
- If the hook on `HighLogic.LoadScene` cannot be installed, the way from the menu to the space
  centre stays KSP's own (`KscArrival.Install`).
- The menu's sun is the light `MenuFireworks.Rank` ranks highest: `PlanetLight` or
  "Directional light", never the back light (`src/Menu/MenuFireworks.cs`).
- `MenuSun.Turn` turns each light from where it stood before the last turn, because Parallax's main
  menu sets its lights' directions in every `Update` (`src/Menu/KscArrival.cs`).
- If the descent is left early, `KscArrivalDescent.OnDestroy` puts KSP's interface back and closes
  the picture, which would otherwise outlive the scene.
