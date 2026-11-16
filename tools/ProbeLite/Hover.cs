using System;
using UnityEngine;

namespace ProbeLite
{
    // Lifts the active vessel over the pad and holds it there, as K-S2's viewer does (CloudViewer.PlaceAt, KspT09World.PlaceVessel and T09Hold): the vessel is set to a
    // point (position, upright, floating origin, velocity of the ground under it, engines off, no crash damage) and then kept there: before every physics step its position
    // is set to a command and its velocity to the ground's; every frame the vessel's centre of mass is read, and where the read is more than SettleToleranceM from the target
    // the command moves by what is missing (the read is of the centre of mass, the set is of the transform, about 9.4 m apart, and the read lags a step or two). Written again
    // from that description for this plugin, with the same constants; the logic is the small part of T09Hold that the placement needs.
    internal static class Hover
    {
        internal const double ToleranceM = 5.0, SettleToleranceM = 2.0, ReadAgreementM = 1.0, CommandLimitM = 40.0;
        internal const int CommandEveryFrames = 10, StableNeededFrames = 30, GiveUpFrames = 150;

        // The vessel's height over the ground for the poses: the far poses take FarAglM (config hoverFarM, 0 = the vessel stays on the pad), the ground poses K-S2's 6.25 m
        // (the camera 5.2 m over the ground and 40 m behind a vessel that is 1.5 degrees up from it).
        internal static double FarAglM = 0.0;
        internal static readonly double GroundAglM = 5.2 + 40.0 * Math.Sin(1.5 * Math.PI / 180.0);

        private static bool active;
        private static double tLat, tLon, tAlt, cLat, cLon, cAlt, lastLat, lastLon, lastAlt;
        private static bool haveLast, holding, unreached;
        private static int frames, sinceCommand, agreeing, stable, corrections;

        internal static bool Enabled { get { return FarAglM > 0.0; } }
        internal static bool Stable { get { return holding && !unreached && stable >= StableNeededFrames; } }
        internal static int Corrections { get { return corrections; } }

        internal static double AglFor(PoseSpec pose)
        {
            return pose.CamAglM > 50.0 ? FarAglM : GroundAglM;
        }

        private static double Distance(CelestialBody body, double lat1, double lon1, double alt1, double lat2, double lon2, double alt2)
        {
            return (body.GetWorldSurfacePosition(lat1, lon1, alt1) - body.GetWorldSurfacePosition(lat2, lon2, alt2)).magnitude;
        }

        private static double WrapLon(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        // Puts the vessel aglM over the PQS terrain at the site and starts the hold.
        internal static void Place(double aglM)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || double.IsNaN(Rig.SiteLat)) return;
            CelestialBody body = v.mainBody;
            double lat = Rig.SiteLat, lon = Rig.SiteLon;
            double alt = Math.Max(0.0, body.TerrainAltitude(lat, lon, true)) + aglM;
            Vector3d up = body.GetSurfaceNVector(lat, lon);
            Vector3d place = body.GetWorldSurfacePosition(lat, lon, alt);
            v.SetPosition(place);
            v.SetRotation(Quaternion.FromToRotation(Vector3.up, (Vector3)up));
            FloatingOrigin.SetOffset(place);
            Krakensbane.ResetVelocityFrame(true);
            v.SetWorldVelocity(body.getRFrmVel(v.GetWorldPos3D()));
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            try
            {
                FlightInputHandler.state.mainThrottle = 0f;
                foreach (ModuleEngines engine in v.FindPartModulesImplementing<ModuleEngines>())
                    if (engine.EngineIgnited) engine.Shutdown();
            }
            catch (Exception) { /* no engines or no input handler yet */ }
            tLat = cLat = lat; tLon = cLon = lon; tAlt = cAlt = alt;
            holding = false; unreached = false; haveLast = false;
            frames = sinceCommand = agreeing = stable = corrections = 0;
            active = true;
            Lite.Say("hover: vessel placed " + Lite.F(aglM) + " m over the ground at " + Lite.F(lat) + ", " + Lite.F(lon) + " (" + Lite.F(alt) + " m)");
        }

        internal static void Stop()
        {
            active = false;
        }

        private static void Read(Vessel v, out double lat, out double lon, out double alt)
        {
            v.mainBody.GetLatLonAlt(v.GetWorldPos3D(), out lat, out lon, out alt);
        }

        private static void KeepVelocity(Vessel v)
        {
            try { FlightInputHandler.state.mainThrottle = 0f; } catch (Exception) { /* no input handler */ }
            v.SetWorldVelocity(v.mainBody.getRFrmVel(v.GetWorldPos3D()));
            try
            {
                foreach (Part p in v.parts)
                {
                    Rigidbody rb = p.Rigidbody;
                    if (rb != null && !rb.isKinematic) rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception) { /* parts not built yet */ }
        }

        // Before every physics step.
        internal static void FixedTick()
        {
            if (!active || !FlightGlobals.ready) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            v.SetPosition(v.mainBody.GetWorldSurfacePosition(cLat, cLon, cAlt));
            KeepVelocity(v);
        }

        // Every frame.
        internal static void Tick()
        {
            if (!active || !FlightGlobals.ready) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            CelestialBody body = v.mainBody;
            double lat, lon, alt;
            Read(v, out lat, out lon, out alt);
            frames++;
            sinceCommand++;
            double away = Distance(body, lat, lon, alt, tLat, tLon, tAlt);
            if (haveLast && Distance(body, lat, lon, alt, lastLat, lastLon, lastAlt) <= ReadAgreementM) agreeing++;
            else agreeing = 0;
            lastLat = lat; lastLon = lon; lastAlt = alt; haveLast = true;
            bool settled = sinceCommand >= CommandEveryFrames && agreeing >= 2;
            if (!holding)
            {
                if (settled && away <= ToleranceM)
                {
                    holding = true;
                    stable = 0;
                    Lite.Say("hover: anchored after " + frames + " frames, " + Lite.F(away) + " m from the placed point");
                }
                else if (frames >= GiveUpFrames)
                {
                    holding = true;
                    unreached = true;
                    stable = 0;
                    Lite.Warn("hover: the vessel is still " + Lite.F(away) + " m from the placed point after " + frames + " frames; holding the placed point");
                }
                else if (settled && away > SettleToleranceM) Command(body, lat, lon, alt);
                return;
            }
            if (away <= ToleranceM) stable++;
            else stable = 0;
            if (settled && away > SettleToleranceM)
            {
                Command(body, lat, lon, alt);
                corrections++;
            }
            else if (unreached && settled && away <= ToleranceM) unreached = false;
        }

        // The command moves by what the settled read is away from the target (within CommandLimitM of it) and is set at once.
        private static void Command(CelestialBody body, double lat, double lon, double alt)
        {
            cLat += tLat - lat;
            cLon += WrapLon(tLon - lon);
            cAlt += tAlt - alt;
            double offset = Distance(body, cLat, cLon, cAlt, tLat, tLon, tAlt);
            if (offset > CommandLimitM)
            {
                double k = CommandLimitM / offset;
                cLat = tLat + (cLat - tLat) * k;
                cLon = tLon + WrapLon(cLon - tLon) * k;
                cAlt = tAlt + (cAlt - tAlt) * k;
            }
            FlightGlobals.ActiveVessel.SetPosition(body.GetWorldSurfacePosition(cLat, cLon, cAlt));
            sinceCommand = 0;
            agreeing = 0;
            stable = 0;
        }
    }
}
