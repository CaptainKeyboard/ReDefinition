using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // Links the systems of successive times into tracks by nearest neighbour (greedy, closest pairs first, inside a gate that follows
    // the radius and the last motion), and answers questions about a set of tracks. Pure, any thread.
    internal static class SystemTracker
    {
        // How many track samples in a row a system may be missed (a level that dips under the detection threshold for one sample) before
        // its track ends; the track coasts on its last motion meanwhile.
        private const int MaxMissed = 1;

        private sealed class Building
        {
            internal readonly List<double> Ut = new List<double>();
            internal readonly List<double> Lat = new List<double>();
            internal readonly List<double> Lon = new List<double>();
            internal readonly List<float> Radius = new List<float>();
            internal readonly List<float> Strength = new List<float>();
            internal ulong Key;
            internal bool Alive = true;
            internal int Missed;
        }

        private struct Pair
        {
            internal double Distance;
            internal int Track;
            internal int Detection;
        }

        // Builds tracks from the systems found at each time. times[i] belongs to perTime[i]. minGateM is the least distance a system
        // may move between two times and still be the same one (the grid spacing of the detector); maxSpeedMs is the fastest a system is
        // assumed to move before its motion is known. A system the source gives a key for keeps that key and is never in two tracks.
        internal static ForecastTrack[] Build(double[] times, List<SystemPoint>[] perTime, int bodyIndex, double bodyRadiusM, double minGateM,
            double maxSpeedMs)
        {
            var tracks = new List<Building>();
            var byKey = new Dictionary<ulong, Building>();
            for (int k = 0; k < times.Length; k++)
            {
                List<SystemPoint> found = perTime[k];
                var alive = new List<Building>();
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i].Alive) alive.Add(tracks[i]);
                }
                var pairs = new List<Pair>();
                double dt = k > 0 ? times[k] - times[k - 1] : 0.0;
                for (int a = 0; a < alive.Count; a++)
                {
                    Building t = alive[a];
                    int last = t.Ut.Count - 1;
                    double gapSteps = dt > 0.0 ? Math.Max(1.0, Math.Round((times[k] - t.Ut[last]) / dt)) : 1.0;
                    double pLat = t.Lat[last], pLon = t.Lon[last];
                    double motionM = 0.0;
                    if (last >= 1)
                    {
                        double spanSteps = dt > 0.0 ? Math.Max(1.0, Math.Round((t.Ut[last] - t.Ut[last - 1]) / dt)) : 1.0;
                        double dLat = (t.Lat[last] - t.Lat[last - 1]) / spanSteps;
                        double dLon = ForecastMath.WrapLon(t.Lon[last] - t.Lon[last - 1]) / spanSteps;
                        pLat = ForecastMath.ClampLat(t.Lat[last] + dLat * gapSteps);
                        pLon = ForecastMath.WrapLon(t.Lon[last] + dLon * gapSteps);
                        motionM = ForecastMath.DistanceM(t.Lat[last - 1], t.Lon[last - 1], t.Lat[last], t.Lon[last], bodyRadiusM) / spanSteps * gapSteps;
                    }
                    for (int d = 0; d < found.Count; d++)
                    {
                        SystemPoint p = found[d];
                        if (t.Key != 0UL && p.Key != 0UL && t.Key != p.Key) continue;
                        double dist = ForecastMath.DistanceM(pLat, pLon, p.LatDeg, p.LonDeg, bodyRadiusM);
                        double gate = Math.Max(t.Radius[last], p.RadiusM) + (last >= 1 ? 0.35 * motionM : 0.5 * maxSpeedMs * dt * gapSteps) + minGateM;
                        if (t.Key != 0UL && p.Key == t.Key) gate = double.MaxValue;
                        if (dist > gate) continue;
                        pairs.Add(new Pair { Distance = dist, Track = a, Detection = d });
                    }
                }
                pairs.Sort((x, y) => x.Distance.CompareTo(y.Distance));
                var trackTaken = new bool[alive.Count];
                var detectionTaken = new bool[found.Count];
                for (int i = 0; i < pairs.Count; i++)
                {
                    Pair pr = pairs[i];
                    if (trackTaken[pr.Track] || detectionTaken[pr.Detection]) continue;
                    trackTaken[pr.Track] = true;
                    detectionTaken[pr.Detection] = true;
                    alive[pr.Track].Missed = 0;
                    Append(alive[pr.Track], times[k], found[pr.Detection]);
                }
                for (int a = 0; a < alive.Count; a++)
                {
                    if (trackTaken[a]) continue;
                    alive[a].Missed++;
                    if (alive[a].Missed > MaxMissed) alive[a].Alive = false;
                }
                for (int d = 0; d < found.Count; d++)
                {
                    if (detectionTaken[d]) continue;
                    SystemPoint p = found[d];
                    Building revived;
                    if (p.Key != 0UL && byKey.TryGetValue(p.Key, out revived))
                    {
                        // The source says this is the system of an earlier track that ended: the track goes on, with a gap.
                        revived.Alive = true;
                        revived.Missed = 0;
                        Append(revived, times[k], p);
                        continue;
                    }
                    var t = new Building();
                    t.Key = p.Key != 0UL ? p.Key : BirthKey(bodyIndex, times[k], p.LatDeg, p.LonDeg);
                    Append(t, times[k], p);
                    tracks.Add(t);
                    if (p.Key != 0UL) byKey[p.Key] = t;
                }
            }
            var result = new ForecastTrack[tracks.Count];
            for (int i = 0; i < result.Length; i++)
            {
                Building b = tracks[i];
                var track = new ForecastTrack();
                track.CellKey = b.Key;
                track.Ut = b.Ut.ToArray();
                track.Lat = b.Lat.ToArray();
                track.Lon = b.Lon.ToArray();
                track.RadiusM = b.Radius.ToArray();
                track.Strength01 = b.Strength.ToArray();
                result[i] = track;
            }
            return result;
        }

        private static void Append(Building t, double ut, SystemPoint p)
        {
            t.Ut.Add(ut);
            t.Lat.Add(p.LatDeg);
            t.Lon.Add(p.LonDeg);
            t.Radius.Add(p.RadiusM);
            t.Strength.Add(p.Strength01);
        }

        // The key of a system first seen at a time and place: the place rounded to a degree and the time to an hour. Used only for a system
        // that no earlier report knew; InheritKeys carries the keys of the earlier set.
        internal static ulong BirthKey(int bodyIndex, double ut, double latDeg, double lonDeg)
        {
            ulong h = 0xC0FFEE11UL;
            h = ForecastMath.Mix(h, (long)bodyIndex);
            h = ForecastMath.Mix(h, (long)Math.Floor(ut / 3600.0));
            h = ForecastMath.Mix(h, (long)Math.Round(latDeg));
            h = ForecastMath.Mix(h, (long)Math.Round(lonDeg));
            return h == 0UL ? 1UL : h;
        }

        // Gives the tracks of a new set the key of the track of the earlier set that is at the same place at the new track's first time.
        internal static void InheritKeys(ForecastTrack[] previous, ForecastTrack[] current, double bodyRadiusM, double minGateM)
        {
            if (previous == null || current == null) return;
            var taken = new bool[previous.Length];
            var pairs = new List<Pair>();
            for (int c = 0; c < current.Length; c++)
            {
                if (current[c].Ut.Length == 0) continue;
                for (int p = 0; p < previous.Length; p++)
                {
                    double lat, lon;
                    float radius, strength;
                    if (!PositionAt(previous[p], current[c].Ut[0], out lat, out lon, out radius, out strength)) continue;
                    double dist = ForecastMath.DistanceM(lat, lon, current[c].Lat[0], current[c].Lon[0], bodyRadiusM);
                    if (dist > Math.Max(radius, current[c].RadiusM[0]) + minGateM) continue;
                    pairs.Add(new Pair { Distance = dist, Track = p, Detection = c });
                }
            }
            pairs.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            var done = new bool[current.Length];
            for (int i = 0; i < pairs.Count; i++)
            {
                Pair pr = pairs[i];
                if (taken[pr.Track] || done[pr.Detection]) continue;
                taken[pr.Track] = true;
                done[pr.Detection] = true;
                current[pr.Detection].CellKey = previous[pr.Track].CellKey;
            }
        }

        // The place, radius and strength of a track at a time, by linear interpolation of the samples (longitude the short way round).
        // False outside the track's life.
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
            lonDeg = ForecastMath.WrapLon(track.Lon[lo] + ForecastMath.WrapLon(track.Lon[hi] - track.Lon[lo]) * f);
            radiusM = (float)(track.RadiusM[lo] + (track.RadiusM[hi] - track.RadiusM[lo]) * f);
            strength01 = (float)(track.Strength01[lo] + (track.Strength01[hi] - track.Strength01[lo]) * f);
            return true;
        }

        // The tracks, cut to the part within a range of the site; a track that never comes that near is left out. The result holds
        // copies of the arrays, so a caller may change them.
        internal static ForecastTrack[] ClipToRange(ForecastTrack[] tracks, double latDeg, double lonDeg, double rangeM, double bodyRadiusM)
        {
            var kept = new List<ForecastTrack>();
            for (int t = 0; t < tracks.Length; t++)
            {
                ForecastTrack tr = tracks[t];
                int first = -1, last = -1;
                for (int i = 0; i < tr.Ut.Length; i++)
                {
                    if (ForecastMath.DistanceM(latDeg, lonDeg, tr.Lat[i], tr.Lon[i], bodyRadiusM) > rangeM) continue;
                    if (first < 0) first = i;
                    last = i;
                }
                if (first < 0) continue;
                int count = last - first + 1;
                var cut = new ForecastTrack();
                cut.CellKey = tr.CellKey;
                cut.Ut = new double[count];
                cut.Lat = new double[count];
                cut.Lon = new double[count];
                cut.RadiusM = new float[count];
                cut.Strength01 = new float[count];
                Array.Copy(tr.Ut, first, cut.Ut, 0, count);
                Array.Copy(tr.Lat, first, cut.Lat, 0, count);
                Array.Copy(tr.Lon, first, cut.Lon, 0, count);
                Array.Copy(tr.RadiusM, first, cut.RadiusM, 0, count);
                Array.Copy(tr.Strength01, first, cut.Strength01, 0, count);
                kept.Add(cut);
            }
            return kept.ToArray();
        }

        // The number of systems whose footprint covers the place at a time.
        internal static int CountCovering(ForecastTrack[] tracks, double latDeg, double lonDeg, double ut, double bodyRadiusM)
        {
            int count = 0;
            for (int t = 0; t < tracks.Length; t++)
            {
                if (Covers(tracks[t], latDeg, lonDeg, ut, bodyRadiusM)) count++;
            }
            return count;
        }

        // The key of the system that covers the place at a time and is strongest there, or 0.
        internal static ulong CoveringKey(ForecastTrack[] tracks, double latDeg, double lonDeg, double ut, double bodyRadiusM)
        {
            ulong key = 0UL;
            float best = -1f;
            for (int t = 0; t < tracks.Length; t++)
            {
                double lat, lon;
                float radius, strength;
                if (!PositionAt(tracks[t], ut, out lat, out lon, out radius, out strength)) continue;
                if (ForecastMath.DistanceM(latDeg, lonDeg, lat, lon, bodyRadiusM) > radius) continue;
                if (strength > best)
                {
                    best = strength;
                    key = tracks[t].CellKey;
                }
            }
            return key;
        }

        private static bool Covers(ForecastTrack track, double latDeg, double lonDeg, double ut, double bodyRadiusM)
        {
            double lat, lon;
            float radius, strength;
            if (!PositionAt(track, ut, out lat, out lon, out radius, out strength)) return false;
            return ForecastMath.DistanceM(latDeg, lonDeg, lat, lon, bodyRadiusM) <= radius;
        }

        // The first time at or after fromUt at which the system's footprint covers the place, to one second; NaN where it never does within
        // the track's life. The positions between samples are interpolated, so the footprint's edge is found by stepping and bisection.
        internal static double ArrivalOf(ForecastTrack track, double latDeg, double lonDeg, double fromUt, double bodyRadiusM)
        {
            int n = track.Ut.Length;
            if (n == 0) return double.NaN;
            double from = Math.Max(fromUt, track.Ut[0]);
            double to = track.Ut[n - 1];
            if (from > to) return double.NaN;
            if (Covers(track, latDeg, lonDeg, from, bodyRadiusM)) return from;
            const double coarse = 300.0;
            double prev = from;
            for (double t = from + coarse; ; t += coarse)
            {
                double at = Math.Min(t, to);
                if (Covers(track, latDeg, lonDeg, at, bodyRadiusM))
                {
                    double lo = prev, hi = at;
                    while (hi - lo > 1.0)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Covers(track, latDeg, lonDeg, mid, bodyRadiusM)) hi = mid;
                        else lo = mid;
                    }
                    return hi;
                }
                if (at >= to) return double.NaN;
                prev = at;
            }
        }

        // The first arrival over all tracks and the distance of that system at the start; where no system arrives, NaN and the distance
        // of the nearest one.
        internal static void Nearest(ForecastTrack[] tracks, double latDeg, double lonDeg, double startUt, double bodyRadiusM, out double etaUt,
            out double distanceM)
        {
            etaUt = double.NaN;
            distanceM = double.NaN;
            double nearest = double.MaxValue;
            double arriving = double.NaN;
            for (int t = 0; t < tracks.Length; t++)
            {
                ForecastTrack tr = tracks[t];
                int n = tr.Ut.Length;
                if (n == 0 || tr.Ut[n - 1] < startUt) continue;
                double lat, lon;
                float radius, strength;
                double at = Math.Max(startUt, tr.Ut[0]);
                if (!PositionAt(tr, at, out lat, out lon, out radius, out strength)) continue;
                double d = ForecastMath.DistanceM(latDeg, lonDeg, lat, lon, bodyRadiusM);
                if (d < nearest) nearest = d;
                double arrival = ArrivalOf(tr, latDeg, lonDeg, startUt, bodyRadiusM);
                if (!double.IsNaN(arrival) && (double.IsNaN(etaUt) || arrival < etaUt))
                {
                    etaUt = arrival;
                    arriving = d;
                }
            }
            if (!double.IsNaN(arriving)) distanceM = arriving;
            else if (nearest < double.MaxValue) distanceM = nearest;
        }
    }
}
