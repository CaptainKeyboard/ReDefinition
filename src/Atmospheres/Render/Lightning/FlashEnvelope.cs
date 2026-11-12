using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The light of a flash over time (package T07): one to three strokes, each a fast rise and an exponential decay, the later ones weaker,
    // and for a flash to the ground a dim continuing glow under them. The shape comes from the flash's seed; the same event always has the
    // same shape. Peak 1 is the brightest stroke.
    internal static class FlashEnvelope
    {
        // The rise of a stroke and the decay time in s, the gap between strokes (the first of the two numbers plus a draw of the second).
        internal const double RiseS = 0.006, DecayS = 0.045;
        internal const double GapBaseS = 0.06, GapSpreadS = 0.05;
        // The glow of a ground flash under the strokes: its level and decay time in s.
        internal const double GlowLevel = 0.22, GlowDecayS = 0.16;
        // How many decay times after the last stroke the flash counts as over.
        internal const double TailDecays = 6.0;

        // The start time of stroke k in s after the flash's start.
        internal static double BumpTime(ref FlashEvent e, int k)
        {
            double t = 0.0;
            for (int i = 1; i <= k; i++) t += GapBaseS + GapSpreadS * LightningSchedule.Draw(e.Seed, 10 + i);
            return t;
        }

        // The amplitude of stroke k, 1 for the first, weaker for the later ones.
        internal static double BumpAmplitude(ref FlashEvent e, int k)
        {
            double basis = k == 0 ? 1.0 : k == 1 ? 0.62 : 0.42;
            return basis * (0.85 + 0.3 * LightningSchedule.Draw(e.Seed, 20 + k));
        }

        // The length of the flash in s.
        internal static double DurationS(ref FlashEvent e)
        {
            double last = BumpTime(ref e, e.Bumps - 1);
            double tail = TailDecays * DecayS;
            if (e.Kind == FlashKind.Ground) tail = Math.Max(tail, TailDecays * GlowDecayS * 0.5);
            return last + tail;
        }

        // The light at age s after the flash's start, 0 to a little over 1 where strokes overlap.
        internal static double Level(ref FlashEvent e, double age)
        {
            if (age < 0.0) return 0.0;
            double sum = 0.0;
            for (int k = 0; k < e.Bumps; k++)
            {
                double a = age - BumpTime(ref e, k);
                if (a < 0.0) continue;
                double shape = a < RiseS ? a / RiseS : Math.Exp(-(a - RiseS) / DecayS);
                sum += BumpAmplitude(ref e, k) * shape;
            }
            if (e.Kind == FlashKind.Ground)
            {
                double rise = age < 0.02 ? age / 0.02 : 1.0;
                sum += GlowLevel * rise * Math.Exp(-age / GlowDecayS);
            }
            return sum > 1.25 ? 1.25 : sum;
        }

        // The times in s after the flash's start where a stroke begins.
        internal static void BumpTimes(ref FlashEvent e, double[] into)
        {
            for (int k = 0; k < e.Bumps && k < into.Length; k++) into[k] = BumpTime(ref e, k);
        }
    }

    // The limit on flashes per second (photosensitivity): no more than MaxBumpsPerSecond strokes begin in any window of one second. The
    // common safety threshold for flashing content is three flashes in any one second; a flash of three strokes therefore uses the whole
    // budget. A flash that would break the limit is dropped, not delayed. The limiter is a filter downstream of the schedule: the schedule
    // stays a pure function of the field and the time, the visible set depends on the flashes seen before.
    internal sealed class FlashLimiter
    {
        private readonly List<double> accepted = new List<double>();
        private readonly double[] scratch = new double[3];

        internal FlashLimiter(int maxBumpsPerSecond)
        {
            MaxBumpsPerSecond = Math.Max(1, Math.Min(maxBumpsPerSecond, 3));
        }

        internal int MaxBumpsPerSecond { get; }
        internal int Dropped { get; private set; }
        internal int Accepted { get; private set; }

        // Whether the flash may be shown; records its strokes where it may.
        internal bool TryAccept(ref FlashEvent e)
        {
            FlashEnvelope.BumpTimes(ref e, scratch);
            Prune(e.Ut - 2.0);
            int n = e.Bumps;
            // The window that holds the most strokes starts at a stroke: test each stroke time (old and new) as a window start.
            for (int pass = 0; pass < 2; pass++)
            {
                int starts = pass == 0 ? accepted.Count : n;
                for (int s = 0; s < starts; s++)
                {
                    double w0 = pass == 0 ? accepted[s] : e.Ut + scratch[s];
                    int count = 0;
                    for (int i = 0; i < accepted.Count; i++)
                        if (accepted[i] >= w0 && accepted[i] < w0 + 1.0) count++;
                    for (int k = 0; k < n; k++)
                    {
                        double t = e.Ut + scratch[k];
                        if (t >= w0 && t < w0 + 1.0) count++;
                    }
                    if (count > MaxBumpsPerSecond)
                    {
                        Dropped++;
                        return false;
                    }
                }
            }
            for (int k = 0; k < n; k++) accepted.Add(e.Ut + scratch[k]);
            accepted.Sort();
            Accepted++;
            return true;
        }

        internal void Reset()
        {
            accepted.Clear();
        }

        private void Prune(double before)
        {
            int i = 0;
            while (i < accepted.Count && accepted[i] < before) i++;
            if (i > 0) accepted.RemoveRange(0, i);
        }
    }
}
