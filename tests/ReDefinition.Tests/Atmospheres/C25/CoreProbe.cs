using System;
using AtmoSim.Core;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // The queries and hashes of the offline proof (tools/atmosphere-sim, Timing.Queries and Timing.HashQueries), written the same way so
    // that the generator compiled into the game's assembly can be asked the proof's questions. The numbers are the proof's: seed 77 for the
    // positions, UT up to 3e7 s, the result hash over the values the determinism criterion names.
    internal static class CoreProbe
    {
        internal struct Q
        {
            internal double Lat, Lon, Alt, Ut;
        }

        internal static Q[] Queries(int n, ulong seed, double utMax)
        {
            var r = new Rng(seed);
            var q = new Q[n];
            for (int i = 0; i < n; i++)
            {
                q[i].Lat = Math.Asin(2.0 * r.Uniform() - 1.0);
                q[i].Lon = (2.0 * r.Uniform() - 1.0) * Math.PI;
                q[i].Alt = r.Uniform() * 12000.0;
                q[i].Ut = r.Uniform() * utMax;
            }
            return q;
        }

        internal static ulong HashQueries(WeatherGenerator g, Q[] q, int[] order, double utShift)
        {
            var hs = new ulong[q.Length];
            Weather w;
            for (int k = 0; k < q.Length; k++)
            {
                int i = order == null ? k : order[k];
                g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, q[i].Ut + utShift, out w);
                hs[i] = WeatherGenerator.HashOf(ref w, (ulong)i);
            }
            ulong h = 0xCBF29CE484222325UL;
            for (int i = 0; i < hs.Length; i++) h = Hash.Mix(h ^ hs[i]);
            return h;
        }

        internal static WeatherGenerator ProofGenerator(ulong seed)
        {
            return new WeatherGenerator(BodyParams.KerbinLike(), seed);
        }
    }
}
