using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // Where the binding of one EVE layer to our maps is.
    internal enum BindState
    {
        // Not bound yet (or the layer is gone).
        Unbound,
        // The materials carry our maps.
        Bound,
        // EVE took the maps away (Apply, ApplyShaderParams, SetShaderParams, a new material) and the binding waits to be made again.
        Lost,
        // The layer has timeSettings, or its members are not as assumed: EVE owns the layer and we do not fight it.
        Left
    }

    // The state machine of one layer's binding, pure (the clock and the check are handed in, so a test drives it with a fake material).
    // EVE discards the binding in every Apply, ApplyShaderParams and SetShaderParams and resets timeFade* and
    // CLOUD_SHADOW_CASTER_OFF with it (CloudsRaymarchedVolume.cs:543-599, 770-812). Two things make the loss visible:
    //   - an event: CloudsManager.onApply fired, or the material the layer holds is another object than the one bound;
    //   - the watchdog: every WatchdogIntervalS the binding is read back (the texture of the property is ours, the keywords and the
    //     alpha mask are as they were set) and a mismatch is a loss, however it came.
    // A loss is counted when it is seen. Rebound closes it; the time from seeing it to the rebind is the latency. Layers with
    // timeSettings are Left, never rebound.
    internal sealed class BindingTracker
    {
        // The default time between two read-backs.
        internal const double WatchdogIntervalS = 1.0;

        private readonly double interval;
        private double lastCheck = double.NegativeInfinity;
        private double lostAt = double.NaN;
        private readonly List<double> latencies = new List<double>();

        internal BindingTracker(double watchdogIntervalS = WatchdogIntervalS)
        {
            interval = watchdogIntervalS;
            State = BindState.Unbound;
        }

        internal BindState State { get; private set; }

        // The number of losses seen, by event and by the watchdog, and how many of them the watchdog found alone.
        internal int Losses { get; private set; }
        internal int WatchdogFinds { get; private set; }
        internal int Rebinds { get; private set; }

        // The longest and the last time from a seen loss to its rebind, in seconds.
        internal double MaxLatencyS { get; private set; }
        internal double LastLatencyS { get; private set; }

        internal IList<double> Latencies
        {
            get { return latencies; }
        }

        // The layer is not ours to bind (timeSettings, or members missing).
        internal void Leave()
        {
            State = BindState.Left;
        }

        // The first binding or a rebind was made at that time.
        internal void Bound(double now)
        {
            if (State == BindState.Left) return;
            if (State == BindState.Lost && !double.IsNaN(lostAt))
            {
                LastLatencyS = now - lostAt;
                if (LastLatencyS > MaxLatencyS) MaxLatencyS = LastLatencyS;
                latencies.Add(LastLatencyS);
                Rebinds++;
            }
            lostAt = double.NaN;
            State = BindState.Bound;
            lastCheck = now;
        }

        // Called every frame. applyEvent is true once after CloudsManager.onApply, materialChanged when the layer's material is not the
        // bound one. readBack reads the binding back (true = intact); it is called at most once per watchdog interval. Returns true
        // when the binding has to be made now (Unbound, or Lost).
        internal bool Observe(double now, bool applyEvent, bool materialChanged, Func<bool> readBack)
        {
            switch (State)
            {
                case BindState.Left:
                    return false;
                case BindState.Unbound:
                    return true;
                case BindState.Lost:
                    return true;
            }
            if (applyEvent || materialChanged)
            {
                Lose(now, false);
                return true;
            }
            if (now - lastCheck >= interval)
            {
                lastCheck = now;
                if (readBack != null && !readBack())
                {
                    Lose(now, true);
                    return true;
                }
            }
            return false;
        }

        private void Lose(double now, bool byWatchdog)
        {
            Losses++;
            if (byWatchdog) WatchdogFinds++;
            lostAt = now;
            State = BindState.Lost;
        }

        // The layer is gone: back to Unbound, the counters stay.
        internal void Reset()
        {
            State = BindState.Unbound;
            lostAt = double.NaN;
            lastCheck = double.NegativeInfinity;
        }
    }
}
