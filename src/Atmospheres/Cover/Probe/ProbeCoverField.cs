using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A decorator around the weather field for the series of package D34 (probe code, never installed in play). The generator's
    // surface map (GeneratorMaps) does not add the forced discs of AddPrecipitationDisc (the forcing reaches only a single site's
    // Evaluate and the samples), so the decorator makes the forced system visible in GetMap(SnowCover) itself: a disc of snow of a rate
    // between two times leaves a depth of 8 times the water equivalent, which fades with a test time constant after the disc ends. The
    // temperature at the disc is scripted (cold while it falls, warm after it, when the warm flag is set), so that the vehicle cover's
    // melt can be seen. Everything else is the inner field's. Installed as WeatherHub.Field by CoverProbe and taken out again.
    internal sealed class ProbeCoverField : IWeatherField, ISurfaceForcing
    {
        internal sealed class Disc
        {
            internal int Body;
            internal double LatDeg, LonDeg, RadiusKm, FromUt, ToUt;
            internal float RateMmPerH;
            // The time constant of the fade after the disc ends, in seconds of game time (0: no fade).
            internal double FadeTauS;
        }

        private readonly IWeatherField inner;
        private readonly List<Disc> discs = new List<Disc>();
        private long changes;

        // The air temperature at the discs while they fall and afterwards when Warm is set, in kelvin.
        internal float ColdK = 255f;
        internal float WarmK = 286f;
        internal bool Warm;

        internal ProbeCoverField(IWeatherField inner)
        {
            this.inner = inner;
        }

        internal IWeatherField Inner { get { return inner; } }

        internal void Add(Disc d)
        {
            discs.Add(d);
            changes++;
        }

        public void AddPrecipitationDisc(int bodyIndex, double latDeg, double lonDeg, double radiusKm, float rateMmPerH, PrecipType type, double fromUt, double toUt)
        {
            if (type == PrecipType.Snow) Add(new Disc { Body = bodyIndex, LatDeg = latDeg, LonDeg = lonDeg, RadiusKm = radiusKm, RateMmPerH = rateMmPerH, FromUt = fromUt, ToUt = toUt });
            else
            {
                var forcing = inner as ISurfaceForcing;
                if (forcing != null) forcing.AddPrecipitationDisc(bodyIndex, latDeg, lonDeg, radiusKm, rateMmPerH, type, fromUt, toUt);
            }
        }

        // The depth in metres the discs leave at a place and time.
        internal double DepthM(int bodyIndex, double latDeg, double lonDeg, double ut, double bodyRadiusM)
        {
            double depth = 0;
            foreach (Disc d in discs)
            {
                if (d.Body != bodyIndex || ut <= d.FromUt) continue;
                if (Distance(latDeg, lonDeg, d.LatDeg, d.LonDeg, bodyRadiusM) > d.RadiusKm * 1000.0) continue;
                double hours = (Math.Min(ut, d.ToUt) - d.FromUt) / 3600.0;
                double m = d.RateMmPerH * hours * 8.0 / 1000.0;
                if (ut > d.ToUt && d.FadeTauS > 0 && Warm) m *= Math.Exp(-(ut - d.ToUt) / d.FadeTauS);
                depth += m;
            }
            return depth;
        }

        private static double Distance(double lat1, double lon1, double lat2, double lon2, double radiusM)
        {
            double dLat = (lat1 - lat2) * Math.PI / 180.0;
            double dLon = (lon1 - lon2) * Math.PI / 180.0 * Math.Cos((lat1 + lat2) * Math.PI / 360.0);
            while (dLon > Math.PI) dLon -= 2 * Math.PI;
            while (dLon < -Math.PI) dLon += 2 * Math.PI;
            return radiusM * Math.Sqrt(dLat * dLat + dLon * dLon);
        }

        private bool Inside(int bodyIndex, double lat, double lon, double ut, double radiusM, out bool falling)
        {
            falling = false;
            foreach (Disc d in discs)
            {
                if (d.Body != bodyIndex || Distance(lat, lon, d.LatDeg, d.LonDeg, radiusM) > d.RadiusKm * 1000.0) continue;
                if (ut >= d.FromUt && ut <= d.ToUt) falling = true;
                if (ut >= d.FromUt) return true;
            }
            return false;
        }

        public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
        {
            bool ok = inner.Sample(bodyIndex, latDeg, lonDeg, altM, ut, out sample);
            bool falling;
            // The radius of the body is not known here: the disc test uses a nominal 600 km; the probe's discs are placed on the body of
            // the active vessel and its radius is passed through RadiusM.
            if (Inside(bodyIndex, latDeg, lonDeg, ut, RadiusM, out falling))
            {
                sample.TemperatureK = falling ? ColdK : (Warm ? WarmK : ColdK);
                if (falling)
                {
                    sample.Level01 = 0.8f;
                    sample.Type = PrecipType.Snow;
                    sample.SnowFraction = 1f;
                    sample.SleetFraction = 0f;
                    sample.RainFraction = 0f;
                }
                ok = true;
            }
            return ok;
        }

        // The radius of the body the discs lie on, set by the probe.
        internal double RadiusM = 600000.0;

        public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target)
        {
            bool ok = inner.GetMap(bodyIndex, kind, ut, target);
            if (kind != WeatherMapKind.SnowCover || discs.Count == 0) return ok;
            if (!ok || target.Width < 1 || target.Height < 1) return ok;
            for (int j = 0; j < target.Height; j++)
            {
                double lat = 90.0 - (j + 0.5) * 180.0 / target.Height;
                for (int i = 0; i < target.Width; i++)
                {
                    double lon = -180.0 + (i + 0.5) * 360.0 / target.Width;
                    double add = DepthM(bodyIndex, lat, lon, ut, RadiusM);
                    if (add > 0) target.Values[j * target.Width + i] += (float)add;
                }
            }
            target.Version = unchecked(target.Version + changes * 1000003L + (long)(ut / 15.0));
            return true;
        }

        public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target)
        {
            return inner.FillWindow(bodyIndex, centreLatDeg, centreLonDeg, radiusKm, ut, target);
        }

        public void Advance(double budgetMs)
        {
            inner.Advance(budgetMs);
        }

        public long Version
        {
            get
            {
                long ut = Planetarium.fetch != null ? (long)(Planetarium.GetUniversalTime() / 15.0) : 0;
                return unchecked(inner.Version + changes * 1000003L + ut);
            }
        }

        public long DefinitionId { get { return inner.DefinitionId; } }
        public bool ThreadSafe { get { return false; } }
        public double StepSeconds { get { return inner.StepSeconds; } }
        public FieldReadiness Readiness { get { return inner.Readiness; } }
    }
}
