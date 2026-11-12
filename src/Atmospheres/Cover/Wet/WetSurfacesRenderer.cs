using System;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The wet ground (package T08, item 1): the water on the ground at the camera's place, from the field's rain history, drawn into the
    // deferred G-buffer as darker, glossier ground with puddles (WetSurfacesPass and the shader ReDefinition/AtmospheresWet). A module
    // of the atmospheres, setting weatherEffects (the T10 package adds its own switch).
    //
    // State: the water the terrain holds and the water standing on it (WetnessModel), kept for the camera's site, a cell of half a
    // degree of latitude and longitude. A new site, a scene change or a time warp is caught up by replaying the field's weather over the
    // lookback (WetnessCatchUp), a few dozen steps per frame; the step of a normal frame is the frame itself, sampled every
    // SampleEveryS of game time. The weather at a UT is the field's sample at the camera's place: the precipitation level, type and
    // fractions, the temperature and the wind; humidity is estimated (rain 0.95, else 0.45 + 0.4 cloud cover) because the weather
    // sample carries none [VERIFY against the field's humidity map], and the sun's share is the present one for the whole replay.
    //
    // Conflicts: with cloudSource = Eve (TVC or EVE as the renderer) EVE's own wet surfaces are driven by EveGroundEffects and run in
    // the same G-buffer; this overlay then stays off unless wet.overEve is set. Deferred: the pass needs the camera on the deferred
    // path; the forward path gets no overlay (the forward fallback is a later step, tvc-parity Q10). Parallax terrain and other opaque
    // shaders are not edited: the overlay works on the G-buffer by depth, so it sees whatever they wrote.
    // Surfaces: terrain, scenery and craft hold water differently (WetClass); the G-buffer carries no class, so the overlay uses the
    // terrain's wetness for everything it sees [VERIFY by the player rows; a class mask is the next step].
    [AtmospheresModule(63)]
    internal sealed class WetSurfacesRenderer : IAtmospheresModule
    {
        private const int FailureLimit = 5;
        private const float PauseAfterFailuresS = 5f;
        // Game seconds between samples of the field in normal play, and the most steps of a catch-up in one frame.
        private const double SampleEveryS = 30.0;
        private const int StepsPerFrame = 48;
        // Degrees of latitude and longitude of a site.
        private const double SiteDeg = 0.5;
        private const float WeatherEveryS = 0.25f;

        private WetSurfacesPass pass;
        private Shader shader;
        private CommandBuffer buffer;
        private Camera attachedTo;
        private bool initialised;
        private readonly StandDown standDown = new StandDown(FailureLimit, PauseAfterFailuresS);

        private WetState state;
        private double cursorUt = double.NaN;
        private int siteBody = -1;
        private long siteKey = long.MinValue;
        private double siteLat, siteLon;
        private bool catchingUp;
        private float sun01 = 1f;
        private float cloud01;
        private float weatherAge = 1e9f;
        private float rainLevel;
        private float temperatureK = float.NaN;
        private float freezeK = 273.15f;
        private float snow01;
        private float clock;
        private float[] bandRates;
        private WetnessTuning tuning = WetnessTuning.Defaults();
        private WetDraw draw;
        private bool abOff;

        // The readings of the series and the player.
        internal static WetnessModelReading Last { get; private set; }
        internal static WetSurfacesRenderer Current { get; private set; }
        internal static int DrawnFrames { get; private set; }
        internal static string ActiveMode { get; private set; }

        public void Init()
        {
            Current = this;
            string reason;
            shader = AtmosphereShaders.Get(WetSurfacesPass.ShaderName, out reason);
            if (shader == null)
            {
                AtmospheresLog.WarnOnce("wet surfaces: the shader " + WetSurfacesPass.ShaderName + " is not available (" + reason + "); the wet ground stays off");
                return;
            }
            pass = new WetSurfacesPass(shader);
            buffer = new CommandBuffer { name = "ReDefinition wet surfaces" };
            initialised = true;
            ActiveMode = "none";
            AtmospheresLog.Say("wet surfaces: ready");
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
            ForgetSite();
        }

        public void Shutdown()
        {
            Detach();
            if (buffer != null) buffer.Release();
            buffer = null;
            if (pass != null) pass.Dispose();
            pass = null;
            initialised = false;
            ActiveMode = "none";
            if (Current == this) Current = null;
        }

        internal static WetnessTuning ReadTuning()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            t.WetDiffuse = Tuning.Get("wet.wetDiffuse", t.WetDiffuse);
            t.WetSmooth = Tuning.Get("wet.wetSmooth", t.WetSmooth);
            t.WetPow = Tuning.Get("wet.wetPow", t.WetPow);
            t.PuddleMax = Tuning.Get("wet.puddleMax", t.PuddleMax);
            t.PuddleFullMm = Tuning.Get("wet.puddleFullMm", t.PuddleFullMm);
            t.PuddleDarken = Tuning.Get("wet.puddleDarken", t.PuddleDarken);
            t.PuddleSmooth = Tuning.Get("wet.puddleSmooth", t.PuddleSmooth);
            t.FrozenDarken = Tuning.Get("wet.frozenDarken", t.FrozenDarken);
            t.SnowDamp = Tuning.Get("wet.snowDamp", t.SnowDamp);
            t.EvapK = Tuning.Get("wet.evapK", t.EvapK);
            t.SoilCapMm = Tuning.Get("wet.soilCapMm", t.SoilCapMm);
            t.SoilKsatMmH = Tuning.Get("wet.soilKsatMmH", t.SoilKsatMmH);
            t.PuddleCapMm = Tuning.Get("wet.puddleCapMm", t.PuddleCapMm);
            t.MaxStepS = Tuning.Get("wet.maxStepS", t.MaxStepS);
            t.LookbackH = Tuning.Get("wet.lookbackH", t.LookbackH);
            t.HistoryStepH = Tuning.Get("wet.historyStepH", t.HistoryStepH);
            t.RippleScale = Tuning.Get("wet.rippleScale", t.RippleScale);
            t.SkipBelow = Tuning.Get("wet.skipBelow", t.SkipBelow);
            return t.Clamped();
        }

        public void Tick(float dt)
        {
            if (!initialised) return;
            standDown.Elapse(dt);
            if (standDown.Paused) return;
            try
            {
                Step(dt);
                standDown.Succeeded();
            }
            catch (Exception e)
            {
                bool pause = standDown.Failed();
                AtmospheresLog.Failed("wet-surfaces", "stepping the wet surfaces failed", e);
                Detach();
                if (pause) AtmospheresLog.WarnOnce("wet surfaces: failed " + FailureLimit + " frames in a row; they stand down for " + PauseAfterFailuresS + " s");
            }
        }

        private void ForgetSite()
        {
            state = default(WetState);
            cursorUt = double.NaN;
            siteKey = long.MinValue;
            catchingUp = false;
        }

        private void Detach()
        {
            if (attachedTo != null && buffer != null) attachedTo.RemoveCommandBuffer(CameraEvent.BeforeLighting, buffer);
            attachedTo = null;
            ActiveMode = "none";
        }

        private void Step(float dt)
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool flightScene = HighLogic.LoadedSceneIsFlight && vessel != null && vessel.mainBody != null && flight != null;
            bool on = flightScene && AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects && Tuning.Flag("wet.enabled", true);
            if (!on)
            {
                Detach();
                if (!flightScene) ForgetSite();
                return;
            }
            bool eveRuns = AtmospheresSettings.CloudSource == CloudSourceKind.Eve && !Tuning.Flag("wet.overEve", false);
            if (eveRuns || flight.actualRenderingPath != RenderingPath.DeferredShading)
            {
                ActiveMode = eveRuns ? "off: EVE draws its own wet surfaces" : "off: the camera is not on the deferred path";
                Detach();
                return;
            }

            CelestialBody body = vessel.mainBody;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null) { Detach(); return; }
            tuning = ReadTuning();
            bandRates = climate.Precip.BandRatesMmPerH;
            freezeK = 273.15f;

            Vector3d camWorld = flight.transform.position;
            double lat = body.GetLatitude(camWorld), lon = body.GetLongitude(camWorld), alt = body.GetAltitude(camWorld);
            double ut = Planetarium.GetUniversalTime();

            // The sun's share and the cloud now; the weather of the camera a few times a second.
            weatherAge += Time.unscaledDeltaTime;
            if (weatherAge >= WeatherEveryS)
            {
                weatherAge = 0f;
                CelestialBody sunBody = GameGlobals.Sun;
                if (sunBody != null)
                {
                    Vector3d toSun = (sunBody.position - camWorld).normalized;
                    Vector3d up = (camWorld - body.position).normalized;
                    sun01 = Mathf.Clamp01((float)Vector3d.Dot(toSun, up) * 1.4f);
                }
                WeatherSample now;
                if (WeatherHub.Sample(body.flightGlobalsIndex, lat, lon, alt, ut, out now))
                {
                    cloud01 = now.CloudCover01;
                    rainLevel = (now.Type == PrecipType.Rain || now.Type == PrecipType.Sleet || now.Type == PrecipType.FreezingRain || now.Type == PrecipType.Methane) ? now.Level01 : 0f;
                    temperatureK = now.TemperatureK;
                }
                CoverBodyState cover = CoverBodies.Of(body);
                snow01 = cover != null && cover.Def != null && cover.Map.Current != null ? (float)Math.Min(1.0, cover.Map.Current.Sample01(lat, lon) * 2.0) : 0f;
            }

            // The site: a new one starts the catch-up from the lookback.
            long key = (long)Math.Floor(lat / SiteDeg) * 100003L + (long)Math.Floor(lon / SiteDeg) + body.flightGlobalsIndex * 7919L * 1000003L;
            double lookback = tuning.LookbackH * 3600.0;
            if (key != siteKey || double.IsNaN(cursorUt) || ut < cursorUt - 1.0 || ut - cursorUt > lookback)
            {
                state = default(WetState);
                cursorUt = ut - lookback;
                siteKey = key;
                siteBody = body.flightGlobalsIndex;
                siteLat = (Math.Floor(lat / SiteDeg) + 0.5) * SiteDeg;
                siteLon = (Math.Floor(lon / SiteDeg) + 0.5) * SiteDeg;
                catchingUp = true;
            }
            AdvanceState(body, ut);

            WetLook look = WetnessLookMath.Eval(WetnessModel.Saturation(ref state, WetClass.Terrain, ref tuning), state.StandingMm, rainLevel, temperatureK, freezeK, snow01, ref tuning);
            Last = new WetnessModelReading { StoredMm = state.StoredMm, StandingMm = state.StandingMm, Look = look, CatchingUp = catchingUp, Sun01 = sun01 };

            abOff = Tuning.Flag("wet.abOff", false);
            clock += Time.timeScale > 0f ? Mathf.Min(Time.unscaledDeltaTime, 0.25f) : 0f;
            if (clock > 3600f) clock -= 3600f;
            BodyFrame basis = default(BodyFrame);
            BodyFrame.Compute(body, camWorld, ref basis);
            double radius = body.Radius + Math.Max(alt, 0.0);
            draw = new WetDraw
            {
                Look = look,
                Tuning = tuning,
                CentreRel = (Vector3)(body.position - camWorld),
                East = (Vector3)basis.East,
                North = (Vector3)basis.North,
                EastM = Wrap(lon * Math.PI / 180.0 * body.Radius * Math.Cos(lat * Math.PI / 180.0)),
                NorthM = Wrap(lat * Math.PI / 180.0 * body.Radius),
                ClockS = clock,
                Strength = abOff ? 0f : 1f,
                NoiseScale = 0f
            };
            if (!WetSurfacesPass.NeedsDraw(ref draw))
            {
                Detach();
                ActiveMode = abOff ? "off: wet.abOff" : "idle: dry";
                return;
            }
            if (attachedTo != flight)
            {
                Detach();
                flight.AddCommandBuffer(CameraEvent.BeforeLighting, buffer);
                attachedTo = flight;
            }
            buffer.Clear();
            pass.RecordGBuffer(buffer, ref draw);
            ActiveMode = "gbuffer";
            DrawnFrames++;
        }

        private static float Wrap(double metres)
        {
            double w = metres - WetSurfacesPass.WrapM * Math.Floor(metres / WetSurfacesPass.WrapM);
            return (float)w;
        }

        // Moves the state from the cursor to now: a catch-up of at most StepsPerFrame history steps per frame, then one step per
        // SampleEveryS of game time.
        private void AdvanceState(CelestialBody body, double ut)
        {
            int budget = StepsPerFrame;
            double historyS = tuning.HistoryStepH * 3600.0;
            while (cursorUt < ut && budget > 0)
            {
                double span = ut - cursorUt;
                double stepS = catchingUp ? Math.Min(historyS, span) : span;
                if (!catchingUp && span < SampleEveryS) break;
                if (!catchingUp && span > historyS) stepS = Math.Min(historyS, span);
                WetInput w = WeatherAt(body, cursorUt + 0.5 * stepS);
                WetnessModel.Step(ref state, ref w, WetClass.Terrain, ref tuning, (float)stepS);
                cursorUt += stepS;
                budget--;
            }
            if (catchingUp && ut - cursorUt < 1.0) catchingUp = false;
        }

        // The weather at the site at a UT as the model reads it.
        private WetInput WeatherAt(CelestialBody body, double ut)
        {
            var w = new WetInput { TemperatureK = float.NaN, FreezeK = freezeK, Humidity01 = 0.6f, Sun01 = sun01 };
            WeatherSample s;
            if (!WeatherHub.Sample(siteBody, siteLat, siteLon, 0.0, ut, out s)) return w;
            float rate = PrecipRate.FromLevel(s.Level01, bandRates);
            w.TemperatureK = s.TemperatureK;
            w.WaterMmH = WetnessModel.Intake(s.Type, s.RainFraction, s.SleetFraction, s.SnowFraction, rate, s.TemperatureK, freezeK);
            w.WindMs = (float)Math.Sqrt(s.WindWorld.x * s.WindWorld.x + s.WindWorld.y * s.WindWorld.y);
            w.Humidity01 = s.Level01 > PrecipScale.FlurriesFrom ? 0.95f : Mathf.Clamp01(0.45f + 0.4f * s.CloudCover01);
            return w;
        }

        // What the player and the series read.
        internal struct WetnessModelReading
        {
            internal float StoredMm, StandingMm, Sun01;
            internal WetLook Look;
            internal bool CatchingUp;
        }
    }
}
