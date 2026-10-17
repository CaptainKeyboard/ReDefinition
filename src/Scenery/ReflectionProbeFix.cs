using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scenery
{
    // KSP's flight reflection probe switched on again after it was switched off
    // outside a flight (KSP 1.12.5, decompiled).
    //
    // FlightReflectionProbe.Enable(false) sets turnOff, Enable(true) sets rendering
    // and leaves turnOff as it is; Update works through both only in a flight, and
    // there turnOff comes first: it switches the probe off and clears rendering. So
    // an Enable(true) that follows an Enable(false) before the next flight is lost.
    // That happens at every start with the reflection refresh Off in KSP's
    // settings.cfg and Deferred installed: the flight camera, and its probe, are made
    // while KSP loads, the probe reads Off and calls Enable(false); Deferred raises
    // the refresh to Low in the main menu (HandleStockProbe) and calls Enable(true);
    // in the flight the probe stays off with the refresh at Low. Deferred lights its
    // ambient from that probe and the raytracing its sky: measured at the launch
    // pad, the frame's brightness 30.6 without raytracing and 18.9 with it, against
    // 58.7 with the probe. Enable(true) here clears turnOff as well.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class ReflectionProbeFix : MonoBehaviour
    {
        private static FieldInfo turnOff;

        private void Awake()
        {
            turnOff = typeof(FlightReflectionProbe).GetField("turnOff", BindingFlags.Instance | BindingFlags.NonPublic);
            HarmonyHooks.Safely("reflection-probe", "KSP's reflection probe may stay off after the refresh is raised outside a flight", () =>
            {
                if (turnOff == null) throw new System.MissingFieldException("FlightReflectionProbe", "turnOff");
                HarmonyHooks.Install("ReDefinition.ReflectionProbeFix", typeof(ReflectionProbeFix), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(FlightReflectionProbe).GetMethod("Enable", new[] { typeof(bool) }), null,
                                    nameof(EnablePostfix), null),
                });
            });
            Destroy(this);
        }

        private static void EnablePostfix(FlightReflectionProbe __instance, bool enable)
        {
            if (enable) turnOff.SetValue(__instance, false);
        }
    }
}
