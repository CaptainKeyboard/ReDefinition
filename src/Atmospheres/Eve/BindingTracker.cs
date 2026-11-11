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

    // What the module has to do for a layer after one look.
    internal enum BindAction
    {
        // The binding stands.
        None,
        // The binding is not there (unbound, lost): make it, then call Bound.
        Bind,
        // The watchdog's interval is over and nothing looks lost: set the same textures again (cheap, idempotent), then call Reasserted.
        Reassert
    }

    // The state machine of one layer's binding, pure (the clock and the checks are handed in, so a test drives it with a fake material).
    // EVE discards the binding in every Apply, ApplyShaderParams and SetShaderParams and resets timeFade* and CLOUD_SHADOW_CASTER_OFF
    // with it (CloudsRaymarchedVolume.cs:543-599, 770-812). The shader declares no properties (RECON P9), so Material.GetTexture
    // cannot read the binding back: it returns null and logs an error whatever was set. A loss is therefore seen only by what can be
    // known without reading a texture:
    //   - an event: CloudsManager.onApply fired, or a material the layer holds is another object than the one bound (EVE's Apply makes
    //     new materials within the frame, RECON P9: latency 0), or the paint set the layer is bound to is another;
    //   - the watchdog's sentinel, every WatchdogIntervalS: the keywords EVE's ApplyTexture and SetShaderParams write (ALPHAMAP_1/2
    //     off, CLOUD_SHADOW_CASTER_OFF on for a layer that receives shadows) are not as the binding left them;
    //   - the watchdog's re-assertion: when nothing looks lost the same textures are set again, which restores a binding that
    //     ApplyShaderParams discarded on the same material without leaving a trace.
    // A loss is counted when it is seen. Bound closes it; the time from seeing it to the rebind is the latency. Layers with
    // timeSettings are Left, never rebound.
    internal sealed class BindingTracker
    {
        // The default time between two watchdog looks.
        internal const double WatchdogIntervalS = 1.0;

        private double lastCheck = double.NegativeInfinity;
        private double lostAt = double.NaN;
        private readonly List<double> latencies = new List<double>();

        internal BindingTracker(double watchdogIntervalS = WatchdogIntervalS)
        {
            IntervalS = watchdogIntervalS;
            State = BindState.Unbound;
        }

        // The time between two watchdog looks; the module sets it from the tuning key eve.watchdogS.
        internal double IntervalS { get; set; }

        internal BindState State { get; private set; }

        // The number of losses seen, by event and by the watchdog's sentinel, and how many of them the sentinel found alone.
        internal int Losses { get; private set; }
        internal int WatchdogFinds { get; private set; }
        internal int Rebinds { get; private set; }

        // How many times the watchdog set the textures again with no loss seen.
        internal int Reasserts { get; private set; }

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

        // The textures were set again by the watchdog.
        internal void Reasserted(double now)
        {
            if (State != BindState.Bound) return;
            Reasserts++;
            lastCheck = now;
        }

        // Called every frame. applyEvent is true once after CloudsManager.onApply, identityChanged when a material or the paint set is
        // not the one bound. sentinel reads the keywords back (true = as left); it is called at most once per watchdog interval.
        internal BindAction Observe(double now, bool applyEvent, bool identityChanged, Func<bool> sentinel)
        {
            switch (State)
            {
                case BindState.Left:
                    return BindAction.None;
                case BindState.Unbound:
                    return BindAction.Bind;
                case BindState.Lost:
                    return BindAction.Bind;
            }
            if (applyEvent || identityChanged)
            {
                Lose(now, false);
                return BindAction.Bind;
            }
            if (now - lastCheck >= IntervalS)
            {
                lastCheck = now;
                if (sentinel != null && !sentinel())
                {
                    Lose(now, true);
                    return BindAction.Bind;
                }
                return BindAction.Reassert;
            }
            return BindAction.None;
        }

        private void Lose(double now, bool bySentinel)
        {
            Losses++;
            if (bySentinel) WatchdogFinds++;
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
