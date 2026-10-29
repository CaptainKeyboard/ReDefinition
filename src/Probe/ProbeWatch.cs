using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // What a probe flight watches for, by the game's own events, so that it does not wait for a line that never comes: the vessel destroyed or
    // crashed. The moment it happens a line is written that the scripts that run the probes end on ("Probe: vessel lost"), and the probe's
    // loops stop.
    internal static class ProbeWatch
    {
        // The line the scripts end a run on.
        internal const string LostLine = "Probe: vessel lost";

        private static Vessel watched;
        private static bool armed;
        // KSP's events keep the delegate's target and fail on a static method: the handlers are an object's.
        private static readonly Handlers handlers = new Handlers();

        private sealed class Handlers
        {
            public void OnDestroy(Vessel vessel)
            {
                ProbeWatch.OnDestroy(vessel);
            }

            public void OnCrash(EventReport report)
            {
                ProbeWatch.OnCrash(report);
            }
        }

        // Whether the watched vessel was lost, and what happened to it.
        internal static bool Lost { get; private set; }
        internal static string Why { get; private set; }

        // Begins to watch the vessel; the events are taken once.
        internal static void Arm(Vessel vessel)
        {
            watched = vessel;
            Lost = false;
            Why = "";
            if (armed) return;
            armed = true;
            GameEvents.onVesselDestroy.Add(handlers.OnDestroy);
            GameEvents.onCrash.Add(handlers.OnCrash);
        }

        // Whether the vessel is gone: lost by an event, or dead, or not there.
        internal static bool Gone(Vessel vessel)
        {
            if (!Lost && (vessel == null || vessel.state == Vessel.State.DEAD)) Mark("the vessel is gone");
            return Lost;
        }

        private static void OnDestroy(Vessel vessel)
        {
            if (vessel != null && watched != null && vessel.id == watched.id) Mark("destroyed: " + vessel.vesselName);
        }

        private static void OnCrash(EventReport report)
        {
            if (report != null && report.origin != null && report.origin.vessel != null && watched != null && report.origin.vessel.id == watched.id)
                Mark("crash of " + report.origin.partInfo.title + " at " + report.origin.vessel.srfSpeed.ToString("0") + " m/s");
        }

        private static void Mark(string why)
        {
            if (Lost) return;
            Lost = true;
            Why = why;
            Debug.Log(Log.Tag + " " + LostLine + ": " + why + ".");
        }
    }
}
