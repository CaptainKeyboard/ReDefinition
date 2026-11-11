using System;
using System.Collections.Generic;
using AtmoSim.Core;

namespace ReDefinition.Atmospheres
{
    // The surface layer of the field for single places: snow depth, wet or dry, dust and the sea ice of a site, each a function of the UT only
    // (AtmoSim.Core.SurfaceSite: the window of 6-hour samples of the site is cached, the ice is integrated from the climatological anchor of the
    // last quarter of the year). A site keeps a few kilobytes of window, so the places asked about are kept in a small least-recently-used
    // list per body; the same call from any thread gives the same answer (a lock serialises the use of one site).
    internal sealed class GeneratorSurface
    {
        // The sites kept per body.
        internal const int Capacity = 96;
        // Two places closer than this, in radians (about 0.6 m on a body of 600 km), count as one site.
        private const double Quantum = 1.0e-6;

        private sealed class Entry
        {
            internal long Key;
            internal SurfaceSite Site;
            internal long Use;
        }

        private readonly GeneratorField field;
        private readonly object gate = new object();
        private readonly Dictionary<int, Dictionary<long, Entry>> bodies = new Dictionary<int, Dictionary<long, Entry>>();
        private long clock;

        internal GeneratorSurface(GeneratorField field)
        {
            this.field = field;
        }

        // Drops the sites of a body (its definition changed).
        internal void Forget(int bodyIndex)
        {
            lock (gate)
            {
                bodies.Remove(bodyIndex);
            }
        }

        // The surface state of a place at a time; false where the body has no state. The state is clear of numbers that are not finite.
        internal bool Evaluate(int bodyIndex, double latDeg, double lonDeg, double ut, out SurfaceState state)
        {
            state = new SurfaceState();
            GeneratorState s = field.StateOf(bodyIndex);
            if (s == null || s.Broken || double.IsNaN(latDeg) || double.IsNaN(lonDeg) || double.IsNaN(ut) || double.IsInfinity(ut)) return false;
            double lat = Math.Max(-90.0, Math.Min(90.0, latDeg)) * (Math.PI / 180.0);
            double lon = DMath.WrapPi(lonDeg * (Math.PI / 180.0));
            lock (gate)
            {
                Dictionary<long, Entry> sites;
                if (!bodies.TryGetValue(bodyIndex, out sites))
                {
                    sites = new Dictionary<long, Entry>();
                    bodies[bodyIndex] = sites;
                }
                long key = (long)Math.Round(lat / Quantum) * 4000003L + (long)Math.Round(lon / Quantum);
                Entry e;
                if (!sites.TryGetValue(key, out e))
                {
                    if (sites.Count >= Capacity) Evict(sites);
                    e = new Entry { Key = key, Site = new SurfaceSite(s.Generator, lat, lon) };
                    sites[key] = e;
                }
                e.Use = ++clock;
                state = e.Site.Evaluate(ut);
            }
            state.SnowDepthM += field.ForcedSnowDepthM(bodyIndex, s.Parameters.RadiusM, lat, lon, ut);
            return Good(ref state);
        }

        private static void Evict(Dictionary<long, Entry> sites)
        {
            long oldest = long.MaxValue, key = 0;
            foreach (KeyValuePair<long, Entry> pair in sites)
            {
                if (pair.Value.Use < oldest)
                {
                    oldest = pair.Value.Use;
                    key = pair.Key;
                }
            }
            sites.Remove(key);
        }

        private static bool Good(ref SurfaceState s)
        {
            return IsFinite(s.SnowDepthM) && IsFinite(s.IceThicknessM) && IsFinite(s.IceFraction) && IsFinite(s.Wet01) && IsFinite(s.DustDepositMm);
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
