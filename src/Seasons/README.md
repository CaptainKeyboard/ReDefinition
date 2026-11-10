# Seasons

Seasons on Kerbin as a mod of its own, `ReDefinition.Seasons.dll` in `GameData/ReDefinition/Seasons`. It needs neither ReDefinition nor Harmony nor ZTheme. Parallax and EVE are optional ([docs/player/seasons.md](../../docs/player/seasons.md)).

## Start here

| File | Read it for |
|---|---|
| `src/Seasons/SeasonClock.cs` | The season phase from the universal time, the year length and the hemispheres. |
| `src/Seasons/SnowMask.cs` | The snow cover from latitude, height, phase and noise, and the design constants. |
| `src/Seasons/SeasonsSettings.cs` | The settings in `PluginData/settings.cfg` of the mod's own folder. |
| `src/Seasons/SeasonsStartup.cs` | The add-on that checks Common, reads the settings and logs one line. |

## What it does

- `SeasonClock.Phase` turns a time and a year length into a phase in [0, 1). `YearSeconds` scales Kerbin's orbit period by the year length factor. `HemispherePhase` gives the southern hemisphere half a year later. Phase 0 is the northern midwinter.
- `SnowMask.Coverage` is a pure function from latitude, height above sea level, phase and a noise value to a cover from 0 to 1. The ground, the orbit view and the storms are to read this one function, so that they agree.
- `SeasonClock.Current` reads Kerbin's orbit period from the game and gives the phase now.
- Nothing is drawn yet.

## What it uses and what uses it

It uses `Core` in `ReDefinition.Common.dll` and nothing else of ReDefinition. ReDefinition does not name it. `ArchitectureTests` and `tools/checks/check_no_seasons_reference.ps1` hold this.

## Rules of the area

- The seasons apply to Kerbin only (`SeasonClock.Applies`).
- The design numbers of the snow line are constants in `SnowMask` and nowhere else.
- No type of `ReDefinition.dll` or of Harmony is named here.
