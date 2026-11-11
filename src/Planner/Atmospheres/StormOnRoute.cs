using System;
using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;

namespace ReDefinition.Planner
{
    // One leg of a route as the storm check needs it: the body, when it starts and ends, and where. A leg that flies on rails has no wind
    // (the planner's background flights feel none), so this is information for the player and changes nothing about the route.
    internal struct RouteLeg
    {
        // The number the player sees ("Leg 3").
        internal int Number;
        internal int BodyIndex;
        internal double StartUt, EndUt;
        internal double StartLatDeg, StartLonDeg, EndLatDeg, EndLonDeg;
    }

    // A weather system on a leg.
    internal struct StormOnRouteEntry
    {
        internal int LegNumber;
        // When the leg first meets the system, where, and for how long a stretch of the leg lies inside it.
        internal double Ut;
        internal double LatDeg, LonDeg;
        internal double InsideMetres;
        internal PrecipBand Band;
        internal PrecipType Type;
        internal ulong CellKey;
    }

    // What the storm check found on a route: the entries and the lines for the route panel, and the place of the worst one for a marker on
    // the route map.
    internal sealed class StormOnRouteInfo
    {
        internal readonly List<StormOnRouteEntry> Entries = new List<StormOnRouteEntry>();

        internal bool HasMarker
        {
            get { return Entries.Count > 0; }
        }

        // The strongest entry (the earliest of equals): where a marker stands.
        internal StormOnRouteEntry Worst
        {
            get
            {
                StormOnRouteEntry best = Entries[0];
                for (int i = 1; i < Entries.Count; i++)
                {
                    if ((int)Entries[i].Band > (int)best.Band) best = Entries[i];
                }
                return best;
            }
        }

        // "Leg 3 (UT 5214600): Heavy snow, 40 km", one line for each entry.
        internal string[] Lines(Func<double, string> dateText = null)
        {
            var lines = new string[Entries.Count];
            for (int i = 0; i < lines.Length; i++) lines[i] = StormOnRoute.Line(Entries[i], dateText);
            return lines;
        }
    }

    // The storms on a planned route. Reads the weather seam of Common (WeatherHooks.BodyTracks and WeatherHooks.Forecast), so it shows
    // nothing where the atmospheres are not installed: both are null then, and so is the result. Pure over the tracks it is given.
    internal static class StormOnRoute
    {
        // The samples taken along a leg (or fewer for a short one), in time.
        internal const int MaxSamples = 96;
        // A system weaker than this is not a storm for the route.
        internal const float MinStrength01 = PrecipScale.FlurriesFrom;

        // Entries for the legs against the tracks of their bodies. tracks gives a body's tracks (null for none); radiusM a body's radius;
        // typeAt the kind of precipitation at a place and time (null: PrecipType.None, the line then says "precipitation").
        internal static StormOnRouteInfo Find(IList<RouteLeg> legs, Func<int, ForecastTrack[]> tracks, Func<int, double> radiusM,
            Func<int, double, double, double, PrecipType> typeAt)
        {
            var info = new StormOnRouteInfo();
            if (legs == null || tracks == null || radiusM == null) return info;
            for (int l = 0; l < legs.Count; l++)
            {
                RouteLeg leg = legs[l];
                ForecastTrack[] list = tracks(leg.BodyIndex);
                double radius = radiusM(leg.BodyIndex);
                if (list == null || list.Length == 0 || radius <= 0.0 || leg.EndUt <= leg.StartUt) continue;
                OnLeg(info, leg, list, radius, typeAt);
            }
            return info;
        }

        // The entries of the seam: the tracks and forecasts the atmospheres publish. Null where there are none.
        internal static StormOnRouteInfo FindWithSeam(IList<RouteLeg> legs, Func<int, double> radiusM)
        {
            Func<int, ForecastTrack[]> tracks = WeatherHooks.BodyTracks;
            if (tracks == null) return null;
            Func<ForecastSite, ForecastReport> forecast = WeatherHooks.Forecast;
            Func<int, double, double, double, PrecipType> typeAt = null;
            if (forecast != null)
            {
                typeAt = (body, lat, lon, ut) =>
                {
                    ForecastReport report = forecast(new ForecastSite { BodyIndex = body, LatDeg = lat, LonDeg = lon });
                    return TypeAt(report, ut);
                };
            }
            return Find(legs, tracks, radiusM, typeAt);
        }

        // The type of the step a time falls in.
        internal static PrecipType TypeAt(ForecastReport report, double ut)
        {
            if (report == null || !report.Available || report.Steps == null || report.Steps.Length == 0) return PrecipType.None;
            int at = 0;
            for (int i = 0; i < report.Steps.Length; i++)
            {
                if (report.Steps[i].Ut <= ut) at = i;
                else break;
            }
            return report.Steps[at].Type;
        }

        private sealed class Run
        {
            internal ulong Key;
            internal double FirstUt, Lat, Lon, Inside;
            internal float Peak;
        }

        // The leg is walked at even steps in time; the position is the great-circle path between its ends at the same fraction of the
        // time. A sample inside a system's footprint adds the length of the step to that system's run on the leg.
        private static void OnLeg(StormOnRouteInfo info, RouteLeg leg, ForecastTrack[] tracks, double radius, Func<int, double, double, double, PrecipType> typeAt)
        {
            double span = leg.EndUt - leg.StartUt;
            double total = Geo.DistanceM(leg.StartLatDeg, leg.StartLonDeg, leg.EndLatDeg, leg.EndLonDeg, radius);
            int samples = Math.Max(2, Math.Min(MaxSamples, (int)Math.Ceiling(span / 600.0) + 1));
            double segment = total / (samples - 1);
            var runs = new Dictionary<ulong, Run>();
            var order = new List<ulong>();
            for (int s = 0; s < samples; s++)
            {
                double f = s / (double)(samples - 1);
                double ut = leg.StartUt + span * f;
                double lat, lon;
                Geo.Along(leg.StartLatDeg, leg.StartLonDeg, leg.EndLatDeg, leg.EndLonDeg, f, out lat, out lon);
                for (int t = 0; t < tracks.Length; t++)
                {
                    double tLat, tLon;
                    float tRadius, strength;
                    if (!Geo.PositionAt(tracks[t], ut, out tLat, out tLon, out tRadius, out strength) || strength < MinStrength01) continue;
                    if (Geo.DistanceM(lat, lon, tLat, tLon, radius) > tRadius) continue;
                    Run run;
                    if (!runs.TryGetValue(tracks[t].CellKey, out run))
                    {
                        run = new Run { Key = tracks[t].CellKey, FirstUt = ut, Lat = lat, Lon = lon };
                        runs[run.Key] = run;
                        order.Add(run.Key);
                    }
                    run.Inside += segment;
                    if (strength > run.Peak) run.Peak = strength;
                }
            }
            for (int i = 0; i < order.Count; i++)
            {
                Run run = runs[order[i]];
                info.Entries.Add(new StormOnRouteEntry
                {
                    LegNumber = leg.Number, Ut = run.FirstUt, LatDeg = run.Lat, LonDeg = run.Lon, InsideMetres = run.Inside,
                    Band = PrecipScale.BandOf(run.Peak, 0f), CellKey = run.Key,
                    Type = typeAt != null ? typeAt(leg.BodyIndex, run.Lat, run.Lon, run.FirstUt) : PrecipType.None
                });
            }
        }

        // "Leg 3 (UT 5214600): Heavy snow, 40 km"; the date text replaces "UT n" where the caller has one.
        internal static string Line(StormOnRouteEntry e, Func<double, string> dateText = null)
        {
            string when = dateText != null ? dateText(e.Ut) : "UT " + Math.Round(e.Ut).ToString("0", CultureInfo.InvariantCulture);
            string km = e.InsideMetres < 1000.0
                ? "under 1 km"
                : Math.Round(e.InsideMetres / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " km";
            return "Leg " + e.LegNumber.ToString(CultureInfo.InvariantCulture) + " (" + when + "): " + PrecipScale.DefaultName(e.Band) + " " + TypeName(e.Type) + ", " + km;
        }

        private static string TypeName(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return "snow";
                case PrecipType.Rain: return "rain";
                case PrecipType.Sleet: return "sleet";
                case PrecipType.FreezingRain: return "freezing rain";
                case PrecipType.Dust: return "dust";
                case PrecipType.Sand: return "sand";
                case PrecipType.Ash: return "ash";
                case PrecipType.Methane: return "methane rain";
                default: return "precipitation";
            }
        }
    }

    // The geometry the check needs: the great-circle distance and path on a sphere, and a track's place at a time.
    internal static class Geo
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        internal static double WrapLon(double lonDeg)
        {
            double l = lonDeg % 360.0;
            if (l >= 180.0) l -= 360.0;
            if (l < -180.0) l += 360.0;
            return l;
        }

        internal static double DistanceM(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg, double radiusM)
        {
            double p1 = lat1Deg * DegToRad, p2 = lat2Deg * DegToRad;
            double s1 = Math.Sin((p2 - p1) * 0.5), s2 = Math.Sin((lon2Deg - lon1Deg) * DegToRad * 0.5);
            double a = s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2;
            if (a > 1.0) a = 1.0;
            return 2.0 * radiusM * Math.Asin(Math.Sqrt(a));
        }

        // The place a fraction of the way along the great circle from one place to another.
        internal static void Along(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg, double fraction, out double latDeg, out double lonDeg)
        {
            double p1 = lat1Deg * DegToRad, l1 = lon1Deg * DegToRad, p2 = lat2Deg * DegToRad, l2 = lon2Deg * DegToRad;
            double s1 = Math.Sin((p2 - p1) * 0.5), s2 = Math.Sin((l2 - l1) * 0.5);
            double a = s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2;
            double d = 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
            if (d < 1e-9)
            {
                latDeg = lat1Deg;
                lonDeg = lon1Deg;
                return;
            }
            double ka = Math.Sin((1.0 - fraction) * d) / Math.Sin(d), kb = Math.Sin(fraction * d) / Math.Sin(d);
            double x = ka * Math.Cos(p1) * Math.Cos(l1) + kb * Math.Cos(p2) * Math.Cos(l2);
            double y = ka * Math.Cos(p1) * Math.Sin(l1) + kb * Math.Cos(p2) * Math.Sin(l2);
            double z = ka * Math.Sin(p1) + kb * Math.Sin(p2);
            latDeg = Math.Atan2(z, Math.Sqrt(x * x + y * y)) * RadToDeg;
            lonDeg = WrapLon(Math.Atan2(y, x) * RadToDeg);
        }

        // The place, radius and strength of a track at a time, by linear interpolation of the samples; false outside the track's life.
        internal static bool PositionAt(ForecastTrack track, double ut, out double latDeg, out double lonDeg, out float radiusM, out float strength01)
        {
            latDeg = lonDeg = 0.0;
            radiusM = strength01 = 0f;
            int n = track.Ut.Length;
            if (n == 0 || ut < track.Ut[0] || ut > track.Ut[n - 1]) return false;
            if (n == 1)
            {
                latDeg = track.Lat[0];
                lonDeg = track.Lon[0];
                radiusM = track.RadiusM[0];
                strength01 = track.Strength01[0];
                return true;
            }
            int hi = 1;
            while (hi < n - 1 && track.Ut[hi] < ut) hi++;
            int lo = hi - 1;
            double span = track.Ut[hi] - track.Ut[lo];
            double f = span > 0.0 ? (ut - track.Ut[lo]) / span : 0.0;
            latDeg = track.Lat[lo] + (track.Lat[hi] - track.Lat[lo]) * f;
            lonDeg = WrapLon(track.Lon[lo] + WrapLon(track.Lon[hi] - track.Lon[lo]) * f);
            radiusM = (float)(track.RadiusM[lo] + (track.RadiusM[hi] - track.RadiusM[lo]) * f);
            strength01 = (float)(track.Strength01[lo] + (track.Strength01[hi] - track.Strength01[lo]) * f);
            return true;
        }
    }
}
