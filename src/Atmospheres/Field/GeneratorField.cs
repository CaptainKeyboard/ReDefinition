using System;
using System.Collections.Generic;
using System.Threading;
using AtmoSim.Core;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The weather field of the atmospheres: the synoptic weather generator behind IWeatherField. A point answer is a PURE function of
    // (body, seed, position, altitude, UT): no state, no checkpoint, no side file, no catch-up. A pause, a warp, a quickload, a revert, a
    // jump of a year, the forecast (the same call at a later UT), the order of the calls and the thread that makes them cannot change an
    // answer, so ThreadSafe is true. The generator core (AtmoSim.Core, the source of tools/atmosphere-sim) is compiled into this
    // assembly as it is; GeneratorBodies maps a body to its parameter set. Temperature is the generator's own, anchored to the game's
    // temperature curves by the fit of ThermalFit and not mixed with the game's thermal system.
    //
    // A guard watches every answer: a value that is not finite freezes that body to its climatology (Readiness Broken, one log line).
    //
    // Determinism. The core answers with no platform transcendental (DMath for sin, cos, exp, asin, atan2), so the same inputs give the same
    // bits on every machine and runtime. The inputs of a body are made from the game's float curves (ThermalFit, GeneratorBodies) and from the
    // forced discs; both use DMath too. A different mod set is a different definition on purpose (DefinitionId). The golden values of
    // tests/data/generator are checked under net48 (unit tests) and under Unity's Mono (weather player, series player-C25, scene
    // generator-golden).
    //
    // Known limits (tuning round 2 of the generator, tools/atmosphere-sim/RESULT.md, decision there: REWORK with open GATE items; this
    // field is built on that state): front events in the south are rare (0.19 per 10 days, the south lifetime is 42 h instead of 48 h and the
    // north drift 12.37 m/s is marginal); a remainder of the comb placement of the systems shows as several similar diagonal streaks per
    // hemisphere in some frames, and a fan of radial streaks at the south pole; the patches of the moist belt are individually pill-shaped
    // and can read as beads on a string; summer snow at 55 degrees N latitude on high terrain is 0.11 (limit 0.05). Rasters, and with them
    // states, exist only for bodies whose terrain sphere runs (GeneratorRaster): a body not visited yet answers Sample with false. Cloud
    // types are not in the field: the Type map holds the precipitation band (see GeneratorMaps).
    internal sealed class GeneratorField : IWeatherField, ISurfaceForcing, IWeatherSystemSource, IWindowJobField
    {
        // The step of the version, in seconds of game time: the version rises when the clock moves into a new step that was observed.
        internal const double VersionStepSeconds = 60.0;
        // The vertical optical depth of a column per unit of the cloud-water proxy of the generator (0 to 1.5). An overcast stratus is
        // about 10 to 20. [VERIFY with C24 and E40: the sun transmission of a heavy deck.]
        internal const float OpticalDepthPerCloudWater = 12f;
        // The grams of cloud water per cubic metre that one unit of the generator's cloud density stands for in the 3D window. [VERIFY C21.]
        internal const float WindowGramsPerDensity = 0.7f;
        // The updraft in m/s of a convective cell at full strength in the 3D window.
        internal const float WindowCellUpdraftMs = 6f;

        // The size of the 3D window: cells east, north and up.
        internal const int WindowNx = 32, WindowNy = 32, WindowNz = 24;

        private struct Disc
        {
            internal int Body;
            internal double LatRad, LonRad, RadiusM;
            internal float RateMmPerH;
            internal PrecipType Type;
            internal double FromUt, ToUt;
        }

        private readonly object gate = new object();
        private GeneratorState[] states = new GeneratorState[0];
        private readonly HashSet<int> brokenBodies = new HashSet<int>();
        private Disc[] discs = new Disc[0];
        private long version;
        private long stepBucket = long.MinValue;
        private long definitionId;
        private float lightKnob = 1f, heavyKnob = 1f;
        private readonly GeneratorSurface surface;
        private readonly GeneratorMaps maps;

        // Where the log lines go; tests replace it.
        internal Action<string> Say = AtmospheresLog.Say;

        internal GeneratorField()
        {
            surface = new GeneratorSurface(this);
            maps = new GeneratorMaps(this, surface);
            definitionId = ComputeDefinition();
        }

        internal GeneratorMaps Maps
        {
            get { return maps; }
        }

        internal GeneratorSurface Surface
        {
            get { return surface; }
        }

        // ---- the bodies ---------------------------------------------------------------------------------------------------

        // The state of a body, or null where the field has none (its raster is not read yet, the generator is off for it, it has no atmosphere).
        internal GeneratorState StateOf(int bodyIndex)
        {
            GeneratorState[] list = Volatile.Read(ref states);
            return bodyIndex >= 0 && bodyIndex < list.Length ? list[bodyIndex] : null;
        }

        // The bodies that have a state, in the order of their index.
        internal List<GeneratorState> AllStates()
        {
            var result = new List<GeneratorState>();
            foreach (GeneratorState s in Volatile.Read(ref states))
            {
                if (s != null) result.Add(s);
            }
            return result;
        }

        // Puts the state of a body in (replacing the one it had); the knobs of the player are applied to it first.
        internal void SetState(GeneratorState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            state.ApplyKnobs(lightKnob, heavyKnob);
            lock (gate)
            {
                GeneratorState[] old = states;
                var next = new GeneratorState[Math.Max(old.Length, state.BodyIndex + 1)];
                Array.Copy(old, next, old.Length);
                next[state.BodyIndex] = state;
                brokenBodies.Remove(state.BodyIndex);
                Volatile.Write(ref states, next);
                Changed();
            }
            surface.Forget(state.BodyIndex);
            maps.Forget(state.BodyIndex);
        }

        // Takes a body out (the generator is off for it).
        internal void RemoveState(int bodyIndex)
        {
            lock (gate)
            {
                GeneratorState[] old = states;
                if (bodyIndex < 0 || bodyIndex >= old.Length || old[bodyIndex] == null) return;
                var next = (GeneratorState[])old.Clone();
                next[bodyIndex] = null;
                Volatile.Write(ref states, next);
                Changed();
            }
            surface.Forget(bodyIndex);
            maps.Forget(bodyIndex);
        }

        // A body whose parameters could not make a generator: it has no weather, and says Broken.
        internal void MarkBroken(int bodyIndex, string reason)
        {
            bool first;
            lock (gate)
            {
                first = brokenBodies.Add(bodyIndex);
                Changed();
            }
            if (first) Say("generator: body " + bodyIndex + " is frozen to nothing (" + reason + ")");
        }

        internal bool IsBroken(int bodyIndex)
        {
            lock (gate)
            {
                return brokenBodies.Contains(bodyIndex);
            }
        }

        // The player's knobs of the precipitation scale (lightWeatherFrequency, heavyShare) change the bands of every body.
        internal void SetKnobs(float lightWeatherFrequency, float heavyShare)
        {
            if (lightWeatherFrequency == lightKnob && heavyShare == heavyKnob) return;
            lock (gate)
            {
                lightKnob = lightWeatherFrequency;
                heavyKnob = heavyShare;
                foreach (GeneratorState s in states)
                {
                    if (s != null) s.ApplyKnobs(lightKnob, heavyKnob);
                }
                Changed();
            }
        }

        // The clock moved: the version rises when the game time enters a new step.
        internal void Observe(double ut)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut)) return;
            long now = (long)Math.Floor(ut / VersionStepSeconds);
            if (now == Interlocked.Exchange(ref stepBucket, now)) return;
            Interlocked.Increment(ref version);
        }

        private void Changed()
        {
            definitionId = ComputeDefinition();
            Interlocked.Increment(ref version);
        }

        private long ComputeDefinition()
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (GeneratorState s in states)
            {
                if (s == null) continue;
                h = Hash.Mix(h ^ (ulong)s.BodyIndex);
                h = Hash.Mix(h ^ (ulong)s.DefinitionId);
            }
            h = Hash.Fold(h, lightKnob);
            h = Hash.Fold(h, heavyKnob);
            foreach (Disc d in discs)
            {
                h = Hash.Fold(h, d.LatRad);
                h = Hash.Fold(h, d.LonRad);
                h = Hash.Fold(h, d.RateMmPerH);
                h = Hash.Fold(h, d.FromUt);
                h = Hash.Fold(h, d.ToUt);
            }
            return unchecked((long)h);
        }

        // ---- IWeatherField ------------------------------------------------------------------------------------------------

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            sample = WeatherSample.Clear(bodyIndex);
            GeneratorState s = StateOf(bodyIndex);
            if (s == null)
            {
                if (IsBroken(bodyIndex)) sample.Readiness = FieldReadiness.Broken;
                return false;
            }
            if (!Finite(latDeg) || !Finite(lonDeg) || !Finite(altM) || !Finite(ut)) return false;
            double lat = Math.Max(-90.0, Math.Min(90.0, latDeg)) * (Math.PI / 180.0);
            double lon = DMath.WrapPi(lonDeg * (Math.PI / 180.0));
            double alt = altM < 0.0 ? 0.0 : altM;
            if (s.Broken)
            {
                Frozen(s, lat, lon, ut, ref sample);
                return true;
            }
            Weather w;
            s.Generator.Sample(lat, lon, alt, ut, out w);
            if (!GeneratorState.Finite(ref w))
            {
                Trip(s, ut);
                Frozen(s, lat, lon, ut, ref sample);
                return true;
            }
            Fill(s, ref w, alt, ref sample);
            Disc[] forced = Volatile.Read(ref discs);
            if (forced.Length > 0) ApplyDiscs(s, forced, lat, lon, ut, ref sample);
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            return maps.Get(bodyIndex, kind, ut, target);
        }

        // The coverage map of the cloud layer at a height above the surface (E43): the layer carried by the wind of its height and thinned beneath towers
        // (LayerCoupling.Coverage through SubGrid.LayerMap), 512 x 256 like the weather maps. Not part of IWeatherField; the painter of the layers asks for it
        // where layerWindSteering is MapsOnly. False until the worker has made the layer's first map.
        internal bool GetLayerCoverage(int bodyIndex, double ut, double heightM, WeatherMap target)
        {
            return maps.GetLayer(bodyIndex, ut, heightM, target);
        }

        // The seed of the body's generator (0: no state), so that patterns made for a body differ from seed to seed.
        internal ulong SeedOf(int bodyIndex)
        {
            GeneratorState s = StateOf(bodyIndex);
            return s == null ? 0UL : s.Seed;
        }

        // The angular rate (rad/s, eastward) at which a pattern fixed to the clouds of a layer at a height moves over the ground (E40 L3, the meso pattern of CloudMesoStage): the rate of the
        // generator's own systems at the storm-track latitude plus the layer's rate against them (HeightWind.LayerRate, the same function the layer maps use; windShear off: none).
        // One rate, so the pattern turns rigidly and does not shear into stripes.
        internal double CloudDriftRadS(int bodyIndex, double heightM)
        {
            GeneratorState s = StateOf(bodyIndex);
            if (s == null || s.Broken) return 0.0;
            WeatherGenerator g = s.Generator;
            double la = g.D.JetLat0;
            double r = g.Body.RadiusM * Math.Cos(la);
            double pattern = r > 1.0 ? HeightWind.PatternDriftMs(g.D, g.Body, la) / r : 0.0;
            return pattern + (LayerWindOptions.WindShear ? HeightWind.LayerRate(g.D, g.Body, heightM) : 0.0);
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            GeneratorState s = StateOf(bodyIndex);
            if (s == null) return false;
            bool layered = LayerWindOptions.AnyOn && s.Parameters.HeightWind;
            return FillWindowCore(s, bodyIndex, centreLatDeg, centreLonDeg, radiusKm, ut, target, layered, layered ? LayerWindOptions.Layers() : null);
        }

        private readonly WindowJob windowJob = new WindowJob();

        public WindowJobState WindowState
        {
            get { return windowJob.State; }
        }

        // The window on a worker (IWindowJobField): the same fill as FillWindow with the settings of this call, into the back buffer.
        public bool BeginWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut)
        {
            GeneratorState s = StateOf(bodyIndex);
            if (s == null || s.Broken) return false;
            bool layered = LayerWindOptions.AnyOn && s.Parameters.HeightWind;
            LayerOptions layers = layered ? LayerWindOptions.Layers() : null;
            return windowJob.Begin(target => FillWindowCore(s, bodyIndex, centreLatDeg, centreLonDeg, radiusKm, ut, target, layered, layers));
        }

        public WindowJobState TakeWindow(ref WeatherWindow spare)
        {
            return windowJob.Take(ref spare);
        }

        // The fill of the window with the switches of the layered columns given: the settings are read by the caller, on its own thread, so that
        // a fill on the worker (WindowJob) makes the window the call would have made at the time it was asked for.
        private bool FillWindowCore(GeneratorState s, int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target,
            bool layered, LayerOptions layers)
        {
            if (target == null || s.Broken) return false;
            if (!Finite(centreLatDeg) || !Finite(centreLonDeg) || !Finite(radiusKm) || !Finite(ut)) return false;
            if (!(radiusKm > 0.0)) return false;
            target.Resize(WindowNx, WindowNy, WindowNz);
            double top = Math.Max(1000.0, s.Parameters.TropopauseM);
            double dz = top / WindowNz;
            double dxy = 2000.0 * radiusKm / (WindowNx - 1);
            target.CentreLatDeg = centreLatDeg;
            target.CentreLonDeg = centreLonDeg;
            target.RadiusKm = radiusKm;
            target.SpacingHorizontalM = (float)dxy;
            target.SpacingVerticalM = (float)dz;
            target.OriginAltM = (float)(0.5 * dz);
            target.BodyIndex = bodyIndex;
            target.Ut = ut;
            target.Version = Version;
            target.Readiness = FieldReadiness.Ready;
            target.Approximate = false;

            double a = s.Parameters.RadiusM;
            double centreLat = Math.Max(-89.9, Math.Min(89.9, centreLatDeg)) * (Math.PI / 180.0);
            double centreLon = centreLonDeg * (Math.PI / 180.0);
            double cosC = Math.Max(0.02, DMath.Cos(centreLat));
            double lapse = s.Parameters.LapseKPerM, tropopause = s.Parameters.TropopauseM;
            Weather w;
            for (int j = 0; j < WindowNy; j++)
            {
                double north = (j - 0.5 * (WindowNy - 1)) * dxy;
                double lat = Math.Max(-1.5707, Math.Min(1.5707, centreLat + north / a));
                for (int i = 0; i < WindowNx; i++)
                {
                    double east = (i - 0.5 * (WindowNx - 1)) * dxy;
                    double lon = DMath.WrapPi(centreLon + east / (a * cosC));
                    s.Generator.Sample(lat, lon, 0.0, ut, out w);
                    if (!GeneratorState.Finite(ref w))
                    {
                        Trip(s, ut);
                        return false;
                    }
                    double surfaceK = w.SurfaceTempK;
                    for (int k = 0; k < WindowNz; k++)
                    {
                        double alt = target.OriginAltM + k * dz;
                        int index = (k * WindowNy + j) * WindowNx + i;
                        if (!layered)
                        {
                            target.CloudWater[index] = (float)(w.DensityAt(alt) * WindowGramsPerDensity);
                            target.Updraft[index] = (float)VerticalWindMs(ref w, alt, false);
                        }
                        double drop = lapse * (alt < tropopause ? alt : tropopause);
                        if (drop > 0.4 * surfaceK) drop = 0.4 * surfaceK;
                        target.Temperature[index] = (float)(surfaceK - drop);
                    }
                }
            }
            // E43: with a wind-by-height generator and any of the switches windShear, verticalMotion, anvilTilt on, cloud water and updraft are the layered columns
            // (the deck carried by the wind of its height, towers growing and sinking, the tilt and the consistency rule), looked up in place of the plain ones.
            if (layered)
            {
                WindowAdvection.Refill(target, 0, WindowNy, s.Generator, layers);
                if (!AllFinite(target.CloudWater, target.Count) || !AllFinite(target.Updraft, target.Count))
                {
                    Trip(s, ut);
                    return false;
                }
            }
            return true;
        }

        private static bool AllFinite(float[] values, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) return false;
            }
            return true;
        }

        // The density factor of a convective tower of the plain window at an altitude (0 to the cell strength).
        private static double TowerShare(ref Weather w, double alt)
        {
            return w.CellStrength * DMath.SmoothStep(300.0, 1500.0, alt) * (1.0 - DMath.SmoothStep(w.CellTopM - 1500.0, w.CellTopM, alt));
        }

        // The vertical wind of a point in m/s (WeatherSample.WindWorld.z): with verticalMotion on and a wind-by-height generator the signed rise of the
        // tower at its life phase (LayerCoupling) plus the broad lift of the column, as the layered window has it; else the plain window's updraft.
        internal static double VerticalWindMs(ref Weather w, double altM, bool layered)
        {
            double lift = 0.3 * w.Uplift * DMath.SmoothStep(0.0, 1500.0, altM);
            if (!layered) return TowerShare(ref w, altM) * WindowCellUpdraftMs + lift;
            if (!(w.CellStrength > 0.0)) return lift;
            double top = LayerCoupling.TopNow(ref w, VerticalOptions);
            double rise = LayerCoupling.TopRiseMs(ref w, VerticalOptions);
            return rise * DMath.SmoothStep(0.0, 0.04, w.CellStrength) * DMath.SmoothStep(300.0, 1500.0, altM) * (1.0 - DMath.SmoothStep(top - 300.0, top + 300.0, altM)) + lift;
        }

        private static readonly LayerOptions VerticalOptions = new LayerOptions();

        // The generator is pure: there is nothing to run. (The maps are made on a worker of their own.)
        public void Advance(double budgetMs)
        {
        }

        public long Version
        {
            get { return Interlocked.Read(ref version); }
        }

        public long DefinitionId
        {
            get { return Interlocked.Read(ref definitionId); }
        }

        public bool ThreadSafe
        {
            get { return true; }
        }

        public double StepSeconds
        {
            get { return VersionStepSeconds; }
        }

        // Ready, or Broken where every body that has a state has tripped its guard.
        public FieldReadiness Readiness
        {
            get
            {
                int total = 0, broken = 0;
                foreach (GeneratorState s in Volatile.Read(ref states))
                {
                    if (s == null) continue;
                    total++;
                    if (s.Broken) broken++;
                }
                return total > 0 && broken == total ? FieldReadiness.Broken : FieldReadiness.Ready;
            }
        }

        // ---- the answer ---------------------------------------------------------------------------------------------------

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private void Trip(GeneratorState s, double ut)
        {
            if (s.Trip()) Say("generator: the guard tripped for " + s.Name + " at UT " + ut.ToString("0") + " (a value that is not finite); the body is frozen to its climatology");
        }

        // The answer of a body that tripped its guard: the climatological temperature and nothing falling.
        private static void Frozen(GeneratorState s, double lat, double lon, double ut, ref WeatherSample sample)
        {
            double t = s.Generator.ClimateTemperature(lat, lon, ut);
            sample.TemperatureK = Finite(t) ? (float)t : float.NaN;
            sample.Readiness = FieldReadiness.Broken;
            sample.Approximate = true;
        }

        private static void Fill(GeneratorState s, ref Weather w, double altM, ref WeatherSample sample)
        {
            float level = PrecipScale.LevelFromRate((float)w.PrecipMmH, s.Bands);
            sample.Level01 = level;
            if (level > 0f) SetType(s, ref w, ref sample);
            sample.VisibilityM = level > 0f ? Math.Min(WeatherSample.ClearVisibilityM, PrecipitationLook.Evaluate(level).VisibilityM) : WeatherSample.ClearVisibilityM;
            // The vertical wind is the updraft of the point (E43), so that upwardsCloudSpeed of a TVC layer follows it.
            sample.WindWorld = new Vector3((float)w.WindEast, (float)w.WindNorth, (float)VerticalWindMs(ref w, altM, s.Parameters.HeightWind && LayerWindOptions.VerticalMotion));
            double gust = (w.GustFactor - 1.0) / 0.8;
            sample.GustFactor = (float)(gust < 0.0 ? 0.0 : gust > 1.0 ? 1.0 : gust);
            sample.TemperatureK = (float)w.TempK;
            sample.CloudCover01 = (float)w.Coverage;
            sample.OpticalDepthVertical = (float)(w.CloudWater * OpticalDepthPerCloudWater);
        }

        // What falls and how a mixed fall divides: the wet-bulb shares of the generator; a dusty fall has no water fractions.
        private static void SetType(GeneratorState s, ref Weather w, ref WeatherSample sample)
        {
            switch (w.Type)
            {
                case PrecipKind.Dust:
                    sample.Type = s.DustType != PrecipType.None ? s.DustType : PrecipType.Dust;
                    return;
                case PrecipKind.None:
                    return;
            }
            double sum = w.SnowFrac + w.SleetFrac + w.RainFrac;
            if (!(sum > 0.0))
            {
                sample.Type = w.Type == PrecipKind.Snow ? PrecipType.Snow : w.Type == PrecipKind.Sleet ? PrecipType.Sleet : PrecipType.Rain;
                sample.SnowFraction = sample.Type == PrecipType.Snow ? 1f : 0f;
                sample.SleetFraction = sample.Type == PrecipType.Sleet ? 1f : 0f;
                sample.RainFraction = sample.Type == PrecipType.Rain ? 1f : 0f;
                return;
            }
            sample.SnowFraction = (float)(w.SnowFrac / sum);
            sample.SleetFraction = (float)(w.SleetFrac / sum);
            sample.RainFraction = (float)(w.RainFrac / sum);
            sample.Type = w.Type == PrecipKind.Snow ? PrecipType.Snow : w.Type == PrecipKind.Sleet ? PrecipType.Sleet : PrecipType.Rain;
        }

        // ---- the forcing --------------------------------------------------------------------------------------------------

        public void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt)
        {
            if (!(radiusKm > 0.0) || !(rateMmPerH > 0f) || !(toUt > fromUt)) return;
            lock (gate)
            {
                var next = new Disc[discs.Length + 1];
                Array.Copy(discs, next, discs.Length);
                next[discs.Length] = new Disc
                {
                    Body = bodyIndex, LatRad = latDeg * (Math.PI / 180.0), LonRad = lonDeg * (Math.PI / 180.0), RadiusM = radiusKm * 1000.0,
                    RateMmPerH = rateMmPerH, Type = type, FromUt = fromUt, ToUt = toUt
                };
                Volatile.Write(ref discs, next);
                Changed();
            }
        }

        // Removes every forced disc.
        internal void ClearDiscs()
        {
            lock (gate)
            {
                Volatile.Write(ref discs, new Disc[0]);
                Changed();
            }
        }

        private void ApplyDiscs(GeneratorState s, Disc[] forced, double lat, double lon, double ut, ref WeatherSample sample)
        {
            for (int i = 0; i < forced.Length; i++)
            {
                Disc d = forced[i];
                if (d.Body != s.BodyIndex || ut < d.FromUt || ut > d.ToUt) continue;
                double dLat = lat - d.LatRad, dLon = DMath.WrapPi(lon - d.LonRad) * DMath.Cos(0.5 * (lat + d.LatRad));
                double distance = s.Parameters.RadiusM * Math.Sqrt(dLat * dLat + dLon * dLon);
                if (distance > d.RadiusM) continue;
                float level = PrecipScale.LevelFromRate(d.RateMmPerH, s.Bands);
                if (level <= sample.Level01) continue;
                sample.Level01 = level;
                sample.Type = d.Type;
                sample.SnowFraction = d.Type == PrecipType.Snow ? 1f : 0f;
                sample.SleetFraction = d.Type == PrecipType.Sleet || d.Type == PrecipType.FreezingRain ? 1f : 0f;
                sample.RainFraction = d.Type == PrecipType.Rain ? 1f : 0f;
                sample.CloudCover01 = Math.Max(sample.CloudCover01, 0.95f);
                sample.VisibilityM = Math.Min(sample.VisibilityM, PrecipitationLook.Evaluate(level).VisibilityM);
            }
        }

        // The depth of snow the forced discs of a body have put on a place by a time, in metres: the rate of a snow disc over the hours it
        // fell, 8 times the water equivalent (the generator's density of fresh snow). The forced fall does not melt.
        internal double ForcedSnowDepthM(int bodyIndex, double radius, double lat, double lon, double ut)
        {
            Disc[] forced = Volatile.Read(ref discs);
            double depth = 0.0;
            for (int i = 0; i < forced.Length; i++)
            {
                Disc d = forced[i];
                if (d.Body != bodyIndex || d.Type != PrecipType.Snow || ut <= d.FromUt) continue;
                double dLat = lat - d.LatRad, dLon = DMath.WrapPi(lon - d.LonRad) * DMath.Cos(0.5 * (lat + d.LatRad));
                if (radius * Math.Sqrt(dLat * dLat + dLon * dLon) > d.RadiusM) continue;
                double hours = (Math.Min(ut, d.ToUt) - d.FromUt) / 3600.0;
                depth += d.RateMmPerH * hours * 8.0 / 1000.0;
            }
            return depth;
        }

        // ---- IWeatherSystemSource -----------------------------------------------------------------------------------------

        // The moving systems of the body at a time: one per active slot of either hemisphere, with the identity of its generation.
        public bool TryGetSystems(int bodyIndex, double ut, List<SystemPoint> into)
        {
            GeneratorState s = StateOf(bodyIndex);
            if (s == null || into == null || s.Broken || !Finite(ut)) return false;
            WeatherGenerator g = s.Generator;
            double reference = Math.Max(1.0, g.D.PressureAmpPa * 1.2);
            var buffer = new double[3 * WeatherGenerator.MaxSlots];
            for (int hemisphere = 0; hemisphere < 2; hemisphere++)
            {
                int count = g.ActiveSystems(hemisphere, ut, buffer);
                int found = 0;
                for (int slot = 0; slot < WeatherGenerator.MaxSlots && found < count; slot++)
                {
                    if (!(g.SlotAmplitude(hemisphere, slot, ut) > 0.0)) continue;
                    double life, offset;
                    g.SlotTiming(hemisphere, slot, out life, out offset);
                    long generation = (long)Math.Floor((ut - offset) / life);
                    var point = new SystemPoint();
                    point.LatDeg = buffer[3 * found] * (180.0 / Math.PI);
                    point.LonDeg = buffer[3 * found + 1] * (180.0 / Math.PI);
                    point.RadiusM = (float)g.D.SystemRadiusM;
                    point.Strength01 = (float)Math.Min(1.0, buffer[3 * found + 2] / reference);
                    point.Key = Hash.Of(s.Seed, hemisphere * 64 + slot, generation, 12);
                    into.Add(point);
                    found++;
                }
            }
            return true;
        }
    }
}
