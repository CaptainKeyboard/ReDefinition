using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The look of a snow storm in flight. It sets Unity's global fog (FogController keeps the bookkeeping), the ambient factor and
    // EVE's snow particle field where EVE has one. The veil and the falling snow are the precipitation renderer's (Render/Precip). It reads the storm intensity at the flight camera from the storm cells (StormCells: places on Kerbin with a
    // radius, a life and a drift), moves a smoothed copy toward it and applies it. The cells near the camera are also drawn from
    // afar as a snow curtain under a dark cloud base (StormVolume). It sets no image effect and no command buffer, so it does not
    // meet the upscalers or raytracing of ReDefinition. Every global it changes is put back when the storm ends and when the scene is
    // left; the fog is put back only while it is still the one the storm wrote. A failure in any step logs once, puts everything back
    // and switches the effect off for the scene. The log lines are StormLog's. The ambient and sun light are no longer written here:
    // the frame driver's LightWriter does it from WeatherView (C20); this class is the legacy cell effect that package C25 removes
    // when the simulation takes over.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class StormEffect : MonoBehaviour
    {
        // How fast the applied intensity follows the target, per second of real time: a full change takes two seconds.
        internal const float FollowPerSecond = 0.5f;

        // Below this applied intensity nothing is drawn and the globals are put back.
        internal const float ActiveBelow = 0.002f;

        // The longest frame time the follow step uses, in seconds: a hitch does not make the storm jump.
        internal const float MaxFrameSeconds = 0.1f;

        // The storm counts as settled when the applied intensity is this close to the target, and the settle line is written again
        // only when the target moved by more than SettledLogDelta since the last line.
        internal const float SettledWithin = 0.005f;
        internal const float SettledLogDelta = 0.01f;

        // The storm cells already announced in the log: at most this many are remembered, and this many are dropped when it is full.
        internal const int KnownCellsMax = 256;
        internal const int KnownCellsDrop = 128;

        // The rain coverage from which EVE's snow sound is raised with it.
        internal const float RainSoundLevel = 0.05f;

        // The fog colour by day, before StormLook.FogBrightness.
        private static readonly Color FogDay = new Color(0.80f, 0.82f, 0.85f, 1f);

        private float current;
        private float target;
        private bool targetForced;
        private bool active;
        private bool broken;
        private readonly FogController fog = new FogController();
        private float settledLogged = -1f;
        private int stormsSeen;
        private double yearSeconds;
        private readonly StormCull cull = new StormCull();
        private float ambientFactor = 1f;
        private float eveRaise;
        private bool hooked;

        // EVE's rain replaced by snow: whether the camera is over snow or ice, whether the replacement runs, the cover there, the frame
        // of the last pre-cull pass, and what was logged.
        private bool cold;
        private bool rainToSnow;
        private double snowCover;
        private int preCullFrame = -1;
        private bool rainLogged;
        private bool notControllableLogged;
        private bool noVolumetricLogged;

        // The storm cells around the camera of this frame, their number, the planet and the camera's place; the intensity at the camera.
        private readonly StormCells.Cell[] cells = new StormCells.Cell[StormCells.Capacity];
        private int cellCount;
        private int drawnCount;
        private CelestialBody cellBody;
        private Camera cellCamera;
        private double cameraLatitude, cameraLongitude, cameraAltitude;
        private double cellIntensity;
        private StormVolume volume;
        private readonly StormCells.Cell[] eveCells = new StormCells.Cell[StormCells.Capacity];
        private int eveCellCount;
        private float nextEveCollect;
        private readonly List<ulong> knownCells = new List<ulong>();
        private int cellsBorn;

        private void Start()
        {
            // Without the main mod (HostGate) the atmospheres do not start: the effect stays off.
            if (!HostGate.Open)
            {
                enabled = false;
                return;
            }
            try
            {
                volume = new StormVolume();
                AtmospheresLog.Say(StormLog.Ready());
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // Runs for every camera before it culls, after every Update and LateUpdate: the ambient light and EVE's snow coverage are
        // written here, once per frame, so that nobody else's write of the frame comes after ours.
        private void OnPreCull(Camera camera)
        {
            if (broken) return;
            try
            {
                if (camera == cellCamera) cull.Apply(camera, active ? StormLook.VisibilityMetres(current, AtmospheresSettings.StormVisibilityM) : 0);
                if (preCullFrame == Time.frameCount) return;
                preCullFrame = Time.frameCount;
                // The snow level of the frame: the storm's, and where EVE's rain is replaced the rain's coverage (what EVE would have shown).
                float snow = active ? eveRaise : 0f;
                if (rainToSnow)
                {
                    float rain = EveRainLink.Suppress();
                    if (rain > snow) snow = rain;
                    int sounds = rain >= RainSoundLevel ? EveSnowLink.RaiseSound(rain) : 0;
                    if (rain > 0f && !rainLogged)
                    {
                        rainLogged = true;
                        AtmospheresLog.Say(StormLog.RainReplaced(rain, EveSnowLink.Fields, EveSnowLink.Names(), sounds));
                    }
                }
                if (snow > 0f) EveSnowLink.Raise(snow);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        private void Update()
        {
            if (broken) return;
            try
            {
                CollectCells();
                target = Target();
                float step = FollowPerSecond * Mathf.Min(Time.unscaledDeltaTime, MaxFrameSeconds);
                current = Mathf.MoveTowards(current, target, step);
                UpdateRainToSnow();
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // Whether the camera is over snow or ice (the season's snow cover at its place), and then EVE's rain is replaced by its snow.
        // The cover is SnowMask.Coverage without the noise of the ground, at the camera's latitude and height above sea level.
        private void UpdateRainToSnow()
        {
            bool wanted = false;
            if (AtmospheresSettings.StormEveRainToSnow && cellBody != null && EveRainLink.Controllable)
            {
                double phase = AtmospheresSettings.ForcePhase >= 0f ? AtmospheresSettings.ForcePhase : SeasonClock.Phase(Planetarium.GetUniversalTime(), yearSeconds);
                snowCover = SnowMask.Coverage(cameraLatitude, cameraAltitude, phase, 0, AtmospheresSettings.WinterSnowLine);
                cold = EveRainLink.Cold(cold, snowCover);
                wanted = cold;
            }
            else
            {
                cold = false;
                if (AtmospheresSettings.StormEveRainToSnow && !EveRainLink.Controllable && !notControllableLogged)
                {
                    notControllableLogged = true;
                    AtmospheresLog.Say("EVE rain to snow: not controllable: " + EveRainLink.Summary);
                }
            }
            if (wanted)
            {
                EveRainLink.Scan(cellBody.name);
                EveSnowLink.Scan(cellBody.name);
            }
            if (wanted == rainToSnow) return;
            rainToSnow = wanted;
            rainLogged = false;
            if (wanted)
            {
                AtmospheresLog.Say(StormLog.RainSuppressed(EveRainLink.Describe(), snowCover, cameraLatitude, cameraAltitude, EveSnowLink.Fields));
            }
            else
            {
                EveRainLink.Release();
                if (!active) EveSnowLink.Release();
                AtmospheresLog.Say("EVE rain restored (left the snow region or the setting is off)");
            }
        }

        private void LateUpdate()
        {
            if (broken) return;
            try
            {
                EveLayerStorm.Update(cellBody, eveCells, cellBody != null ? eveCellCount : 0, cameraAltitude);
                EveCellLayer.Update(cellBody, eveCells, cellBody != null ? eveCellCount : 0, cameraAltitude);
                if (volume != null)
                {
                    if (cellCount > 0 && cellBody != null) volume.Update(cellCamera, cellBody, cells, drawnCount, cameraAltitude);
                    else volume.HideAll();
                }
                if (current > ActiveBelow) Apply();
                else if (active) End();
                SyncHook();
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        // The pre-cull hook is on while a storm is on or the rain is being replaced.
        private void SyncHook()
        {
            bool need = active || rainToSnow;
            if (need == hooked) return;
            hooked = need;
            if (need) Camera.onPreCull += OnPreCull;
            else Camera.onPreCull -= OnPreCull;
        }

        private void OnDestroy()
        {
            if (!HostGate.Open) return;
            Teardown();
        }

        // A step failed: say so once, put everything back and leave the effect off for this scene.
        private void Break(Exception e)
        {
            if (broken) return;
            broken = true;
            AtmospheresLog.Failed("atmospheres-storm", "the storm effect failed and is switched off for this scene", e);
            Teardown();
        }

        // Puts every global back and frees what was made; each step on its own, so that one failure does not keep the others.
        private void Teardown()
        {
            if (rainToSnow)
            {
                rainToSnow = false;
                Quietly(EveRainLink.Release);
                Quietly(EveSnowLink.Release);
            }
            if (active) Quietly(End);
            if (hooked)
            {
                hooked = false;
                Camera.onPreCull -= OnPreCull;
            }
            Quietly(cull.Restore);
            StormVolume.CameraVisibilityM = 0;
            if (volume != null) Quietly(volume.Dispose);
            Quietly(EveCellLayer.Release);
            Quietly(EveLayerStorm.Forget);
            volume = null;
        }

        private static void Quietly(Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-storm-teardown", "a step of putting the storm effect away failed", e);
            }
        }

        // The storm cells around the flight camera now (on Kerbin, in its atmosphere), and the intensity they give at the camera.
        // The cells are the schedule of StormModel's spawn rule when storms are on, and the forced cell of the diagnostics setting.
        private void CollectCells()
        {
            cellCount = 0;
            drawnCount = 0;
            cellIntensity = 0;
            cellBody = null;
            Vessel vessel = GameGlobals.ActiveVessel;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (!AtmospheresSettings.Enabled || vessel == null || vessel.mainBody == null || cam == null) return;
            CelestialBody body = vessel.mainBody;
            if (!SeasonClock.Applies(body.name) || !body.atmosphere || body.orbit == null) return;
            // The cell's cloud is EVE's: without EVE's volumetric clouds there are no cells and no storm.
            if (!EveCellLayer.HasVolumetric)
            {
                if (!noVolumetricLogged)
                {
                    noVolumetricLogged = true;
                    AtmospheresLog.Say("storms: needs EVE with Volumetric Clouds (" + EveCellLayer.Summary + "); no storm cells, ground and orbit snow only");
                }
                return;
            }
            Vector3d position = cam.transform.position;
            cameraLatitude = body.GetLatitude(position);
            cameraLongitude = body.GetLongitude(position);
            cameraAltitude = body.GetAltitude(position);
            yearSeconds = SeasonClock.YearSeconds(body.orbit.period, AtmospheresSettings.YearLengthFactor);
            var rules = new StormCells.Rules
            {
                BodyRadius = body.Radius,
                YearSeconds = yearSeconds,
                ForcedPhase = AtmospheresSettings.ForcePhase,
                WinterLine = AtmospheresSettings.WinterSnowLine,
                Frequency = AtmospheresSettings.StormFrequency,
                RadiusKm = AtmospheresSettings.StormCellRadiusKm,
                Seed = AtmospheresSettings.StormSeed,
                Scheduled = AtmospheresSettings.StormsEnabled,
                HasForced = AtmospheresSettings.ForceCellOn,
                ForcedLatitude = AtmospheresSettings.ForceCellLatitude,
                ForcedLongitude = AtmospheresSettings.ForceCellLongitude,
                ForcedRadiusKm = AtmospheresSettings.ForceCellRadiusKm
            };
            cellCount = StormCells.Collect(cells, Planetarium.GetUniversalTime(), cameraLatitude, cameraLongitude, StormVolume.RangeM, rules);
            // The cells of the EVE layer's coverage map reach farther than the ones that are drawn raymarched; twice a second is enough.
            if (Time.unscaledTime >= nextEveCollect)
            {
                nextEveCollect = Time.unscaledTime + 0.5f;
                eveCellCount = StormCells.Collect(eveCells, Planetarium.GetUniversalTime(), cameraLatitude, cameraLongitude,
                    EveCellLayer.RangeFor(cameraAltitude, body.Radius, MapView.MapIsEnabled), rules);
            }
            cellBody = body;
            cellCamera = cam;
            cellIntensity = StormCells.IntensityAt(cells, cellCount, cameraLatitude, cameraLongitude, body.Radius);
            LogBirths(body);
            drawnCount = StormCells.Nearest(cells, cellCount, AtmospheresSettings.StormMaxCells);
        }

        // One line for each cell that comes into range for the first time.
        private void LogBirths(CelestialBody body)
        {
            for (int i = 0; i < cellCount; i++)
            {
                ulong key = cells[i].Key;
                if (knownCells.Contains(key)) continue;
                if (knownCells.Count >= KnownCellsMax) knownCells.RemoveRange(0, KnownCellsDrop);
                knownCells.Add(key);
                cellsBorn++;
                double ground = 0;
                try
                {
                    ground = body.TerrainAltitude(cells[i].Latitude, cells[i].Longitude, false);
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("atmospheres-storm-ground", "storm cell ground height failed", e);
                }
                AtmospheresLog.Say(StormLog.CellBorn(cellsBorn, cells[i], ground));
            }
        }

        // The intensity the flight camera is in now, 0 to 1: the largest of the cells at its place, times the height factor.
        private float Target()
        {
            targetForced = false;
            Vessel vessel = GameGlobals.ActiveVessel;
            if (cellBody == null || vessel == null) return 0f;
            double height = StormLook.HeightFactor(vessel.altitude);
            if (height <= 0) return 0f;
            targetForced = AtmospheresSettings.ForceStorm >= 0f;
            return (float)(StormModel.Resolve(AtmospheresSettings.ForceStorm, true, cellIntensity) * height);
        }

        private void Apply()
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel vessel = GameGlobals.ActiveVessel;
            if (cam == null || vessel == null) return;
            if (!active) Begin(cam);

            double visibility = StormLook.VisibilityMetres(current, AtmospheresSettings.StormVisibilityM);
            StormVolume.CameraVisibilityM = visibility;
            double sinSun = 0;
            CelestialBody sunBody = GameGlobals.Sun;
            if (sunBody != null)
            {
                Vector3d toSun = (sunBody.position - vessel.GetWorldPos3D()).normalized;
                sinSun = Vector3d.Dot(toSun, vessel.upAxis);
            }
            float brightness = (float)StormLook.FogBrightness(sinSun);
            Color colour = new Color(FogDay.r * brightness, FogDay.g * brightness, FogDay.b * brightness, 1f);

            float density = (float)StormLook.FogDensity(visibility);
            if (fog.FoundOutsideChange(RenderSettings.fogDensity))
                AtmospheresLog.WarnOnce(StormLog.FogChangedOutside(RenderSettings.fogDensity, fog.WrittenDensity));
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = density;
            RenderSettings.fogColor = colour;
            fog.Wrote(density, colour);

            // The veil and the flakes are the precipitation renderer's (Render/Precip); this class keeps the fog and the ambient factor.
            float darkening = AtmospheresSettings.StormSkyDarkening;
            float groundAlpha = (float)StormLook.VeilAlpha(current, AtmospheresSettings.StormVeil);
            float skyAlpha = Mathf.Max(groundAlpha, (float)StormLook.SkyVeilAlpha(current, AtmospheresSettings.StormVeil));
            ambientFactor = (float)StormLook.AmbientFactor(current, darkening);
            // In a cell the cell layer's own snow field falls by the coverage of the cell; the stock fields are raised only elsewhere
            // (a storm that is forced without a cell).
            bool cellDrivesField = EveCellLayer.OwnSnowField && cellIntensity > 0;
            eveRaise = AtmospheresSettings.StormEveSnow && !cellDrivesField ? current : 0f;
            if (AtmospheresSettings.StormEveSnow) EveSnowLink.Scan(vessel.mainBody.name);
            if (Mathf.Abs(current - target) < SettledWithin && Mathf.Abs(target - settledLogged) > SettledLogDelta)
            {
                settledLogged = target;
                AtmospheresLog.Say(StormLog.StormSettled(new StormLog.Settled
                {
                    Intensity = target,
                    Source = targetForced ? " (forced)" : AtmospheresSettings.ForceCellOn ? " (forced cell)" : " (schedule)",
                    VisibilityM = visibility,
                    VeilAlpha = StormLook.VeilAlpha(current, AtmospheresSettings.StormVeil),
                    SkyVeilAlpha = skyAlpha,
                    Brightness = brightness,
                    AmbientFactor = ambientFactor,
                    AmbientScaling = WeatherFrameDriver.Light != null ? WeatherFrameDriver.Light.Ambient.DescribeScaling() : "not scaling (no frame driver)",
                    AmbientExternalWrites = WeatherFrameDriver.Light != null ? WeatherFrameDriver.Light.ExternalWrites : 0,
                    EveFields = AtmospheresSettings.StormEveSnow ? EveSnowLink.Fields.ToString(System.Globalization.CultureInfo.InvariantCulture) : "off"
                }));
            }
        }

        // The first frame of a storm: remember the fog, create the pieces and say which path is used.
        private void Begin(Camera cam)
        {
            active = true;
            stormsSeen++;
            settledLogged = -1f;
            fog.Save(RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogDensity, RenderSettings.fogColor);
            Vessel flying = GameGlobals.ActiveVessel;
            int eveFields = AtmospheresSettings.StormEveSnow && flying != null && flying.mainBody != null ? EveSnowLink.Scan(flying.mainBody.name) : 0;
            string eveState = StormLog.EveSnowState(AtmospheresSettings.StormEveSnow, EveSnowLink.Controllable, eveFields, EveSnowLink.Names(), EveSnowLink.Summary);
            AtmospheresLog.Say(StormLog.StormBegins(stormsSeen, cam, fog, eveState));
        }

        // The storm is over: the fog is as it was (unless someone else owns it now).
        private void End()
        {
            active = false;
            bool wasSaved = fog.Saved;
            bool restored = !wasSaved;
            if (wasSaved)
            {
                FogMode savedMode = fog.SavedMode;
                bool savedFog = fog.SavedFog;
                float savedDensity = fog.SavedDensity;
                Color savedColour = fog.SavedColour;
                if (fog.Restore(RenderSettings.fog, RenderSettings.fogMode, RenderSettings.fogDensity, RenderSettings.fogColor))
                {
                    RenderSettings.fog = savedFog;
                    RenderSettings.fogMode = savedMode;
                    RenderSettings.fogDensity = savedDensity;
                    RenderSettings.fogColor = savedColour;
                    restored = true;
                }
            }
            settledLogged = -1f;
            eveRaise = 0f;
            cull.Restore();
            StormVolume.CameraVisibilityM = 0;
            if (!rainToSnow) EveSnowLink.Release();
            AtmospheresLog.Say(StormLog.StormEnds(restored));
        }
    }
}
