using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // With the main mod's GPU profiler switch (-redefinitionGpuProfile): the main-thread milliseconds each Atmospheres module's Tick takes, and the own cloud pass's
    // OnPreCull work (fill, then shadow/record/motion), summed and at the worst over one second, in one log line a second. Nothing is measured without the switch.
    internal static class ModuleCpuTiming
    {
        private sealed class Slot
        {
            internal double Sum, Worst;
            internal int Calls;
        }

        private static readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double nextReport = 1.0;

        // The start of a measurement: a timestamp, or -1 while the switch is off.
        internal static long Begin()
        {
            return HostSeams.GpuProfileOn() ? Stopwatch.GetTimestamp() : -1L;
        }

        internal static void End(string name, long started)
        {
            if (started < 0) return;
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            Slot slot;
            if (!slots.TryGetValue(name, out slot)) slots[name] = slot = new Slot();
            slot.Sum += ms;
            slot.Calls++;
            if (ms > slot.Worst) slot.Worst = ms;
        }

        // Called once a frame: writes the line when a second has passed. Entries under 0.02 ms a second are left out.
        internal static void Report()
        {
            if (slots.Count == 0 || clock.Elapsed.TotalSeconds < nextReport) return;
            nextReport = clock.Elapsed.TotalSeconds + 1.0;
            var text = new StringBuilder("CPU main thread, last second (sum ms / worst ms / calls):");
            double total = 0.0;
            foreach (KeyValuePair<string, Slot> pair in slots)
            {
                total += pair.Value.Sum;
                if (pair.Value.Sum >= 0.02)
                    text.Append("  ").Append(pair.Key).Append(' ').Append(pair.Value.Sum.ToString("0.0", CultureInfo.InvariantCulture)).Append('/')
                        .Append(pair.Value.Worst.ToString("0.0", CultureInfo.InvariantCulture)).Append('/').Append(pair.Value.Calls);
            }
            text.Append("  total ").Append(total.ToString("0.0", CultureInfo.InvariantCulture));
            slots.Clear();
            AtmospheresLog.Say(text.ToString());
        }
    }
}
