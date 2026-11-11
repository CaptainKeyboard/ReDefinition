using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.B12
{
    // A deterministic test field written against IWeatherField: a set of cells that drift around a body of RadiusM, each with a smooth
    // life cycle (sin^2 of the age), a Gaussian footprint and a peak; the level is the union of the cells. It can list its systems
    // (IWeatherSystemSource) so that the exact path of the track search can be run, and hide that interface in a wrapper to run the
    // generic grid search. The seed is part of DefinitionId; Version may bump on every call to prove that the cache does not read it.
    internal sealed class MovingCellsField : IWeatherField, IWeatherSystemSource
    {
        internal const double RadiusM = 600000.0;
        internal const double Day = 86400.0;

        private struct Cell
        {
            internal double Lat0, Lon0, VEast, LatAmp, LatOmega, LatPhase, PeriodS, Phase, SigmaM;
            internal float Peak;
            internal int Index;
        }

        private readonly Cell[] cells;
        private readonly int seed;
        private readonly bool bumpVersion;
        private long version;

        internal MovingCellsField(int seed, int cellCount = 12, bool threadSafe = true, bool bumpVersion = false)
        {
            this.seed = seed;
            ThreadSafeFlag = threadSafe;
            this.bumpVersion = bumpVersion;
            Readiness = FieldReadiness.Ready;
            cells = new Cell[cellCount];
            ulong s = (ulong)seed * 0x9E3779B97F4A7C15UL + 12345UL;
            for (int i = 0; i < cellCount; i++)
            {
                var c = new Cell();
                c.Index = i;
                c.Lat0 = -65.0 + 130.0 * Next(ref s);
                c.Lon0 = -180.0 + 360.0 * Next(ref s);
                double dir = Next(ref s) < 0.2 ? -1.0 : 1.0;
                c.VEast = dir * (4.0 + 21.0 * Next(ref s));
                c.LatAmp = 2.0 + 4.0 * Next(ref s);
                c.LatOmega = 2.0 * Math.PI / ((5.0 + 7.0 * Next(ref s)) * Day);
                c.LatPhase = 2.0 * Math.PI * Next(ref s);
                c.PeriodS = (36.0 + 60.0 * Next(ref s)) * 3600.0;
                c.Phase = Next(ref s);
                c.SigmaM = 25000.0 + 45000.0 * Next(ref s);
                c.Peak = (float)(0.35 + 0.65 * Next(ref s));
                cells[i] = c;
            }
        }

        private static double Next(ref ulong s)
        {
            s += 0x9E3779B97F4A7C15UL;
            ulong z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) / 9007199254740992.0;
        }

        internal bool ThreadSafeFlag;
        internal int SampleCalls;

        public FieldReadiness Readiness { get; set; }

        private static void CellAt(ref Cell c, double ut, out double lat, out double lon, out float strength)
        {
            lat = c.Lat0 + c.LatAmp * Math.Sin(c.LatOmega * ut + c.LatPhase);
            lon = c.Lon0 + c.VEast * ut / (RadiusM * Math.Cos(c.Lat0 * Math.PI / 180.0)) * 180.0 / Math.PI;
            lon = lon - 360.0 * Math.Floor((lon + 180.0) / 360.0);
            double u = ut / c.PeriodS + c.Phase;
            u -= Math.Floor(u);
            double s = Math.Sin(Math.PI * u);
            strength = (float)(c.Peak * s * s);
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            Interlocked.Increment(ref SampleCalls);
            if (bumpVersion) Interlocked.Increment(ref version);
            double clear = 1.0;
            for (int i = 0; i < cells.Length; i++)
            {
                double lat, lon;
                float strength;
                CellAt(ref cells[i], ut, out lat, out lon, out strength);
                if (strength < 1e-4f) continue;
                double dy = (lat - latDeg) * Math.PI / 180.0 * RadiusM;
                double dl = lon - lonDeg;
                dl -= 360.0 * Math.Floor((dl + 180.0) / 360.0);
                double dx = dl * Math.PI / 180.0 * RadiusM * Math.Cos(0.5 * (lat + latDeg) * Math.PI / 180.0);
                double r2 = (dx * dx + dy * dy) / (cells[i].SigmaM * cells[i].SigmaM);
                if (r2 > 20.0) continue;
                clear *= 1.0 - strength * Math.Exp(-r2);
            }
            float level = (float)(1.0 - clear);
            sample = WeatherSample.Clear(bodyIndex);
            sample.Level01 = level;
            if (level >= PrecipScale.FlurriesFrom)
            {
                bool cold = Math.Abs(latDeg) > 45.0;
                sample.Type = cold ? PrecipType.Snow : PrecipType.Rain;
                sample.SnowFraction = cold ? 1f : 0f;
                sample.RainFraction = cold ? 0f : 1f;
            }
            sample.VisibilityM = (float)(WeatherSample.ClearVisibilityM * (1.0 - 0.97 * Math.Pow(level, 0.6)));
            sample.WindWorld = new Vector3((float)(8.0 + 12.0 * level + 3.0 * Math.Sin(ut / 5000.0 + lonDeg)), (float)(2.0 * Math.Cos(ut / 7000.0 + latDeg)), 0f);
            sample.TemperatureK = 268f;
            sample.CloudCover01 = Math.Min(1f, level * 1.5f + 0.1f);
            sample.Readiness = Readiness;
            return true;
        }

        // The systems at a time, with a key per cell and life cycle (so a new life cycle is a new system).
        public bool TryGetSystems(int bodyIndex, double ut, List<SystemPoint> into)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                double lat, lon;
                float strength;
                CellAt(ref cells[i], ut, out lat, out lon, out strength);
                if (strength < PrecipScale.FlurriesFrom) continue;
                double cycle = Math.Floor(ut / cells[i].PeriodS + cells[i].Phase);
                var p = new SystemPoint();
                p.LatDeg = lat;
                p.LonDeg = lon;
                p.RadiusM = (float)(0.8326 * cells[i].SigmaM);
                p.Strength01 = strength;
                p.Key = ForecastMath.Mix(ForecastMath.Mix((ulong)seed + 77UL, (long)i), (long)cycle);
                into.Add(p);
            }
            return true;
        }

        // The cells at a time without the filter (for tests that compare a track with the truth).
        internal void Truth(double ut, List<SystemPoint> into)
        {
            TryGetSystems(0, ut, into);
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
        public void Advance(double budgetMs) { }
        public long Version { get { return Interlocked.Read(ref version); } }
        public long DefinitionId { get { return seed * 1000003L + cells.Length; } }
        public bool ThreadSafe { get { return ThreadSafeFlag; } }
        public double StepSeconds { get { return 3600.0; } }
    }

    // The same cells without the listing of systems: the forecast must search the field for them.
    internal sealed class HiddenSystemsField : IWeatherField
    {
        private readonly MovingCellsField inner;

        internal HiddenSystemsField(MovingCellsField inner)
        {
            this.inner = inner;
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            return inner.Sample(bodyIndex, latDeg, lonDeg, altM, ut, out sample);
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
        public void Advance(double budgetMs) { }
        public long Version { get { return inner.Version; } }
        public long DefinitionId { get { return inner.DefinitionId; } }
        public bool ThreadSafe { get { return inner.ThreadSafe; } }
        public double StepSeconds { get { return inner.StepSeconds; } }
        public FieldReadiness Readiness { get { return inner.Readiness; } }
    }

    // A level that is a function of the UT alone, the same everywhere: the exact input of the alert tests.
    internal sealed class ScriptedField : IWeatherField
    {
        internal Func<double, float> Level = ut => 0f;
        internal Func<double, float> WindEast = ut => 3f;
        internal Func<double, float> Visibility;
        internal bool ThreadSafeFlag = true;
        internal long Id = 4242;
        internal FieldReadiness ReadyState = FieldReadiness.Ready;
        internal bool Approx;
        internal int SampleCalls;

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            Interlocked.Increment(ref SampleCalls);
            sample = WeatherSample.Clear(bodyIndex);
            float level = Level(ut);
            sample.Level01 = level;
            sample.Type = level >= PrecipScale.FlurriesFrom ? PrecipType.Snow : PrecipType.None;
            sample.SnowFraction = level >= PrecipScale.FlurriesFrom ? 1f : 0f;
            sample.WindWorld = new Vector3(WindEast(ut), 0f, 0f);
            sample.VisibilityM = Visibility != null ? Visibility(ut) : WeatherSample.ClearVisibilityM * (1f - 0.9f * level);
            sample.TemperatureK = 260f;
            sample.Readiness = ReadyState;
            sample.Approximate = Approx;
            return true;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
        public void Advance(double budgetMs) { }
        public long Version { get { return 0; } }
        public long DefinitionId { get { return Id; } }
        public bool ThreadSafe { get { return ThreadSafeFlag; } }
        public double StepSeconds { get { return 3600.0; } }
        public FieldReadiness Readiness { get { return ReadyState; } }
    }

    // Wraps a field, counts the calls and records the arguments of Sample; Mutate may change the result (a negative control).
    internal sealed class RecordingField : IWeatherField
    {
        private readonly IWeatherField inner;
        internal readonly List<double[]> Calls = new List<double[]>();
        internal int MapCalls, WindowCalls;
        internal Func<double, float, float> Mutate;

        internal RecordingField(IWeatherField inner)
        {
            this.inner = inner;
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            lock (Calls)
            {
                Calls.Add(new[] { bodyIndex, latDeg, lonDeg, altM, ut });
            }
            bool ok = inner.Sample(bodyIndex, latDeg, lonDeg, altM, ut, out sample);
            if (Mutate != null) sample.Level01 = Mutate(ut, sample.Level01);
            return ok;
        }

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            MapCalls++;
            return inner.GetMap(bodyIndex, kind, ut, target);
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            WindowCalls++;
            return inner.FillWindow(bodyIndex, centreLatDeg, centreLonDeg, radiusKm, ut, target);
        }

        public void Advance(double budgetMs) { inner.Advance(budgetMs); }
        public long Version { get { return inner.Version; } }
        public long DefinitionId { get { return inner.DefinitionId; } }
        public bool ThreadSafe { get { return inner.ThreadSafe; } }
        public double StepSeconds { get { return inner.StepSeconds; } }
        public FieldReadiness Readiness { get { return inner.Readiness; } }
    }

    // The environment of a ForecastService in a test.
    internal sealed class TestEnvironment : IForecastEnvironment
    {
        internal IWeatherField FieldValue;
        internal double Ut;
        internal bool EnabledValue = true;
        internal Func<int, ForecastContext> Contexts;

        public IWeatherField Field { get { return FieldValue; } }
        public double NowUt { get { return Ut; } }
        public bool Enabled { get { return EnabledValue; } }

        public ForecastContext ContextFor(int bodyIndex)
        {
            return Contexts(bodyIndex);
        }
    }
}
