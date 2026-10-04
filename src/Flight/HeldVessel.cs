using System;

namespace ReDefinition.Flight
{
    // A vessel a static cache was worked out for, held without keeping it, and
    // the parts it hangs on to, alive once its scene is gone.
    internal sealed class HeldVessel
    {
        private WeakReference<Vessel> held;

        internal bool Is(Vessel vessel)
        {
            Vessel kept;
            if (held == null) return ReferenceEquals(vessel, null);
            return held.TryGetTarget(out kept) && ReferenceEquals(kept, vessel);
        }

        internal void Set(Vessel vessel)
        {
            if (Is(vessel)) return;
            held = ReferenceEquals(vessel, null) ? null : new WeakReference<Vessel>(vessel);
        }
    }
}
