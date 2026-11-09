using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ReDefinition.Core;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Engineer
{
    // With -redefinitionPlannerTiming: what the planner's work costs the main thread, by part. Every 10 s the log gets each timed part's calls,
    // its total and its longest call in milliseconds. Without the switch a call costs one comparison.
    internal static class PlannerTiming
    {
        private const string Argument = "-redefinitionPlannerTiming";
        private const float Period = 10f;
        private static readonly bool On = CommandLine.Has(Argument);
        private static readonly double MillisecondsPerTick = 1000.0 / Stopwatch.Frequency;

        private sealed class Sum
        {
            public int Calls;
            public long Ticks;
            public long Longest;
        }

        private static readonly Dictionary<string, Sum> sums = new Dictionary<string, Sum>();
        private static float nextLog;

        // The time a part starts at; zero while the timing is off.
        internal static long Start()
        {
            return On ? Stopwatch.GetTimestamp() : 0L;
        }

        // Adds the call that started at `start` to the part's sums, and writes the log where the period is over.
        internal static void Stop(string part, long start)
        {
            if (!On) return;
            long ticks = Stopwatch.GetTimestamp() - start;
            Sum sum;
            if (!sums.TryGetValue(part, out sum)) sums[part] = sum = new Sum();
            sum.Calls++;
            sum.Ticks += ticks;
            if (ticks > sum.Longest) sum.Longest = ticks;
            if (UnityEngine.Time.realtimeSinceStartup < nextLog) return;
            nextLog = UnityEngine.Time.realtimeSinceStartup + Period;
            var text = new StringBuilder(Log.Tag + " Planner timing, last " + Period.ToString("0") + " s:");
            foreach (KeyValuePair<string, Sum> pair in sums)
                text.Append("\n  ").Append(pair.Key.PadRight(22)).Append(pair.Value.Calls.ToString().PadLeft(6)).Append(" calls ")
                    .Append((pair.Value.Ticks * MillisecondsPerTick).ToString("0.0").PadLeft(9)).Append(" ms total ")
                    .Append((pair.Value.Longest * MillisecondsPerTick).ToString("0.00").PadLeft(8)).Append(" ms longest");
            Debug.Log(text.ToString());
            sums.Clear();
        }
    }
}
