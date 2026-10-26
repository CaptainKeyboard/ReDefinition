using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Hud
{
    // The altimeter switches itself between the height above sea level and above
    // the ground, while the setting says so (OwnSettings.AutoAltimeter). It switches to the
    // ground's below Low metres over it and on the ground, and back to the sea's above
    // High. Between them it stays, so that it does not flicker at the edge.
    // KSP's own button switches it as before (AltitudeTumbler.ChangeAltimeterMode);
    // a mode set by hand stays until the rule would switch by itself again. KSP's
    // AltitudeTumbler.SetModeTumbler sets the mode, turns the drum that shows it and
    // keeps it with the vessel (Vessel.altimeterDisplayState); decompiled.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AutoAltimeter : MonoBehaviour
    {
        // In metres over the ground: the ground's height below Low, the sea's above High; and how often, in seconds, the
        // rule is checked.
        internal const double Low = 5000.0;
        internal const double High = 6000.0;
        private const float Interval = 0.25f;

        // When the rule is checked next, and the vessel it was last checked for.
        private float next;
        private Vessel vessel;
        // What the rule wants now, and what was last set here; unknown at first.
        private AltimeterDisplayState wanted = AltimeterDisplayState.DEFAULT;
        private AltimeterDisplayState set = AltimeterDisplayState.DEFAULT;

        // Every Interval: the altimeter set as the rule wants, unless the player set it by hand since and the rule has not
        // changed its mind.
        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            AltitudeTumbler tumbler = AltitudeTumbler.Instance;
            Vessel active = FlightGlobals.ActiveVessel;
            if (addon == null || !addon.Live.AutoAltimeter || tumbler == null || active == null) return;
            if (active != vessel)
            {
                vessel = active;
                wanted = set = AltimeterDisplayState.DEFAULT;
            }

            AltimeterDisplayState rule = Rule(active.LandedOrSplashed, active.radarAltitude, wanted);
            bool changed = rule != wanted;
            wanted = rule;
            // A mode set by hand since stays, until the rule changes its mind.
            if (set != AltimeterDisplayState.DEFAULT && tumbler.CurrentMode != set && !changed) return;
            if (tumbler.CurrentMode == wanted)
            {
                set = wanted;
                return;
            }
            tumbler.SetModeTumbler(wanted);
            set = wanted;
            Debug.Log(Log.Tag + " Altimeter set to " + (wanted == AltimeterDisplayState.AGL ? "the height above the ground" : "the height above sea level")
                      + " at " + active.radarAltitude.ToString("0") + " m over the ground.");
        }

        // Above the ground below Low and on it, above the sea over High, and between
        // them what it was; above the sea where nothing was decided yet.
        internal static AltimeterDisplayState Rule(bool landed, double overGround, AltimeterDisplayState before)
        {
            if (landed || overGround < Low) return AltimeterDisplayState.AGL;
            if (overGround > High) return AltimeterDisplayState.ASL;
            return before == AltimeterDisplayState.AGL ? AltimeterDisplayState.AGL : AltimeterDisplayState.ASL;
        }
    }
}
