using System;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Autopilot.Flight
{
    // Holds back the time warp the player turns up while the autopilot or the navigation has the vessel: it is slowed as the next thing the vessel has
    // to do comes near, and is off when that is a burn. The player's warp is only cut back, never raised. MechJeb's own warp to a burn goes on as it is.
    internal static class WarpGuard
    {
        // The warp is cut back this many fixed steps before the event: the vessel must not skip over it in a frame.
        private const double StepsAhead = 4.0;
        // A burn is waited for this long after its node, and the warp stays off so long, over the time it takes.
        private const double BurnSlack = 5.0;
        private static double toldFor = double.MinValue;
        // The UT the autopilot warps to by itself, with the game's own warp to a time; zero when it does not.
        internal static double SelfWarpUntil;

        internal static void Limit(Vessel vessel)
        {
            TimeWarp warp = TimeWarp.fetch;
            if (warp == null || vessel == null || vessel.orbit == null || TimeWarp.WarpMode != TimeWarp.Modes.HIGH || TimeWarp.CurrentRateIndex == 0) return;
            string what;
            bool hold;
            double at = NextEvent(vessel, out what, out hold);
            if (double.IsPositiveInfinity(at)) return;
            double ahead = at - Planetarium.GetUniversalTime();
            int index = TimeWarp.CurrentRateIndex;
            int slower = index;
            if (hold) slower = 0;
            else
                while (slower > 0 && warp.warpRates[slower] * Time.fixedDeltaTime * StepsAhead > ahead) slower--;
            if (slower == index) return;
            TimeWarp.SetRate(slower, true, false);
            if (Math.Abs(toldFor - at) < 1.0) return;
            toldFor = at;
            Debug.Log(Log.Tag + " Route: the time warp is " + (hold ? "off" : "slowed") + ", " + Math.Max(0.0, ahead).ToString("0") + " s before " + what + ".");
        }

        // The time of the next event the vessel has to be at the pace of: the start of a burn (and the burn), the change of the body it is round, the air, the
        // ground. With `hold` set the warp is to be off now. Infinity where there is none.
        private static double NextEvent(Vessel vessel, out string what, out bool hold)
        {
            what = null;
            hold = false;
            double now = Planetarium.GetUniversalTime();
            double next = double.PositiveInfinity;
            // MechJeb's executor warps to its burn itself, and the guard leaves a node to it.
            bool handled = MechJebDriver.Busy(vessel);
            double acceleration = !handled && vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes.Count > 0 ? Engines.Acceleration(vessel) : 0.0;
            if (!handled && vessel.patchedConicSolver != null)
                foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes)
                {
                    // The guide of a climb or a descent is no burn to wait for.
                    if (Guidance.GuideNode.Is(node)) continue;
                    double burn = acceleration > 0.0 ? Math.Min(node.DeltaV.magnitude / acceleration, 3600.0) : 0.0;
                    double begins = node.UT - 2.0 * NodePlanner.LeadTime - 0.5 * burn;
                    double ends = node.UT + 0.5 * burn + BurnSlack;
                    if (now > ends) continue;
                    if (now >= begins)
                    {
                        what = "the burn";
                        hold = true;
                        return begins;
                    }
                    if (begins < next)
                    {
                        next = begins;
                        what = "the burn";
                    }
                }
            // The autopilot's own warp to the end of the orbit or to the air goes on as it is.
            if (now < SelfWarpUntil + 2.0) return next;
            Orbit orbit = vessel.orbit;
            // The guide of a climb or a descent ends the orbit a moment from now, and is no change of it.
            if (orbit.patchEndTransition != Orbit.PatchTransitionType.FINAL && orbit.EndUT > now && orbit.EndUT < next && !Guidance.GuideNode.Holds(vessel))
            {
                next = orbit.EndUT;
                what = "the change of orbit";
            }
            CelestialBody body = orbit.referenceBody;
            if (body != null && !vessel.LandedOrSplashed && vessel.altitude > 0.0)
            {
                double top = body.atmosphere ? body.atmosphereDepth : 0.0;
                double entry = TimeToAltitude(orbit, body.Radius + top, now);
                if (orbit.PeA < top && entry > now && entry < next)
                {
                    next = entry;
                    what = body.atmosphere ? "the air" : "the ground";
                }
            }
            return next;
        }

        // When the orbit comes down to a radius on the way in, in UT; zero where it does not, or the vessel is below it.
        private static double TimeToAltitude(Orbit orbit, double radius, double now)
        {
            if (orbit.PeR >= radius) return 0.0;
            if (orbit.eccentricity < 1.0 && orbit.ApR <= radius) return 0.0;
            double anomaly = orbit.TrueAnomalyAtRadius(radius);
            if (double.IsNaN(anomaly) || orbit.meanMotion <= 0.0) return 0.0;
            double before = Math.Abs(orbit.GetMeanAnomaly(orbit.GetEccentricAnomaly(anomaly))) / orbit.meanMotion;
            return now + orbit.timeToPe - before;
        }
    }
}
