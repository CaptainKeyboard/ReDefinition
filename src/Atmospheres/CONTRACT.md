# Contract of ReDefinition Atmospheres

The interfaces every package of the atmospheres is written against. The files named here are frozen: a change needs the orchestrator and is
batched at a wave boundary (at most one announced Common version bump per wave). Packages read these types and keys; they add their own files
and never edit a shared one.

## 1. The seam in Common (`src/Common/Weather/`, namespace `ReDefinition.Core`)

| Type | File | One line |
|---|---|---|
| `PrecipType` | `PrecipScale.cs` | What falls: None, Snow, Rain, Sleet, FreezingRain, Dust, Sand, Ash, Methane. |
| `PrecipBand` | `PrecipScale.cs` | None, Flurries, Light, Moderate, Heavy, Blizzard. |
| `PrecipScale` | `PrecipScale.cs` | `BandOf(level01, sustainedWindMs)` with the starts 0.04 / 0.20 / 0.40 / 0.60 / 0.80 (Blizzard needs a sustained wind of 15.6 m/s, else Heavy), `StartOf`, `DefaultName`, `LevelFromRate(rateMmPerH, bandRates)`. |
| `FieldReadiness` | `IWeatherField.cs` | Ready, CatchingUp, Seeding, Broken. |
| `WeatherMapKind` | `IWeatherField.cs` | Coverage, Type, Precipitation, Humidity, SnowCover, SurfaceTemperature, IceFraction, IceThickness, WindSpeed. |
| `IWeatherSource` | `IWeatherField.cs` | A provider with a name, priority, thread-safety flag, `TrySample` and `TryWindForecast`. |
| `WeatherSourceBase` | `IWeatherField.cs` | The base a provider derives from: `TryWindForecast` answers false unless overridden. |
| `IWeatherField` | `IWeatherField.cs` | `Sample`, `GetMap`, `FillWindow`, `Advance(budgetMs)`, `Version`, `DefinitionId`, `ThreadSafe`, `StepSeconds`, `Readiness`. |
| `IForecastableField` | `IWeatherField.cs` | Optional: `Clone()` and `RunForward(hours)` for the forecast's forward run. |
| `ICheckpointedField` | `IWeatherField.cs` | Optional: `SaveCheckpoint`, `LoadCheckpoint(stream, ut)`, `CheckpointIntervalSeconds`. |
| `ISurfaceForcing` | `IWeatherField.cs` | Optional: `AddPrecipitationDisc(...)`, a test forcing; a forced run never writes side files. |
| `WeatherSample` | `WeatherSample.cs` | A struct: Level01, Type with SnowFraction / SleetFraction / RainFraction, VisibilityM, WindWorld (east, north, up), GustFactor, TemperatureK, CloudCover01, OpticalDepthVertical, BodyIndex, Readiness, Approximate. |
| `WeatherMap` | `WeatherMap.cs` | An equirectangular map the field fills: Width, Height, Values, Bytes, Kind, Version, Readiness, Approximate, `Sample(lat, lon)`, `PackBytes`. |
| `WeatherWindow` | `WeatherMap.cs` | A local 3D cut-out: Nx, Ny, Nz, spacing, origin, CloudWater, Updraft, Temperature. |
| `NullField` | `WeatherHub.cs` | The field that has no weather; every sample is clear. |
| `WeatherHub` | `WeatherHub.cs` | `Register`, `Unregister`, `Sample` (the merge of section 4), `Field`, `Version`, `SourceCount`. |
| `WeatherView` | `WeatherView.cs` | The frame data at the camera, mutated in place by one writer. |
| `FlakeLayer` | `WeatherView.cs` | One of the three flake layers (width, share, size range, opacity). |
| `CloudMapsState` | `WeatherView.cs` | Static, thread-safe: `Version` and `LayerName` of the maps EVE reads; set by the cloud-map painter, copied into the view by the frame driver. |
| `WeatherHooks` | `WeatherHooks.cs` | `View`, `Caps`, `CloudOpticalDepth`, `Forecast`, `BodyTracks`, `AlertRaised`, `ForecastGeneration`. |
| `WeatherRenderCaps` | `WeatherHooks.cs` | Written by the host only: Upscaler, FrameGenerationOn, HostApiAvailable, Tier. |
| `UpscalerKind`, `QualityTier` | `WeatherHooks.cs` | The enums of the caps (`QualityTier`: Auto, Low, Medium, High, Ultra, Max). |
| `SunTransmission` | `SunTransmission.cs` | `From(opticalDepthVertical, sunElevationRad)` = exp(-tau x air mass), air mass clamped to 20. The one definition. |
| `WindNoise` | `WindNoise.cs` | `Gust(ut, bodyFixedPos, windDir, level01, seed)`, `Wave`, `Phase01`, `Depth(level)` (0.1 to 0.4) and the slew limits (visibility 25 % per s, flake direction 30 deg per s, wind 8 m/s per s) with `LimitVisibility`, `LimitFlakeDirection`, `LimitWind`. |
| `ForecastSite`, `ForecastStep`, `ForecastTrack`, `ForecastAlert`, `AlertKind` | `Forecast.cs` | The forecast data. `AlertKind`: Approaching, Arrived, Worsening, Clearing, LaunchWindow. |
| `WeatherSource`, `WeatherSourceKind` | `Forecast.cs` | The wind label of a report: Ours, Foreign(name), ExternalNowcast(name). |
| `ForecastReport` | `Forecast.cs` | Steps, tracks, alerts, the nearest system's ETA and distance, `Wind`, `Generation`, `Available`. |

`ForecastGeneration` (in `WeatherHooks`) rises when an input of the forecast changes. It is not the simulation's epoch boundary.

## 2. The platform in `ReDefinition.Atmospheres` (internal)

| Type | Folder | One line |
|---|---|---|
| `IAtmospheresModule`, `AtmospheresModuleAttribute(order)` | `Infrastructure/Registry` | `Init`, `OnBodyChanged(old, new)`, `Tick(dt)`, `Shutdown`; a class with the attribute is a module. |
| `ModuleRegistry`, `ModuleState` | `Infrastructure/Registry` | Reflection discovery inside the assembly, order by the attribute, each call in its own try/catch; `Init` retried 3 times (`RetryGate`), a module that fails 5 ticks in a row (`FailureStreak`) is switched off. |
| `ModuleRunner` | `Infrastructure/Registry` | The component that ticks the registry once per frame; `AtmospheresStartup` adds it. |
| `Tuning`, `TuningTable` | `Infrastructure/Registry` | `Tuning.Get(key, compiledDefault)`: the numbers of `ATMO_TUNING` nodes in `GameData/ReDefinition/Atmospheres/Tuning/*.cfg`, cached per frame, with an override layer a series variant sets (`SetOverride`, effective at the next frame). |
| `BodyWatcher`, `BodyHysteresis` | `Infrastructure/Registry` | The active body (the flying vessel's main body, else the camera's) is looked at twice a second; a change counts after 5 s, a scene load counts at once; the old body's intensity ramps to 0 over 1 s (`BodyWatcher.OldBodyIntensity`). The simulation host persists across scenes. |
| `AtmospheresStartup`, `AtmospheresLog`, `RetryGate`, `FailureStreak` | `Infrastructure/Core` | The existing start-up and safeguards, moved. |
| `IPostUpscaleCompositor`, `PostUpscaleDraw` | `Infrastructure/Contracts` | The route to the host's after-upscaling hooks; the bridge package implements it; the null object answers false. |
| `IOrbitOverlayLayer`, `OrbitOverlayLayers` | `Infrastructure/Contracts` | A layer drawn on the planet in the map view; implementations register, the orbit painter calls them in `Order`. |
| `IEveFieldSuppressor` | `Infrastructure/Contracts` | The cloud-map painter implements it; the precipitation renderer calls it. |
| `Services` | `Infrastructure/Contracts` | The slots (`PostUpscale`, `EveFieldSuppressor`) with null objects. |
| `IBodyProvider`, `Bodies`, `DefaultBodyProvider` | `Infrastructure/Contracts` | `Bodies.Get(CelestialBody)`, `Bodies.Active`; the default gives a generic climate; the body package assigns the real provider. |
| `BodyClimate`, `BodyStatus` | `Bodies/Schema` | Immutable: Name, Index, Radius, YearSeconds, DaySeconds, Status (Off(reason), CoverOnly, Full, Broken(reason)), Seed and the rule classes. |
| `SeasonRules`, `TemperatureRules`, `PrecipRules`, `PrecipTypeRule`, `CoverRules`, `IceRules`, `GeneratorRules`, `CloudRules`, `LookRules`, `SoundRules`, `WindRules`, `GameplayRules`, `AuroraRules`, `LightMediumRules`, `FrostRules`, `ForecastRules`, `Curve` | `Bodies/Schema` | The sub-nodes of `REDEFINITION_BODY` (SEASONS, TEMPERATURE, PRECIP with TYPE, COVER, ICE, GENERATOR, CLOUDS, LOOK, SOUND, WIND, GAMEPLAY, AURORA, LIGHT_MEDIUM, FROST, FORECAST). Each constructor's parameters default to the generic default. |
| `SeasonModel` (not part of A01) | `Bodies` | Created by B13 (it owns `Bodies/*.cs` with `SeasonClock` and `SeasonState`): the season data plus the pure `Phase(ut, model)`; A01 only delivers the data class `SeasonRules`. C25 (`BodyForcing`) reads the insolation phase through it; until B13 lands, `SeasonClock` is the phase. |
| `GeneratorField`, `GeneratorState`, `GeneratorMaps`, `GeneratorSurface`, `GeneratorModule` | `Field`, `Generator` | The weather field: `IWeatherField`, `ISurfaceForcing` and the forecast's system source; per-body immutable state; maps and surface maps on a worker; single-site surface layer; the module that installs the field and reads the terrain raster (`GeneratorRaster`) and the temperature curves (`ThermalProbe`, `ThermalFit`). |
| `GeneratorBodies` | `Generator` | The mapping from `BodyClimate`, `BodyFacts`, terrain raster and fit to the core's parameter set. |
| `ForecastModule` | `Forecast/Engine` | The `[AtmospheresModule(70)]` that owns a `ForecastService`, assigns `WeatherHooks.Forecast` and `BodyTracks` once the field is Ready (null before and while not Ready or Approximate), raises `AlertRaised` through `WeatherHooks.RaiseAlert`, and follows the setting `alertSites` (the pad and the active vessel) through `SetAutoWatched` every 2 s. Its static `TryPadSite` and `TryVesselSite` build the sites with the altitude rule above. |
| `ForecastService`, `IForecastEnvironment`, `ForecastContext` | `Forecast/Engine` | The request queue, the single worker (or main-thread chunks when the field is not thread-safe), the cache, the slow tick (at most every 2 s real time and 300 s game time), the generation check, the alert tick per watched site; `Watch`, `ForecastOf`, `BodyTracks`, `LastAlertTick`. The environment gives field, clock and the per-body context. |
| `AlertEvaluator`, `AlertTick`, `LaunchConditions` | `Forecast/Engine` | Pure alert logic on a report: Approaching, Arrived, Worsening, Clearing by site-level threshold crossings of the hourly steps, hysteresis of one clear hour, dedupe by site, kind and absolute step, summary of a warp jump from the stored steps (nothing beyond them), re-arm when UT goes backwards. `LaunchConditions` gives go / marginal / no-go and the best window in 24 h. Delivery (messages, warp stop, KAC) is D30; the warp stop reads the preview in `ForecastReport.Alerts`. |
| `ForecastWindProviders` | `Forecast/Engine` | The wind providers the forecast may ask (`Add`, `Remove`, `Snapshot`, `Version`). `WeatherHub` keeps its provider list private, so a package that registers a wind provider with the hub also adds it here (D31 for its wind bridges). |
| `IWeatherSystemSource` | `Forecast/Engine` | Optional on a field: lists its weather systems (key, place, radius, strength) at a time so that the tracks come from the field's own cells; `GeneratorField` implements it, any other field gets the generic grid search. |
| `AtmospheresSettings`, `SettingDef`, `SettingsTable` | `Settings` | The settings (section 6): `Keys`, `Categories`, `GreyReasons`, `ProfileRows`, typed readers, `SetNumber` / `SetText`, `Definitions`. |

## 3. Rules

- The single writer of `WeatherView` is the frame driver (package C20), once per frame in `LateUpdate`, before `Camera.onPreCull`. The view is sealed and changed in place; `Version` rises when a value moved by more than `WeatherView.VersionEpsilon`.
- Readers are on the main thread, from `Camera.onPreCull` or later in the frame; a read in `Update` sees the previous frame. A reader treats `IsStale(frame)` (not Active, or `FrameStamp` older than two frames) as "no weather".
- A worker thread never reads the view. It copies a `WeatherSample` (a plain struct) from a field or the hub that says `ThreadSafe`.
- The host (the main mod) writes only `WeatherHooks.Caps` and `WeatherHooks.CloudOpticalDepth`. The atmospheres write everything else.
- No public member of the seam returns or holds a `UnityEngine.Object` (a test checks it). Visibility is in metres everywhere.
- `WeatherView.SunTransmission` is the transmission along the current sun ray at the camera; the frame driver writes it from `SunTransmission.From`. Raytracing, solar panels and the sky read only it.
- Every consumer that writes a wind speed (D31 forces, the aero hook) passes it through `WindNoise.LimitWind`: `Gust` alone changes faster than `MaxWindSlewMsPerS` for a mean wind over about 22 m/s and for a moving observer. A consumer that slews a wind VECTOR (D31 `WindCompose`) takes its rate from `WindMath.EffectiveSlew`, which never exceeds `MaxWindSlewMsPerS`; the speed then changes by at most the vector's step, so the vector slew stays under the `LimitWind` limit.
- Providers start from `WeatherSample.Clear(bodyIndex)`: a `default(WeatherSample)` has `VisibilityM` 0 and the merge would take the visibility of the whole sky to 0. Known: `WeatherView.Clear()` resets the flags and levels but leaves `FogColour`, the veil colours, `FlakeLayers`, `FlakeSeed`, `CloudMapsVersion` and `FrameStamp`; readers check `IsStale` first.
- `PrecipRateToLevel` (C20) is `PrecipScale.LevelFromRate` plus the knob shifts; C25 calls the Common function.
- `WeatherView.Level` is the DISPLAY level (the field level, rate-limited, times the shower pulse). It is for pictures, sound and light only. Forecast, surface cover and any other consumer that must not see a flurry's pulse read `IWeatherField` (`WeatherSample.Level01`), never `WeatherView.Level`. The unpulsed level exists only inside the frame driver (`FrameComposer.Level`, internal).
- Altitude argument: the `altM` of `IWeatherField.Sample`, `IWeatherSource.TrySample` and the forecast is metres ABOVE SEA LEVEL (`body.GetAltitude(position)`, `vessel.altitude`). It is never the height above the ground. `ForecastSite.AltAgl` keeps its A01 name and carries that altitude; the forecast passes it to the field unchanged, so forecast = live holds at any place and height. The forecast module fills it with `vessel.altitude` for a vessel and with the terrain altitude above sea level for the pad (`CelestialBody.TerrainAltitude(lat, lon, true)`). Renaming the field to `AltM` is a request to the orchestrator (Common change, `CommonVersion` bump, D30 uses the field).
- Legacy storm effect (open after C25): C25 deleted the cell scheduler (`StormCells`, `StormModel`), the EVE storm layers (`EveLayerStorm`, `EveCellLayer`, `EveStormMath`) and the legacy field bridge (`LegacyBridge`). `StormEffect`, `StormLog` and `StormVolume` still call a few of their members, so `LegacyStormShims.cs` keeps them as inert `[Obsolete]` shims: the scheduler makes no cell, the EVE layers inject nothing. `StormEffect` (C20), `StormLook`, `StormVolume` (C21), `StormShaders` (shim), `FogController` (the `legacy` flag and `LegacyHolds`), `StormCull` and the ten `[Obsolete]` aliases assigned to C20 above go with the last caller, which deletes `LegacyStormShims.cs` and the settings aliases of the cell scheduler (`StormMaxCells`, `ForceCell*`). `StormLook` is marked `[Obsolete]`; the last of those packages deletes it. The shims deviate from the plan rule "no inert interim copy" because their callers are outside C25's ownership; every member a caller can reach returns a neutral value (no intensity, no cell, no distance). Who removes what:

  | Shim | Callers | Removed by |
  |---|---|---|
  | `StormCells` (`Cell`, `Rules`, `Collect`, `IntensityAt`, `Nearest`, `Distance`) | `StormEffect`, `StormLog` | C20 |
  | `StormCells` (`Cell`, `Distance`), `StormModel.Unit` | `StormVolume` | C21 |
  | `StormModel.Resolve` | `StormEffect` | C20 |
  | `EveLayerStorm`, `EveCellLayer` (`Update`, `Release`, `Summary`, `HasVolumetric`, `OwnSnowField`) | `StormEffect` | C20 |
  | `EveLayerStorm.Supplying`, `BaseAboveGroundM`, `EveCellLayer.BaseAboveGroundM` | `StormVolume` | C21 |
  | `EveCellLayer` constants and `RangeFor` | `EveCoverageTests`, `EveSwirlTests` | C23 |
  | `StormShaders` (own shim) | `StormVolume`, `OrbitTextures`, `VolumePass` | C22 |

  The package that removes the last caller deletes `LegacyStormShims.cs`.
- The weather field is `GeneratorField` (`src/Atmospheres/Field/`), installed in `WeatherHub.Field` by `GeneratorModule` while `weatherEffects` and `genEnabled` are on. Its core is the source of `tools/atmosphere-sim/AtmoSim.Core`, compiled into the assembly by a pattern in the project file (no copy); `tools/sync_generator_core.py` records the golden values of `tests/data/generator` after the core changed. `GeneratorBodies` maps a body (rules, facts, terrain raster, fit of the temperature curves) to the core's parameter set. `Sample` is a pure function of (body, seed, place, altitude, UT): thread-safe, no state, no side file. Details of the field:
  - Determinism: the core and the inputs made from the game's curves (`ThermalFit`, the forced discs) use `DMath`, no platform transcendental, so the bits do not depend on the machine's libm. A different mod set (other game curves) is a different definition on purpose (`DefinitionId`). The Mono check of the golden values is the player series `player-C25` (scene `generator-golden`, `tests/series/rows/C25.json`); it measures the core's bits under Unity's Mono, on this machine's libm for the platform `Math.Asin` positions of the first row.
  - A body has a state only when its terrain sphere runs (`GeneratorRaster` reads the heights from the PQS and never starts a sphere): a body not visited yet gives `Sample == false`, and so no forecast for it. Answering earlier from a synthetic ocean source would be a different definition and must change the `DefinitionId`; it is not built.
  - `GetMap(Type)` holds the precipitation band index (`PrecipScale.BandOf(level, wind)` as a float), not a cloud type index. The cloud-map painter (C23) maps level, cloud water and uplift to the cloud types of the body's configuration.
  - The surface maps (snow, ice fraction, ice thickness, 256 x 128) are interpolated bilinearly from 64 x 32 sites; a texel with no site of its own kind (land or sea) among the four neighbours gets 0.
  - Measured against the game's temperature curves (B09 dumps): the zonal mean at sea level differs by 2.39 K at worst (limit 3 K).
  - Known limits of the generator (tuning round 2, `tools/atmosphere-sim/RESULT.md`, state REWORK with open GATE items; C25 is built on it): south front events 0.19 per 10 days and south lifetime 42 h, a remainder of similar diagonal streaks and a polar fan, pill-shaped patches of the moist belt, summer snow 0.11 at 55 degrees N latitude on high terrain.
- Hook classes are open to later fields: the view is mutated in place, providers derive from `WeatherSourceBase`, and Common bumps are batched.
- The atmospheres never reference `ReDefinition.dll`; they reach the host through the bridge behind `IPostUpscaleCompositor`.

## 4. The merge rule (`WeatherHub.Sample`)

The field counts as priority 0. Every provider that answers takes part.

- Wind (`WindWorld`, `GustFactor`): the highest priority owns it; a tie goes to the one asked first (the field, then the providers in the order of their priority and registration).
- `Level01`, `Type` and the three fractions: from the source with the largest level.
- `VisibilityM`: the visibilities multiply as shares of `WeatherSample.ClearVisibilityM`.
- Sun transmission: `OpticalDepthVertical` adds, so the transmissions multiply. `CloudCover01` combines as independent layers (1 - product of the clear shares).
- `TemperatureK`: the highest priority that knows it.
- `Readiness` is the worst of the sources, `Approximate` is true if any source says so.
- `windSource` (Auto / Ours / Foreign / Off) decides which providers the consumers register; the forecast's wind uses the same order and labels the source (`WeatherSource`).

## 5. `cloudSource`

| Value | Decks | 3D form (storm cores, curtain) | Particles and ground effects | Orbit and map picture | Old cell injection |
|---|---|---|---|---|---|
| Ours | own volume pass; EVE's weather layers on that body are off | own | own precipitation | own 2D shell | deleted |
| Eve | EVE renders the maps the field paints | own, only where needed | EVE's field and ground effects through the mirror; ours suppresses them only if `precipDensity` is on | EVE's 2D layer fed by the map plus the swirl | deleted |
| Both | as Eve, plus EVE's native weather on layers we do not paint (`useEveWeather`) | own | as Eve | as Eve | deleted |
| None | none from us | none | own precipitation | none | deleted |

A body's `CLOUDS` node may set its own `cloudSource`; without one the setting decides.

### Cloud types per level (encoded by the cloud-map painter, built into the EVE template)

| Level | Cloud | Base to top above the ground | Density | Colour |
|---|---|---|---|---|
| Flurries, Light | none of ours; EVE's weather deck or a thin SnowLight stratus | 600 to 1500 m, 300 m thick | 0.002 to 0.003 | light grey (190, 192, 196) |
| Moderate | SnowStratus | 400 to 1800 m | 0.005 to 0.008 | mid grey |
| Heavy | the storm type, low dark base | 300 to 2300 m | 0.012 | dark base |
| Blizzard | the storm type plus veil and volume | 300 to 2300 m | 0.012 to 0.016 | darkest |

The level changes density and thickness continuously through the type map's blend, never in a step.

## 6. Settings

`AtmospheresSettings` holds every setting of the project in one table (`SettingsTable.All`, `Settings/AtmospheresSettings.Keys.cs`), stored in `PluginData/settings.cfg` of the mod and nowhere else. A key is added there and nowhere else; packages only read (`AtmospheresSettings.GustStrength`, ...). Reset puts a setting back to its default. A value outside its range is brought into it, a value that is not a number is ignored. Tooltips are Title Case words. The window and the profiles are package D36's; the greyed-out reason ids are `AtmospheresSettings.GreyReasons` and the profile rows `AtmospheresSettings.ProfileRows`.

Greyed-out reasons: `needs-weather-effects` (`weatherEffects` off), `needs-eve` (no EVE or `cloudSource` Ours / None), `needs-host-api`, `needs-simulation`, `needs-volume-pass`, `needs-weather-audio`, `needs-forecast`, `needs-alerts`, `needs-wind-forces`, `needs-ground-cover`, `needs-sea-ice`, `needs-light-in-medium`, `needs-precipitation`, `needs-solar-dimming`, `needs-own-source`, `needs-own-clouds`, `needs-cloud-noise`, `needs-cloud-shadows`, `needs-ambient-taps`, `needs-entry-refinement`, `needs-lightning`.

`weatherEffects` is the master of the weather system: off, there is no simulation, no cloud-map painter and no side file. `enabled` stays the switch of the whole mod. The default of `weatherEffects` is off (master plan Q5) until the K-W3a rows pass; the E40 wave flips it to on. A test that needs weather sets it explicitly.

### All settings

The table of `SettingsTable.All`, in the order of the window.

**General**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `enabled` | on | on / off | Atmospheres | - | - |
| `weatherEffects` | off | on / off | Weather Effects | - | - |
| `yearLengthFactor` | 1 | 0.01 to 10 | Season Year Length | - | - |
| `seasonMode` | RealSeasons | GlobalSummer / GlobalWinter / GlobalSeasons / RealSeasons | Season Mode | - | - |
| `qualityTier` | Auto | Auto / Low / Medium / High / Ultra / Max | Quality Tier | - | quality |
| `qualityGovernor` | off | on / off | Quality Governor | needs-weather-effects | quality |

**Clouds**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `cloudSource` | Eve | Ours / Eve / Both / None | Cloud Source | needs-weather-effects | volume |
| `weatherCompose` | Auto | Auto / Scene / Host | Weather Composition | needs-weather-effects | volume |
| `useEveWeather` | on | on / off | Use EVE Weather | needs-eve | - |
| `eveLayerCap` | 4 | 1 to 8 | EVE Layer Cap | needs-eve | volume |
| `lightWeatherFrequency` | 1 | 0 to 2 | Light Weather Frequency | needs-simulation | - |
| `heavyShare` | 1 | 0 to 2 | Heavy Weather Share | needs-simulation | - |
| `typeByTemperature` | on | on / off | Precipitation Type By Temperature | needs-weather-effects | - |
| `ownClouds` | off | on / off | Own Volumetric Clouds | needs-own-source | - |
| `layerCap` | 4 | 1 to 4 | Own Cloud Layer Cap | needs-own-clouds | volume |
| `layerStepQuality` | Medium | Low / Medium / High | Own Cloud Step Quality | needs-own-clouds | volume |
| `layerStepGrowth` | 2 | 0 to 4 | Own Cloud Step Growth | needs-own-clouds | - |
| `cloudNoise` | on | on / off | Cloud Shape Noise | needs-own-clouds | - |
| `cloudNoiseUntile` | on | on / off | Cloud Noise Untiling | needs-cloud-noise | - |
| `cloudNoiseCurl` | on | on / off | Cloud Noise Curl | needs-cloud-noise | - |
| `cloudNoiseTier` | Medium | Low / Medium / High | Cloud Noise Size | needs-cloud-noise | volume |
| `cloudNoiseDistanceLod` | on | on / off | Cloud Noise Distance Detail | needs-cloud-noise | - |
| `cloudFineDetail` | on | on / off | Cloud Fine Detail Octave | needs-cloud-noise | - |
| `cloudEntryRefinement` | off | on / off | Cloud Entry Refinement | needs-own-clouds | - |
| `cloudRefineAtContours` | off | on / off | Cloud Refine Only At Contours | needs-entry-refinement | - |
| `cloudRefineBisect` | 0 | 0 to 4 | Cloud Entry Bisection Steps | needs-own-clouds | volume |
| `cloudRefineEdgeMask` | off | on / off | Cloud Refine At Edge Mask | needs-own-clouds | volume |
| `cloudHashDither` | on | on / off | Cloud White Dither | needs-own-clouds | - |
| `cloudLightQuality` | High | Low / Medium / High | Cloud Light Quality | needs-own-clouds | volume |
| `cloudLightOctaves` | 4 | 1 to 4 | Cloud Scattering Octaves | needs-own-clouds | - |
| `cloudPowder` | 0.8075 | 0 to 1 | Cloud Powder Effect | needs-own-clouds | - |
| `cloudSunTint` | on | on / off | Cloud Sun Colour Through Air | needs-own-clouds | - |
| `cloudMoonLights` | 2 | 0 to 2 | Cloud Moon Lights | needs-own-clouds | - |
| `cloudAmbientTaps` | off | on / off | Cloud Ambient Taps | needs-own-clouds | - |
| `cloudAmbientReuse` | 0 | 0 to 2 | Cloud Ambient Tap Reuse | needs-ambient-taps | - |
| `cloudLightCache` | off | on / off | Cloud Light Cache | needs-own-clouds | - |
| `cloudShadows` | on | on / off | Cloud Shadows | needs-own-clouds | - |
| `cloudShadowQuality` | Medium | Low / Medium / High | Cloud Shadow Quality | needs-cloud-shadows | volume |
| `cloudShadowFloor` | 0 | 0 to 1 | Cloud Shadow Floor | needs-cloud-shadows | - |
| `cloudReconMode` | Full | Full / Block / Low | Cloud Reconstruction | needs-own-clouds | - |
| `cloudResolution` | 1 | 1 to 4 | Cloud Resolution | needs-own-clouds | volume |
| `cloudSunMarchLong` | off | on / off | Cloud Long Sun March | needs-own-clouds | - |
| `cloudSunWhiteBalance` | on | on / off | Cloud Sun White Balance | needs-own-clouds | - |
| `cloudDiffusionDecay` | 0.02 | 0 to 0.2 | Cloud Diffusion Decay | needs-own-clouds | - |
| `cloudFarHaze` | off | on / off | Cloud Far Haze | needs-own-clouds | - |
| `cloudFarShapeMean` | off | on / off | Cloud Far Shape Mean | needs-own-clouds | - |
| `cloudFarMinSamples` | 0 | 0 to 16 | Cloud Far Minimum Samples | needs-own-clouds | - |
| `cloudFarPolicy` | off | on / off | Cloud Far March Policy | needs-own-clouds | - |
| `cloudFarMapFade` | off | on / off | Cloud Far Map Fade | needs-own-clouds | - |
| `cloudBaseVariation` | on | on / off | Cloud Base Variation | needs-own-clouds | - |
| `cloudBasePhysics` | on | on / off | Cloud Base From Humidity | needs-own-clouds | - |
| `cloudTopFromUpdraft` | on | on / off | Cloud Top From Updraft | needs-own-clouds | - |
| `baseRagged` | on | on / off | Cloud Ragged Underside | needs-own-clouds | - |

The own cloud system's keys (package T10) follow the stream T option structs: `LayerSwitches`, `NoiseSwitches`, `CloudLightSettings`, `CloudShadowSwitches`, `ReconSettings`; `OwnCloudOptions` writes them from the values. The default of `ownClouds` is off until the integration package (T09) accepts the system; the quality-reducing choices (`cloudResolution` above 1, which marches one pixel of each 2 x 2, 3 x 3 or 4 x 4 block per frame, `cloudReconMode` Low, which overrides the slider, `cloudEntryRefinement`, `cloudRefineAtContours`, which restricts the refinement to the contour quads, `cloudRefineBisect`, which finds the entry of a cloud by that many halvings of the step instead of the refinement's second march, and `cloudRefineEdgeMask`, which restricts the refinement to the blocks the edge mask flags and so runs the edge-adaptive picture) are off until the owner agrees. The tier mapping of `cloudResolution` is open (`TierRows.NotSet`). The cost controls and the look constants of the light have no row here: they are tuning keys (`layers.coverageBound`, `layers.lightStride`, `layers.lightCutoff`, `layers.emptyLeap`, `layers.cheapShare`, `layers.fogShare`, `layers.refineBudget`, `layers.refineFraction`, `layers.edgePower`, `light.octaveA`, `light.octaveB`, `light.octaveC`, `light.msBrightness`, `light.diffusion`, `light.diffusionTau`, `light.msIsotropy`, `light.ambientOcclusion`, `light.ambientGain`, `light.skyTint`, `light.ambientFloor`) with the compiled values of the structs as defaults. The look fixes and the far view of the own clouds have keys too (`cloudSunMarchLong`: the sun march is 4 km, the length the parity rows measured, and 9 km when on; `cloudSunWhiteBalance`, `cloudDiffusionDecay`; `cloudFarHaze`, `cloudFarShapeMean`, `cloudFarMinSamples`, `cloudFarPolicy`, `cloudFarMapFade`, all off), and so have the wet surfaces (`wetSurfaces`), the window droplets (`windowDroplets`) and the lightning (`lightning` and eight `lightning*` keys), each off until the owner agrees. The coverage threshold of the layers has a runtime shift for measuring the owner's open choice of the rain deck's coverage (0.5 or about 0.42): the tuning keys `cloud.coverageShift` (added to every layer's coverage) and `cloud.coverageShiftRain` (added on top for the group `weather`, the rain and snow decks), -1 to 1, default 0 = the configured coverage unchanged; the result is clamped to 0 to 1. The only reader of the threshold is `CloudLayerDef.EffectiveCoverage` (`CloudCoverShift`): the layer pass and the cloud shadows (frame values, re-uploaded when the shift changes), `LayerDensity`, `HeightDensity` and the `CloudCoverModel` twin all use it. The sphere scale of the noise is a body key (`sphereScale` in `CLOUDS`) and the layer set of a body is the `LAYER` nodes of `CLOUDS` (`CloudLayerSpec`).

**Wind**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `windSource` | Auto | Auto / Ours / Foreign / Off | Wind Source | needs-weather-effects | wind |
| `windInjection` | Auto | Auto / Aero / Force | Wind Injection | needs-wind-forces | wind |
| `windForces` | on | on / off | Wind Forces On Vehicles | needs-weather-effects | wind |
| `windStrength` | 1 | 0 to 2 | Wind Strength | needs-wind-forces | wind |
| `windOnLanded` | on | on / off | Wind On Landed Vehicles | needs-wind-forces | - |
| `gustStrength` | 1 | 0 to 2 | Gust Strength | needs-weather-effects | - |
| `windShear` | on | on / off | Wind Shear By Height | needs-simulation | - |
| `layerWindSteering` | MainPeriod | Off / MainPeriod / MapsOnly | Layer Wind Steering | needs-eve | - |
| `verticalMotion` | on | on / off | Vertical Cloud Motion | needs-simulation | - |
| `anvilTilt` | on | on / off | Anvil Tilt | needs-simulation | - |
| `solarDimming` | off | on / off | Solar Panel Dimming | needs-weather-effects | - |
| `solarDimmingStrength` | 1 | 0 to 1 | Solar Dimming Strength | needs-solar-dimming | - |
| `visibilityExport` | on | on / off | Export Visibility | needs-weather-effects | - |

**Simulation**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `genEnabled` | on | on / off | Weather Generator | needs-weather-effects | simulation |
| `simGrid` | Auto | Auto / Coarse / Normal / Fine | Simulation Grid | needs-simulation | simulation |
| `simLevels` | 0 | 0 to 32 | Simulation Levels | needs-simulation | simulation |
| `simSliceMs` | 0.5 | 0.1 to 4 | Simulation Slice | needs-simulation | simulation |
| `genSeed` | 1 | -2147483648 to 2147483647 | Weather Seed | needs-simulation | - |

**Volume**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `volumePass` | on | on / off | Volumetric Clouds And Curtains | needs-weather-effects | volume |
| `volumeResolution` | 1 | 0.25 to 1 | Volume Resolution | needs-volume-pass | volume |
| `volumeSteps` | 28 | 16 to 48 | Volume Steps | needs-volume-pass | volume |
| `volumeCompose` | Scene | Scene / PostUpscale | Volume Composition | needs-volume-pass | volume |

**Precipitation**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `precipDensity` | 1 | 0 to 2 | Precipitation Density | needs-weather-effects | precipitation |
| `precipSnow` | on | on / off | Draw Snow | needs-precipitation | - |
| `precipRain` | on | on / off | Draw Rain | needs-precipitation | - |
| `precipSleet` | on | on / off | Draw Sleet | needs-precipitation | - |
| `precipDust` | on | on / off | Draw Dust | needs-precipitation | - |
| `precipSand` | on | on / off | Draw Sand | needs-precipitation | - |
| `precipAsh` | on | on / off | Draw Ash | needs-precipitation | - |
| `precipMethane` | on | on / off | Draw Methane | needs-precipitation | - |
| `stormCullDistance` | off | on / off | Cull Distant Terrain In Storms | needs-weather-effects | precipitation |
| `fogBrightness` | 1 | 0.25 to 2 | Fog Brightness | needs-weather-effects | - |
| `veilStrength` | 0.85 | 0 to 0.95 | Veil Strength | needs-weather-effects | - |
| `blizzardVisibilityM` | 60 | 20 to 500 | Blizzard Visibility | needs-weather-effects | - |
| `skyDimming` | 0.6 | 0 to 1 | Sky Darkening | needs-weather-effects | - |

**Light**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `lightInMedium` | on | on / off | Light In Fog And Precipitation | needs-weather-effects | light |
| `lightCount` | 4 | 0 to 8 | Lamp Count | needs-light-in-medium | light |
| `lightInMediumStrength` | 1 | 0 to 2 | Lamp Scatter Strength | needs-light-in-medium | - |
| `aurora` | Auto | Off / Auto / Shell2D / Volumetric | Aurora | needs-weather-effects | light |
| `auroraAmbientTint` | 0.3 | 0 to 1 | Aurora Ambient Tint | needs-weather-effects | - |
| `cameraFrost` | on | on / off | Camera Frost | needs-weather-effects | light |
| `windowDroplets` | off | on / off | Window Droplets | needs-weather-effects | - |
| `lightning` | off | on / off | Lightning | needs-weather-effects | - |
| `lightningMaxPerSecond` | 3 | 1 to 3 | Lightning Strokes Per Second | needs-lightning | - |
| `lightningBrightness` | 1 | 0 to 1 | Lightning Brightness | needs-lightning | - |
| `lightningBolts` | on | on / off | Lightning Bolts | needs-lightning | - |
| `lightningTerrainLight` | on | on / off | Lightning Ground Light | needs-lightning | - |
| `lightningCloudFlash` | on | on / off | Lightning Cloud Flash | needs-lightning | - |
| `lightningThunder` | on | on / off | Lightning Thunder | needs-lightning | - |
| `lightningRealisticDelay` | on | on / off | Lightning Thunder Delay | needs-lightning | - |

**Audio**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `weatherAudio` | on | on / off | Weather Audio | needs-weather-effects | audio |
| `weatherAudioVolume` | 0.6 | 0 to 1 | Weather Audio Volume | needs-weather-audio | - |
| `audioWind` | on | on / off | Wind Sound | needs-weather-audio | - |
| `audioPrecip` | on | on / off | Precipitation Sound | needs-weather-audio | - |
| `audioFootsteps` | on | on / off | Footstep Sound | needs-weather-audio | - |
| `audioFootstepsSurface` | on | on / off | Footsteps By Surface | needs-weather-audio | - |
| `audioDistantStorm` | off | on / off | Distant Storm Sound | needs-weather-audio | - |
| `audioQuietSnow` | on | on / off | Quiet Snow | needs-weather-audio | - |
| `audioMuffling` | Auto | Auto / Own / Rse | Sound Muffling | needs-weather-audio | - |
| `eveSound` | Auto | Auto / Eve / Ours / Both | EVE Sound | needs-weather-audio | - |
| `audioBedSource` | Synth | Synth / Recorded / Mix | Sound Bed Source | needs-weather-audio | - |
| `audioProbeSolo` | off | on / off | Solo Weather Audio For Probes | needs-weather-audio | - |

**Cover**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `groundCover` | on | on / off | Ground Cover | needs-weather-effects | cover |
| `coverStrength` | 1 | 0 to 1 | Cover Strength | needs-ground-cover | - |
| `vehicleCover` | Landed | Off / Landed / Always | Vehicle Cover | needs-ground-cover | cover |
| `vehicleCoverMethod` | Shell | Shell / ScreenSpace | Vehicle Cover Method | needs-ground-cover | - |
| `tracks` | on | on / off | Tracks In Cover | needs-ground-cover | cover |
| `coverDrift` | 0.3 | 0 to 1 | Cover Drift | needs-ground-cover | - |
| `wetSurfaces` | off | on / off | Wet Surfaces | needs-weather-effects | - |

**Ice**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `seaIce` | Orbit | Off / Orbit / OrbitAndGround / Physical | Sea Ice | needs-weather-effects | ice |
| `iceDrawDistanceKm` | 5 | 1 to 50 | Ice Draw Distance | needs-sea-ice | ice |
| `iceCollision` | off | on / off | Ice Collision | needs-sea-ice | - |

**Forecast**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `forecastEnabled` | on | on / off | Forecast | needs-weather-effects | forecast |
| `alertsEnabled` | on | on / off | Weather Alerts | needs-forecast | - |
| `alertLeadHours` | 3 | 0.5 to 24 | Alert Lead Time | needs-alerts | - |
| `alertMinLevel` | Heavy | Flurries / Light / Moderate / Heavy / Blizzard | Alert Minimum Level | needs-alerts | - |
| `alertWindMs` | 15 | 0 to 60 | Alert Wind Speed | needs-alerts | - |
| `alertWarpStop` | off | on / off | Stop Time Warp On Alert | needs-alerts | - |
| `alertKac` | off | on / off | Kerbal Alarm Clock Alarms | needs-alerts | - |
| `alertMessageApp` | on | on / off | Message App Alerts | needs-alerts | - |
| `alertScreenMessage` | on | on / off | Alert Screen Messages | needs-alerts | - |
| `forecastOverlay` | on | on / off | Forecast Map Overlay | needs-forecast | - |
| `alertSites` | empty | text | Alert Sites | needs-alerts | - |
| `forecastHorizonHours` | 24 | 6 to 96 | Forecast Horizon | needs-forecast | forecast |
| `forecastUncertainty` | 0 | 0 to 1 | Forecast Uncertainty | needs-forecast | - |
| `launchMaxLevel` | Heavy | Flurries / Light / Moderate / Heavy / Blizzard | Launch Maximum Level | needs-forecast | - |
| `launchMaxWindMs` | 10 | 0 to 60 | Launch Maximum Wind | needs-forecast | - |
| `launchMinVisibilityM` | 1000 | 0 to 20000 | Launch Minimum Visibility | needs-forecast | - |
| `launchLeadHours` | 2 | 0 to 24 | Launch Lead Time | needs-forecast | - |

**Orbit**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `orbitBodies` | Focused | Focused / Visible / All | Orbit View Bodies | needs-weather-effects | - |
| `orbitOverlay` | on | on / off | Weather In Orbit View | needs-weather-effects | - |
| `orbitMapWidth` | 2048 | 1024 to 4096 | Orbit Map Width | needs-weather-effects | - |

**Diagnostics**

| Key | Default | Range | Tooltip | Greyed out | Profile row |
|---|---|---|---|---|---|
| `forceLevel` | -1 | -1 to 1 | Force Precipitation Level | - | - |
| `forceType` | Auto | Auto / Snow / Rain / Sleet / FreezingRain / Dust / Sand / Ash / Methane | Force Precipitation Type | - | - |
| `forceBody` | empty | text | Force Body | - | - |
| `forcePhase` | -1 | -1 to 1 | Force Season Phase | - | - |
| `atmoDebug` | Off | Off / Contrib / Level / Maps | Debug Overlay | - | - |

### Audit of the settings that existed before the contract

Removal of the `[Obsolete]` aliases in `Legacy.cs` (24 members, 13 aliases and 11 plain fields; a test counts them and its expected number is lowered by the package that removes them): `StormsEnabled`, `StormFrequency`, `StormSeed`, `StormVisibilityM`, `StormVeil`, `StormSkyDarkening`, `StormEveRainToSnow`, `StormVolume`, `StormVolumeSteps` and `ForceStorm` by C20 (storm effect and volume); `SnowStrength`, `OrbitSnow` and `OrbitSnowWidth` by D34 (cover and orbit snow). The plain fields of deleted features (`WinterSnowLine`, `GroundRefreshMode`, `RefreshSeconds` by D34; `StormSnowfall`, `StormOwnFlakes` (removed by C22); `StormEveSnow`, `StormClouds` by C23; `StormCellRadiusKm`, `StormMaxCells`, `ForceCell*` by C25) go with the same packages. Expected count of `[Obsolete]` members at F50: 0.

The mod is unreleased, so no old name is kept as a key. The C# members of the old names stay as `[Obsolete]` aliases or plain fields in `Settings/AtmospheresSettings.Legacy.cs` until the last caller is gone; the package that deletes the last caller deletes the line.

| Old key | Disposition | New key or reason |
|---|---|---|
| `enabled` | keep | the switch of the whole mod |
| `yearLengthFactor` | keep | the season clock's year |
| `snowStrength` | rename | `coverStrength` |
| `winterSnowLine` | delete | the cold is the field's; `SnowMask` goes with the cover package |
| `groundRefreshMode`, `refreshSeconds` | delete | the cover package repaints within its own pass budget |
| `storms` | delete | `weatherEffects` |
| `stormFrequency` | rename | `heavyShare` (`lightWeatherFrequency` is new) |
| `stormSeed` | rename | `genSeed` |
| `stormVisibility` | rename | `blizzardVisibilityM` |
| `stormVeil` | rename | `veilStrength` |
| `stormSnowfall` | delete | `precipDensity` at 0, and the per-type switches |
| `stormSkyDarkening` | rename | `skyDimming` |
| `stormEveSnow` | delete | `cloudSource` and `useEveWeather` |
| `stormEveRainToSnow` | rename | `typeByTemperature` |
| `stormCellRadiusKm`, `stormMaxCells` | delete | weather systems emerge from the generator (`GeneratorRules`) |
| `stormVolume` | rename | `volumePass` |
| `stormVolumeSteps` | rename | `volumeSteps` |
| `stormClouds` | delete | `cloudSource` replaces the cell layers |
| `stormOwnFlakes` | delete | `weatherCompose` and `IEveFieldSuppressor` decide who draws |
| `stormCullDistance` | keep | unchanged |
| `orbitSnow` | rename | `orbitOverlay` |
| `orbitSnowWidth` | rename | `orbitMapWidth` |
| `forceStorm` | rename | `forceLevel` |
| `forceStormCell` | delete | a forced cell is a test forcing of the field (`ISurfaceForcing`) |
| `forcePhase` | keep | unchanged |

Keys of the plans that are not part of the contract: `iceWinterLine`, `freezeLineDeg`, `lagYears` (the field decides what freezes; there is no second cold and no lag).

## 7. Tuning

Every look constant of a package (sun share, ambient, density, erosion, colours, gains, offsets) is read as `Tuning.Get("package.name", compiledDefault)`; the compiled value is the fallback, so a package works without any file. `Tuning.Flag` reads a switch. The values come from nodes of the form

```
ATMO_TUNING
{
    c20.sunShare = 0.65
}
```

in `GameData/ReDefinition/Atmospheres/Tuning/*.cfg` (package E40 owns the files; ModuleManager can patch them). Later nodes win. `Tuning.SetOverride(key, value)` lets a series variant change a constant inside one start of the game; it takes effect at the next frame boundary (`Tuning.BeginFrame`, called by `ModuleRunner` before the modules tick), so every reader in one frame sees the same numbers. The keys with their default and range are listed in each package's report for E40.

## 8. The budget ledger

Medium on the A4500 or the 3070: about 22 ms per frame with raytracing Full, 15.9 ms with it off. The whole-system GPU budget by tier: Low 1.2 ms, Medium 2.6 ms, High 3.6 ms, Ultra 5.0 ms, Max measured. The curtain-only subrow of the volume pass: Low 0.4, Medium 0.8, High 1.5, Ultra 2.5 ms. VRAM by tier: Low up to 80 MB, Medium 200 MB, High 300 MB, Ultra 340 MB, Max measured; below 10 GB of video memory the defaults fall to the Low rows. Rows are at the Medium render size 1707x960 unless a 2560x1440 value is shown. Package E42 asserts the sum after the shed order has run and the curtain subrow separately, at the worst-case scene, in absolute terms.

| System | Owner | GPU ms | Main-thread CPU | Worker CPU | VRAM | RAM |
|---|---|---|---|---|---|---|
| Fog, ambient and sun writer | C22, C20 | about 0 | under 0.05 ms | - | 0 | - |
| Own volume slab, half resolution | C21 | 0.56 at 1707x960, 1.28 at 2560x1440 | 20 to 40 us per cell | window resample | 15 to 30 MB | 3D window about 2 MB |
| Curtain | C21 | 0.02 to 0.70 | as above | - | with the slab | - |
| Veil and particles, host composite | C22 | under 0.5 | about 0 | - | small | - |
| EVE particle field in `Eve` mode | EVE (accepted) | constant vertex cost of 400k to 600k particles | - | - | EVE | - |
| Map painter | C23 | at most 0.3 per update (every 1 to 2 s) | swap and upload at most 0.1 ms | readback mirror | 2 to 4 MB plus mirror | mirror 0.5 MB |
| Simulation | C25 | - | swap at most 0.05 ms | amortised at most 0.5 ms per frame | - | at most 30 MB per copy, at most 4 copies (about 120 MB) |
| Forecast worker | B12 | - | at most 0.1 ms | at most 1.5 s per 24 h run, all sites | - | within the 120 MB |
| Checkpoint IO | C25 | - | 0 | on a worker | - | file at most 30 MB per body |
| Lamp scatter | D32 | at most 0.5 (4 lamps), 0 when no lamp is within 150 m | at most 0.05 ms | - | small | - |
| Camera frost | D37 | at most 0.25 while active | - | - | atlas 4 MB | - |
| Aurora shell or volumetric | D38 | at most 0.15 or 0.8 | - | - | about 4 MB | - |
| Ground cover repaint | D34 | about 0 | inside a 2 ms pass, no frame above 1 ms | resample worker | 1 MB per layer | - |
| Vehicle shells, 200 parts | D34 | 0.3 to 0.6 | 0.05 ms | - | cached meshes | - |
| Tracks vertex colours or ribbons | D34 | about 0, under 0.2 | within the repaint pass | - | tiles up to 4 MB | - |
| Ice overlay | D35 | under 0.5 | - | grid once | clipmap plus 1 MB | - |
| Raytracing repack and fog | C24 | under 0.1, under 0.05 | - | - | 16 MB (existing) | - |
| Audio | D33 | 0 | 0.05 ms | at most 1 % of one core (audio thread) | - | at most 12 MB |

Rules. The camera-local passes (volume, precipitation, lamp scatter, frost, ice, shells) cost nothing below level 0.04. The simulation, the painter and the forecast are worker or GPU jobs. "Outside a storm at most 0.3 ms" means the main thread only. `weatherEffects` off means no simulation, no painter and no side file. Hitch limits: no main-thread task above 1 ms (Low and Medium) or 2 ms (High); the longest frame at most three times the median in the natural run; no `GC.Collect` over 10 minutes. RAM in total about 135 MB (simulation 120 MB, audio 12 MB, mirror and window about 3 MB); side files at most 30 MB per body.

Shed order, when the sum is over the tier's budget: tracks ribbons and shells, then the far rings of the ice, then the lamp-scatter count, then the volume steps and resolution, then the particle counts. Quality costs only after the owner agrees; the owner is asked whether an overshoot at the Max tier is acceptable.

## 9. The key sweep rule

A backticked lowerCamelCase token in a brief that looks like a setting must be declared in section 6 (`AtmospheresSettings.Keys`), or the brief must say it is a key of another system (an EVE field, a node key of `REDEFINITION_BODY`). The check is a name search over the briefs (`tools/checks/check_briefs.py`); it cannot see two packages that own the same key. The same search holds every type and member name in backticks against this file and the Common sources.

## 10. Open points marked `[VERIFY]` in the code

- The unit of `WeatherWindow.CloudWater` (grams per cubic metre) is settled by the simulation package.
- `PrecipRules.PlaceholderBandRates` are placeholders until the header of package B10 signs the absolute rate bands.
- The pad site and the vessel site of the forecast module (`SpaceCenter.Instance` in the flight scene, the unit of `SpaceCenter.Latitude/Longitude`, the terrain altitude of the pad, `vessel.altitude` as altitude above sea level) are checked by the rows `B12.*` of K-W3a (`tests/series/rows/B12.json`, emitted by `B12Probe`).
- `GameDatabase.GetConfigNodes("ATMO_TUNING")` returns the inner nodes; checked in the game by the first series.
- `eveLayerCap`, `iceDrawDistanceKm`, `alertWindMs` and `auroraAmbientTint` have starting defaults that their packages may change in the next announced wave.
