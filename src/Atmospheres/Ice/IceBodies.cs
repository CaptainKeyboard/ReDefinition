using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The sea ice of one body in the game: the parameters from its ICE node, the coast map, the field's maps and the drift. Everything the
    // orbit layer and the ground overlay draw from. Main thread only, except the coast map's worker.
    internal sealed class IceBodyState
    {
        // The seconds of real time between two reads of the field's maps, and between two looks at the wind.
        internal const float MapSeconds = 3f;
        internal const float WindSeconds = 2f;

        internal readonly CelestialBody Body;
        internal readonly int Index;
        internal BodyClimate Climate;
        internal IceParams Params;
        internal readonly IceHeights Heights = new IceHeights();
        internal readonly IceDrift Drift = new IceDrift();
        // The maps of the field as the last read left them (main thread); the orbit worker gets copies.
        internal readonly WeatherMap FractionMap = new WeatherMap();
        internal readonly WeatherMap ThicknessMap = new WeatherMap();
        internal readonly WeatherMap SnowMap = new WeatherMap();
        internal bool MapsValid;
        internal double MapsUt;
        internal long MapsVersion = -1;
        // Rises each time the maps, the drift, the parameters or the coast map changed (the orbit picture is made again after it).
        internal int InputsStamp;

        private float nextMap;
        private float nextWind;
        private KspFrame frame;
        private volatile byte[] shore;
        private volatile string shoreError;
        private bool shoreStarted;
        private long shoreMilliseconds;

        internal IceBodyState(CelestialBody body, BodyClimate climate)
        {
            Body = body;
            Index = body.flightGlobalsIndex;
            Update(climate);
        }

        // The coast map (4 bytes a texel), null until the worker has made it.
        internal byte[] Shore
        {
            get { return shore; }
        }

        internal string ShoreError
        {
            get { return shoreError; }
        }

        internal long ShoreMilliseconds
        {
            get { return shoreMilliseconds; }
        }

        // The height of the sea in metres on the PQS's own scale, and the sea sphere's radius.
        internal float SeaLevelM
        {
            get { return Body.pqsController != null ? (float)Body.pqsController.mapOceanHeight : 0f; }
        }

        internal double SeaRadiusM
        {
            get { return Body.Radius + SeaLevelM; }
        }

        // The latitude / longitude frame, measured on first use from the sphere's directions (the same measurement as the cover's).
        internal KspFrame Frame
        {
            get { return frame ?? (frame = Calibrate()); }
        }

        private KspFrame Calibrate()
        {
            try
            {
                Vector3d a = Body.GetRelSurfaceNVector(0.0, 0.0), b = Body.GetRelSurfaceNVector(0.0, 90.0), p = Body.GetRelSurfaceNVector(90.0, 0.0);
                var f = new KspFrame(a.x, a.y, a.z, b.x, b.y, b.z, p.x, p.y, p.z);
                if (f.Valid) return f;
                AtmospheresLog.WarnOnce("sea ice: the directions of latitude and longitude of " + Body.name + " are not a frame; the sphere's own convention is used.");
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("sea ice: the frame of " + Body.name + " could not be read; the sphere's own convention is used (" + CompatibilityLog.Reason(e) + ")");
            }
            return KspFrame.Sphere;
        }

        // Takes the rules of the climate (a changed climate makes new parameters).
        internal void Update(BodyClimate climate)
        {
            if (ReferenceEquals(Climate, climate) && Params != null) return;
            Climate = climate;
            IceRules rules = climate.Ice;
            Params = new IceParams
            {
                RadiusM = (float)Body.Radius,
                FloeSizeKm = rules.FloeSizeKm,
                LeadFraction = rules.LeadFraction,
                Permanent = rules.Permanent,
                Seed = unchecked((uint)climate.Seed) ^ 0x1CE5EA1Eu
            };
            InputsStamp++;
        }

        // The cell size of the floe pattern in metres.
        internal double CellMetres
        {
            get { return Math.Max(1.0, Params.FloeSizeKm * 1000.0); }
        }

        // Reads the heights a little, and starts the worker that makes the coast map when they are complete.
        internal void StepShore()
        {
            if (shore != null || shoreError != null) return;
            if (!shoreStarted)
            {
                Heights.Sample(Body.pqsController);
                float[] grid = Heights.Grid;
                if (grid == null) return;
                shoreStarted = true;
                float level = SeaLevelM;
                float radius = (float)Body.Radius;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        shore = ShoreGrid.Build(grid, level, radius);
                    }
                    catch (Exception e)
                    {
                        shoreError = "coast map: " + e.GetType().Name + " " + e.Message;
                    }
                    finally
                    {
                        shoreMilliseconds = watch.ElapsedMilliseconds;
                    }
                });
            }
        }

        // Reads the field's maps when they are due; true where they changed.
        internal bool RefreshMaps(float now, double ut)
        {
            if (now < nextMap && MapsValid) return false;
            nextMap = now + MapSeconds;
            IWeatherField field = WeatherHub.Field;
            bool ok = false;
            try
            {
                ok = field.GetMap(Index, WeatherMapKind.IceFraction, ut, FractionMap)
                     && field.GetMap(Index, WeatherMapKind.IceThickness, ut, ThicknessMap);
                if (!field.GetMap(Index, WeatherMapKind.SnowCover, ut, SnowMap)) SnowMap.Width = 0;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("atmospheres-ice-map", "sea ice: the field's maps of " + Body.name + " could not be read", e);
                ok = false;
            }
            bool changed = ok != MapsValid || (ok && (FractionMap.Version != MapsVersion || Math.Abs(FractionMap.Ut - MapsUt) > 1.0));
            MapsValid = ok;
            if (ok)
            {
                MapsVersion = FractionMap.Version;
                MapsUt = FractionMap.Ut;
            }
            if (changed) InputsStamp++;
            return changed;
        }

        // The drift: the wind at a reference place, at most once per WindSeconds of real time (the drift itself steps once per game minute).
        internal void StepDrift(float now, double ut, double refLat, double refLon)
        {
            if (now < nextWind) return;
            nextWind = now + WindSeconds;
            double east = 0.0, north = 0.0;
            try
            {
                WeatherSample s;
                if (WeatherHub.Sample(Index, refLat, refLon, 0.0, ut, out s))
                {
                    east = s.WindWorld.x;
                    north = s.WindWorld.y;
                }
            }
            catch (Exception)
            {
                return;
            }
            double ex, ey, ez, nx, ny, nz;
            TangentVectors(Frame, refLat, refLon, out ex, out ey, out ez, out nx, out ny, out nz);
            if (Drift.Advance(ut, east, north, ex, ey, ez, nx, ny, nz, CellMetres)) InputsStamp++;
        }

        // The unit vectors of east and north at a place, in the sphere's frame (central differences of the frame's direction).
        internal static void TangentVectors(KspFrame f, double latDeg, double lonDeg, out double ex, out double ey, out double ez, out double nx, out double ny, out double nz)
        {
            double ax, ay, az, bx, by, bz;
            double dl = 0.05;
            f.Direction(latDeg, lonDeg + dl, out ax, out ay, out az);
            f.Direction(latDeg, lonDeg - dl, out bx, out by, out bz);
            Normalise(ax - bx, ay - by, az - bz, out ex, out ey, out ez);
            double lat0 = Math.Max(-89.9, latDeg - dl), lat1 = Math.Min(89.9, latDeg + dl);
            f.Direction(lat1, lonDeg, out ax, out ay, out az);
            f.Direction(lat0, lonDeg, out bx, out by, out bz);
            Normalise(ax - bx, ay - by, az - bz, out nx, out ny, out nz);
        }

        private static void Normalise(double x, double y, double z, out double ox, out double oy, out double oz)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l < 1e-12)
            {
                ox = 1; oy = 0; oz = 0;
                return;
            }
            ox = x / l; oy = y / l; oz = z / l;
        }

        // Fills a set of inputs for the worker or the probe from the state as it is now (the maps are copied).
        internal void FillInputs(IceInputs into)
        {
            into.Params = Params;
            into.Frame = Frame;
            into.Shore = Shore;
            into.Fraction.CopyFrom(MapsValid ? FractionMap : null);
            into.ThicknessM.CopyFrom(MapsValid ? ThicknessMap : null);
            into.SnowM.CopyFrom(MapsValid && SnowMap.Width > 0 ? SnowMap : null);
            into.DriftX = (float)Drift.X;
            into.DriftY = (float)Drift.Y;
            into.DriftZ = (float)Drift.Z;
            into.Red = Climate.Ice.Colour.r;
            into.Green = Climate.Ice.Colour.g;
            into.Blue = Climate.Ice.Colour.b;
            into.ForcedFraction = IceTuning.ForcedFraction;
            float lead = Tuning.Get(IceTuning.LeadOverrideKey, -1f);
            if (lead >= 0f)
            {
                into.Params = new IceParams
                {
                    RadiusM = Params.RadiusM, FloeSizeKm = Params.FloeSizeKm, LeadFraction = lead, Permanent = Params.Permanent, Seed = Params.Seed,
                    PermanentThicknessM = Params.PermanentThicknessM, ShoreGain = Params.ShoreGain, EdgeNoise = Params.EdgeNoise
                };
            }
        }

        // A number that changes when anything the picture is made from changes (the forced values included).
        internal int PictureStamp()
        {
            unchecked
            {
                int h = InputsStamp;
                h = h * 31 + IceTuning.ForcedFraction.GetHashCode();
                h = h * 31 + Tuning.Get(IceTuning.LeadOverrideKey, -1f).GetHashCode();
                h = h * 31 + (Shore != null ? 1 : 0);
                return h;
            }
        }
    }

    // The bodies that carry sea ice, by the body's index: created from the body's climate when the ice is first asked for.
    internal static class IceBodies
    {
        private static readonly Dictionary<int, IceBodyState> States = new Dictionary<int, IceBodyState>();

        internal static int Count { get { return States.Count; } }

        // The state of a body, or null where the body has no sea to freeze (no ocean, no liquid, switched off, broken).
        internal static IceBodyState Of(CelestialBody body)
        {
            if (body == null || !body.ocean || body.pqsController == null) return null;
            BodyClimate climate = Bodies.Get(body);
            if (climate == null || climate.Status.Kind == BodyStatusKind.Off || climate.Status.Kind == BodyStatusKind.Broken) return null;
            if (!climate.Ice.Liquid) return null;
            IceBodyState state;
            if (States.TryGetValue(body.flightGlobalsIndex, out state))
            {
                state.Update(climate);
                return state;
            }
            state = new IceBodyState(body, climate);
            States[body.flightGlobalsIndex] = state;
            return state;
        }

        internal static IEnumerable<IceBodyState> All { get { return States.Values; } }

        internal static void Clear()
        {
            States.Clear();
        }
    }
}
