using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AtmoSim.Core;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The maps of a body that the field hands to its consumers (the cloud-map painter, the orbit view, the ground and ice layers). They are
    // point evaluations of the generator on an equirectangular grid, made on a worker thread of their own, never on the main thread: a
    // reader asks (GetMap), the worker renders the body for the time it was asked about, and the reader copies the finished snapshot (two
    // buffers per body, so a read never sees a half-written map and takes about as long as a copy of 2 MB). The generator is continuous in
    // the UT, so a map changes smoothly by itself; the limit on how fast a texel may move on screen is the painter's (MapSlew).
    //
    // Weather maps (coverage, cloud type, precipitation level, humidity, surface temperature, wind) are 512 x 256 and at most every
    // MinIntervalMs of real time; the surface maps (snow, ice fraction, ice thickness) are 256 x 128, made from a coarse grid of sites
    // (SiteWidth x SiteHeight, a few kilobytes of window each) every CoverIntervalMs of real time or after a jump of the UT. The surface
    // maps are interpolated bilinearly from the sites (about 5.6 degrees apart), not point evaluations, and a texel whose four neighbouring
    // sites hold none of its own kind (land for snow, sea for ice) gets 0: a small island or a narrow coast can lose its snow or ice.
    //
    // The Type map holds the precipitation band index (PrecipScale.BandOf of the level and the wind, as a float), not an index of a cloud type
    // of the body's configuration. Mapping the bands or the cloud-water and uplift fields to cloud types is the cloud-map painter's (C23).
    internal sealed class GeneratorMaps
    {
        internal const int Width = 512, Height = 256;
        internal const int CoverWidth = 256, CoverHeight = 128;
        internal const int SiteWidth = 64, SiteHeight = 32;
        // The shortest real time between two renders of the weather maps of a body, and of its surface maps.
        internal const int MinIntervalMs = 250, CoverIntervalMs = 30000;
        // A change of the UT by more than this makes the surface maps due at once (a jump, a long warp step).
        internal const double CoverJumpS = 86400.0;
        // A body that has not been asked for this long is not rendered any more.
        internal const int IdleMs = 10000;

        internal sealed class Weather2D
        {
            internal double Ut;
            internal long Version;
            internal readonly float[] Coverage = new float[Width * Height];
            internal readonly float[] Type = new float[Width * Height];
            internal readonly float[] Precipitation = new float[Width * Height];
            internal readonly float[] Humidity = new float[Width * Height];
            internal readonly float[] SurfaceTemperature = new float[Width * Height];
            internal readonly float[] WindSpeed = new float[Width * Height];
        }

        internal sealed class Cover2D
        {
            internal double Ut;
            internal long Version;
            internal readonly float[] Snow = new float[CoverWidth * CoverHeight];
            internal readonly float[] IceFraction = new float[CoverWidth * CoverHeight];
            internal readonly float[] IceThickness = new float[CoverWidth * CoverHeight];
        }

        // The coverage map of one cloud layer at a height (package E43, layerWindSteering MapsOnly): the layer lookup of the core (SubGrid.LayerMap), made on the
        // worker like the weather maps, at most every LayerIntervalMs of real time (the layer map moves with the wind of its height, 20 m/s is 20 m a second against
        // texels of 78 km). Layers are keyed by their height in LayerStepM steps; a layer not asked for for IdleMs is dropped.
        internal const int LayerIntervalMs = 1000;
        internal const double LayerStepM = 250.0;

        private sealed class LayerSnap
        {
            internal float[] Current, Spare = new float[Width * Height];
            internal double Ut, HeightM, WantedUt = double.NaN;
            internal long Version, LastAskedMs, LastRenderMs = long.MinValue / 2;
            internal LayerOptions Options;
        }

        private sealed class Body
        {
            internal readonly object Lock = new object();
            // Held for the whole render of a layer map of the body.
            internal readonly object LayerRender = new object();
            internal readonly Dictionary<int, LayerSnap> Layers = new Dictionary<int, LayerSnap>();
            // Held for the whole render of the weather maps / the surface maps of the body (see RenderNow).
            internal readonly object WeatherRender = new object(), CoverRender = new object();
            internal Weather2D Current, Spare = new Weather2D();
            internal Cover2D CoverCurrent, CoverSpare = new Cover2D();
            internal double WantedUt = double.NaN;
            internal bool WantsCover;
            internal long LastAskedMs, LastWeatherMs = long.MinValue / 2, LastCoverMs = long.MinValue / 2;
            internal SurfaceSite[] Sites;
            internal bool[] SiteSea;
            internal float[] SurfaceMap;
        }

        // The Surface map (WeatherMapKind.Surface) of a body from its height grid: 0 over sea, 0.5 on lowland, 1 where the terrain stands 3000 m or more above the sea; the coast is the
        // ocean fraction's blend. The grid does not change over time, so the map is made once.
        internal static float[] BuildSurfaceMap(HeightGrid grid)
        {
            var map = new float[Width * Height];
            if (grid == null)
            {
                for (int k = 0; k < map.Length; k++) map[k] = 0.5f;
                return map;
            }
            for (int j = 0; j < Height; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / Height) * (Math.PI / 180.0);
                for (int i = 0; i < Width; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / Width) * (Math.PI / 180.0);
                    double land = 1.0 - grid.OceanAt(lat, lon);
                    double relief = Math.Min(1.0, Math.Max(0.0, grid.TerrainAt(lat, lon) / 3000.0));
                    map[j * Width + i] = (float)(land * (0.5 + 0.5 * relief));
                }
            }
            return map;
        }

        private readonly GeneratorField field;
        private readonly GeneratorSurface surface;
        private readonly object gate = new object();
        private readonly Dictionary<int, Body> bodies = new Dictionary<int, Body>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread worker;
        private volatile bool stop;

        // True: a read renders inline when it has no snapshot (tests and probes; never on the game's main thread).
        internal bool Synchronous;

        internal GeneratorMaps(GeneratorField field, GeneratorSurface surface)
        {
            this.field = field;
            this.surface = surface;
        }

        // ---- the reader ---------------------------------------------------------------------------------------------------

        internal bool Get(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            if (target == null) return false;
            GeneratorState state = field.StateOf(bodyIndex);
            if (state == null || state.Broken || double.IsNaN(ut) || double.IsInfinity(ut)) return false;
            bool surfaceMap = kind == WeatherMapKind.SnowCover || kind == WeatherMapKind.IceFraction || kind == WeatherMapKind.IceThickness;
            Body b = BodyOf(bodyIndex);
            if (kind == WeatherMapKind.Surface)
            {
                lock (b.Lock)
                {
                    if (b.SurfaceMap == null) b.SurfaceMap = BuildSurfaceMap(state.Generator.Grid);
                    Hand(target, kind, bodyIndex, ut, 0, Width, Height, b.SurfaceMap);
                    return true;
                }
            }
            lock (b.Lock)
            {
                b.WantedUt = ut;
                b.LastAskedMs = clock.ElapsedMilliseconds;
                if (surfaceMap) b.WantsCover = true;
            }
            wake.Set();
            if (Synchronous && (surfaceMap ? b.CoverCurrent == null : b.Current == null))
            {
                if (surfaceMap) RenderCoverNow(bodyIndex, ut);
                else RenderNow(bodyIndex, ut);
            }
            lock (b.Lock)
            {
                if (surfaceMap)
                {
                    Cover2D c = b.CoverCurrent;
                    if (c == null) return false;
                    float[] source = kind == WeatherMapKind.SnowCover ? c.Snow : kind == WeatherMapKind.IceFraction ? c.IceFraction : c.IceThickness;
                    Hand(target, kind, bodyIndex, c.Ut, c.Version, CoverWidth, CoverHeight, source);
                    return true;
                }
                Weather2D w = b.Current;
                if (w == null) return false;
                float[] values;
                switch (kind)
                {
                    case WeatherMapKind.Coverage: values = w.Coverage; break;
                    case WeatherMapKind.Type: values = w.Type; break;
                    case WeatherMapKind.Precipitation: values = w.Precipitation; break;
                    case WeatherMapKind.Humidity: values = w.Humidity; break;
                    case WeatherMapKind.SurfaceTemperature: values = w.SurfaceTemperature; break;
                    case WeatherMapKind.WindSpeed: values = w.WindSpeed; break;
                    default: return false;
                }
                Hand(target, kind, bodyIndex, w.Ut, w.Version, Width, Height, values);
                return true;
            }
        }

        // The coverage map of the cloud layer at a height (E43): the last finished render of the layer, asked for the time given. The switches of the layered
        // clouds are read here, on the caller's thread, and travel with the request. False until the first render of the layer is there.
        internal bool GetLayer(int bodyIndex, double ut, double heightM, WeatherMap target)
        {
            if (target == null || double.IsNaN(heightM) || double.IsInfinity(heightM) || double.IsNaN(ut) || double.IsInfinity(ut)) return false;
            GeneratorState state = field.StateOf(bodyIndex);
            if (state == null || state.Broken) return false;
            int key = (int)Math.Round(Math.Max(0.0, heightM) / LayerStepM);
            LayerOptions options = LayerWindOptions.Layers();
            Body b = BodyOf(bodyIndex);
            lock (b.Lock)
            {
                LayerSnap snap;
                if (!b.Layers.TryGetValue(key, out snap))
                {
                    snap = new LayerSnap();
                    b.Layers[key] = snap;
                }
                snap.WantedUt = ut;
                snap.HeightM = key * LayerStepM;
                snap.Options = options;
                snap.LastAskedMs = clock.ElapsedMilliseconds;
            }
            wake.Set();
            if (Synchronous)
            {
                bool missing;
                lock (b.Lock)
                {
                    missing = b.Layers[key].Current == null;
                }
                if (missing) RenderLayerNow(bodyIndex, key);
            }
            lock (b.Lock)
            {
                LayerSnap snap = b.Layers[key];
                if (snap.Current == null) return false;
                Hand(target, WeatherMapKind.Coverage, bodyIndex, snap.Ut, snap.Version, Width, Height, snap.Current);
                return true;
            }
        }

        // Renders the layer map of a key for the time it was asked about and publishes it. True where a snapshot was made.
        internal bool RenderLayerNow(int bodyIndex, int key)
        {
            GeneratorState s = field.StateOf(bodyIndex);
            if (s == null || s.Broken) return false;
            Body b = BodyOf(bodyIndex);
            lock (b.LayerRender)
            {
                LayerSnap snap;
                float[] target;
                double ut, height;
                LayerOptions options;
                lock (b.Lock)
                {
                    if (!b.Layers.TryGetValue(key, out snap)) return false;
                    target = snap.Spare;
                    ut = snap.WantedUt;
                    height = snap.HeightM;
                    options = snap.Options;
                }
                if (double.IsNaN(ut) || options == null) return false;
                SubGrid.LayerMap(s.Generator, ut, Width, Height, height, options, target);
                for (int i = 0; i < target.Length; i++)
                {
                    if (float.IsNaN(target[i]) || float.IsInfinity(target[i]))
                    {
                        s.Trip();
                        return false;
                    }
                }
                lock (b.Lock)
                {
                    snap.Spare = snap.Current ?? new float[Width * Height];
                    snap.Current = target;
                    snap.Ut = ut;
                    snap.Version = field.Version;
                    snap.LastRenderMs = clock.ElapsedMilliseconds;
                }
                return true;
            }
        }

        private static void Hand(WeatherMap target, WeatherMapKind kind, int bodyIndex, double ut, long version, int width, int height, float[] values)
        {
            target.Resize(width, height);
            Array.Copy(values, target.Values, values.Length);
            target.Kind = kind;
            target.BodyIndex = bodyIndex;
            target.Ut = ut;
            target.Version = version;
            target.Readiness = FieldReadiness.Ready;
            target.Approximate = false;
        }

        // Forgets the snapshots and sites of a body (its definition changed).
        internal void Forget(int bodyIndex)
        {
            lock (gate)
            {
                bodies.Remove(bodyIndex);
            }
        }

        private Body BodyOf(int bodyIndex)
        {
            lock (gate)
            {
                Body b;
                if (!bodies.TryGetValue(bodyIndex, out b))
                {
                    b = new Body();
                    bodies[bodyIndex] = b;
                }
                return b;
            }
        }

        // ---- the worker ---------------------------------------------------------------------------------------------------

        internal void Start()
        {
            if (worker != null) return;
            stop = false;
            worker = new Thread(Run) { IsBackground = true, Name = "ReDefinition generator maps", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        internal void Stop()
        {
            stop = true;
            wake.Set();
            Thread t = worker;
            worker = null;
            if (t != null) t.Join(2000);
        }

        private void Run()
        {
            while (!stop)
            {
                wake.WaitOne(100);
                if (stop) return;
                try
                {
                    Pass();
                }
                catch (Exception e)
                {
                    AtmospheresLog.Warn("generator", "the map worker failed (" + e.GetType().Name + ": " + e.Message + "); it goes on");
                    Thread.Sleep(1000);
                }
            }
        }

        // One look at every body that was asked about: renders what is due.
        internal void Pass()
        {
            int[] indices;
            lock (gate)
            {
                indices = new int[bodies.Count];
                bodies.Keys.CopyTo(indices, 0);
            }
            foreach (int index in indices)
            {
                Body b;
                double ut;
                bool cover;
                long now = clock.ElapsedMilliseconds;
                lock (gate)
                {
                    if (!bodies.TryGetValue(index, out b)) continue;
                }
                lock (b.Lock)
                {
                    ut = b.WantedUt;
                    cover = b.WantsCover;
                    if (double.IsNaN(ut) || now - b.LastAskedMs > IdleMs) continue;
                }
                bool weatherDue = now - b.LastWeatherMs >= MinIntervalMs && (b.Current == null || b.Current.Ut != ut);
                if (weatherDue) RenderNow(index, ut);
                bool coverDue = cover && (b.CoverCurrent == null || now - b.LastCoverMs >= CoverIntervalMs || Math.Abs(ut - b.CoverCurrent.Ut) > CoverJumpS);
                if (coverDue) RenderCoverNow(index, ut);
                RenderDueLayers(index, b);
            }
        }

        // The layer maps (E43) of a body that were asked about and are due; a layer nobody asks for any more is dropped.
        private void RenderDueLayers(int bodyIndex, Body b)
        {
            List<int> due = null;
            long now = clock.ElapsedMilliseconds;
            lock (b.Lock)
            {
                List<int> idle = null;
                foreach (KeyValuePair<int, LayerSnap> pair in b.Layers)
                {
                    LayerSnap snap = pair.Value;
                    if (now - snap.LastAskedMs > IdleMs)
                    {
                        if (idle == null) idle = new List<int>();
                        idle.Add(pair.Key);
                        continue;
                    }
                    if (now - snap.LastRenderMs >= LayerIntervalMs && (snap.Current == null || snap.Ut != snap.WantedUt))
                    {
                        if (due == null) due = new List<int>();
                        due.Add(pair.Key);
                    }
                }
                if (idle != null)
                {
                    foreach (int key in idle) b.Layers.Remove(key);
                }
            }
            if (due == null) return;
            foreach (int key in due) RenderLayerNow(bodyIndex, key);
        }

        // ---- the render ---------------------------------------------------------------------------------------------------

        // Renders the weather maps of a body for a time and publishes them. True where a snapshot was made.
        internal bool RenderNow(int bodyIndex, double ut)
        {
            GeneratorState s = field.StateOf(bodyIndex);
            if (s == null || s.Broken) return false;
            Body b = BodyOf(bodyIndex);
            // One render of a body's weather maps at a time: two callers (the worker and a Synchronous read) would take the same spare
            // buffer, and the second swap would leave Spare and Current as the same buffer, which a later render writes while a reader copies it.
            lock (b.WeatherRender)
            {
                Weather2D target;
                lock (b.Lock)
                {
                    target = b.Spare;
                }
                lock (target)
                {
                    if (!RenderWeather(s, ut, target)) return false;
                    target.Version = field.Version;
                }
                lock (b.Lock)
                {
                    b.Spare = b.Current ?? new Weather2D();
                    b.Current = target;
                    b.LastWeatherMs = clock.ElapsedMilliseconds;
                }
                return true;
            }
        }

        // The weather maps of a state for a time, into a snapshot. False where a value was not finite (the guard of the state tripped).
        internal static bool RenderWeather(GeneratorState s, double ut, Weather2D into)
        {
            WeatherGenerator g = s.Generator;
            float[] bands = s.Bands;
            Weather w;
            for (int j = 0; j < Height; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / Height) * (Math.PI / 180.0);
                for (int i = 0; i < Width; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / Width) * (Math.PI / 180.0);
                    g.Sample(lat, lon, 0.0, ut, out w);
                    if (!GeneratorState.Finite(ref w))
                    {
                        s.Trip();
                        return false;
                    }
                    int k = j * Width + i;
                    float level = PrecipScale.LevelFromRate((float)w.PrecipMmH, bands);
                    into.Coverage[k] = (float)w.Coverage;
                    into.Precipitation[k] = level;
                    into.Type[k] = (float)(int)PrecipScale.BandOf(level, (float)w.WindSpeed);
                    into.Humidity[k] = (float)w.Humidity;
                    into.SurfaceTemperature[k] = (float)w.SurfaceTempK;
                    into.WindSpeed[k] = (float)w.WindSpeed;
                }
            }
            into.Ut = ut;
            return true;
        }

        // Renders the surface maps of a body for a time and publishes them.
        internal bool RenderCoverNow(int bodyIndex, double ut)
        {
            GeneratorState s = field.StateOf(bodyIndex);
            if (s == null || s.Broken) return false;
            Body b = BodyOf(bodyIndex);
            // Serialised per body like RenderNow (the site arrays of the body are built in the first render as well).
            lock (b.CoverRender)
            {
                Cover2D target;
                lock (b.Lock)
                {
                    target = b.CoverSpare;
                }
                lock (target)
                {
                    if (!RenderCover(b, s, ut, target)) return false;
                    target.Version = field.Version;
                }
                lock (b.Lock)
                {
                    b.CoverSpare = b.CoverCurrent ?? new Cover2D();
                    b.CoverCurrent = target;
                    b.LastCoverMs = clock.ElapsedMilliseconds;
                }
                return true;
            }
        }

        private bool RenderCover(Body b, GeneratorState s, double ut, Cover2D into)
        {
            if (b.Sites == null)
            {
                var sites = new SurfaceSite[SiteWidth * SiteHeight];
                var sea = new bool[sites.Length];
                for (int j = 0; j < SiteHeight; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / SiteHeight) * (Math.PI / 180.0);
                    for (int i = 0; i < SiteWidth; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / SiteWidth) * (Math.PI / 180.0);
                        sites[j * SiteWidth + i] = new SurfaceSite(s.Generator, lat, lon);
                        sea[j * SiteWidth + i] = sites[j * SiteWidth + i].IsSea;
                    }
                }
                b.Sites = sites;
                b.SiteSea = sea;
            }
            var snow = new float[b.Sites.Length];
            var iceFraction = new float[b.Sites.Length];
            var iceThickness = new float[b.Sites.Length];
            bool dusty = s.Parameters.Dusty;
            for (int k = 0; k < b.Sites.Length; k++)
            {
                SurfaceState st = b.Sites[k].Evaluate(ut);
                if (!(IsFinite(st.SnowDepthM) && IsFinite(st.IceThicknessM) && IsFinite(st.IceFraction) && IsFinite(st.DustDepositMm)))
                {
                    s.Trip();
                    return false;
                }
                snow[k] = (float)(dusty ? st.DustDepositMm * 0.001 : st.SnowDepthM);
                iceFraction[k] = (float)st.IceFraction;
                iceThickness[k] = (float)st.IceThicknessM;
            }
            HeightGrid grid = s.Generator.Grid;
            for (int j = 0; j < CoverHeight; j++)
            {
                double latDeg = 90.0 - (j + 0.5) * 180.0 / CoverHeight;
                double fy = (90.0 - latDeg) / 180.0 * SiteHeight - 0.5;
                for (int i = 0; i < CoverWidth; i++)
                {
                    double lonDeg = -180.0 + (i + 0.5) * 360.0 / CoverWidth;
                    bool texelSea = grid.OceanAt(latDeg * (Math.PI / 180.0), lonDeg * (Math.PI / 180.0)) >= 0.5;
                    double fx = (lonDeg + 180.0) / 360.0 * SiteWidth - 0.5;
                    Upsample(b, snow, iceFraction, iceThickness, fx, fy, texelSea, into, j * CoverWidth + i);
                }
            }
            into.Ut = ut;
            return true;
        }

        // The value of a texel interpolated from the four surrounding sites of the same kind (land for snow, sea for ice), 0 where there is none.
        private static void Upsample(Body b, float[] snow, float[] iceFraction, float[] iceThickness, double fx, double fy, bool texelSea, Cover2D into, int at)
        {
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            double sumW = 0.0, vSnow = 0.0, vFrac = 0.0, vThick = 0.0;
            for (int dy = 0; dy < 2; dy++)
            {
                int y = Math.Max(0, Math.Min(SiteHeight - 1, y0 + dy));
                double wy = dy == 0 ? 1.0 - ty : ty;
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = ((x0 + dx) % SiteWidth + SiteWidth) % SiteWidth;
                    int k = y * SiteWidth + x;
                    if (b.SiteSea[k] != texelSea) continue;
                    double w = wy * (dx == 0 ? 1.0 - tx : tx);
                    sumW += w;
                    vSnow += w * snow[k];
                    vFrac += w * iceFraction[k];
                    vThick += w * iceThickness[k];
                }
            }
            if (sumW <= 0.0)
            {
                into.Snow[at] = 0f;
                into.IceFraction[at] = 0f;
                into.IceThickness[at] = 0f;
                return;
            }
            into.Snow[at] = texelSea ? 0f : (float)(vSnow / sumW);
            into.IceFraction[at] = texelSea ? (float)(vFrac / sumW) : 0f;
            into.IceThickness[at] = texelSea ? (float)(vThick / sumW) : 0f;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
