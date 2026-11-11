using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Flight 2 of the wind probe: a Mk1 pod under a parachute over the sea (the vessel of the reconnaissance premise P4, made as KSP makes
    // a vessel), dropped from 4 km. The windows are fixed in game time: 15 s without wind (control), 30 s of 15 m/s through H1, 20 s
    // of calm, 30 s through H3, 20 s of a forced absurd wind (the clamp), then the rest of the descent at physics warp 3x with the wind on
    // (the soak). The pod and its canopy come from a synthetic proto vessel; the parts, the attach nodes and the offsets are those of the
    // reconnaissance probe (src/Atmospheres/Testing/Recon/AtmoRecon.Flight.cs, which is B09's and is not edited here).
    internal sealed partial class WindProbe
    {
        private const string PodName = "D31 Canopy";
        private const double DropAltitude = 4000.0, WindMs = 15.0;

        private double podLat, podLon, coastBearing;

        private IEnumerable<ProbeWait> ChuteScenario()
        {
            Vessel pad = GameGlobals.ActiveVessel;
            CelestialBody home = pad != null ? pad.mainBody : null;
            if (home == null || home.pqsController == null || !home.ocean)
            {
                Fail("D31: the home body has no terrain or no ocean for the canopy drop");
                yield break;
            }
            double padLat = pad.latitude, padLon = pad.longitude;
            if (!FindCoast(home, padLat, padLon))
            {
                Fail("D31: no coast within 25 km of the pad with 2 km of open sea behind it");
                yield break;
            }
            Say("coast at bearing " + Num(coastBearing) + ", pod at " + Num(podLat) + ", " + Num(podLon));

            // Out of the flight and back in with the canopy vessel.
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.SPACECENTER, 240, "the space centre");
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                Fail("D31: the space centre did not come up");
                yield break;
            }
            yield return ProbeWait.Seconds(8);
            Game game = HighLogic.CurrentGame;
            game.flightState.universalTime = Ut();
            game.flightState.protoVessels.RemoveAll(p => p.vesselName == PodName);
            int index = AddPod(game, home, podLat, podLon, DropAltitude);
            FlightDriver.StartAndFocusVessel(game, index);
            yield return ProbeWait.Until(PodReady, 240, "the canopy flight");
            if (!PodReady())
            {
                Fail("D31: the canopy flight did not come up");
                yield break;
            }
            Vessel pod = FlightGlobals.ActiveVessel;
            ModuleParachute chute = pod.FindPartModuleImplementing<ModuleParachute>();
            if (chute == null)
            {
                Fail("D31: the pod has no parachute");
                yield break;
            }
            chute.deployAltitude = 20000f;
            chute.Deploy();
            yield return ProbeWait.Until(() => chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED, 60, "the canopy open");
            Emit("D31.chute_deployed", chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED);
            if (chute.deploymentState != ModuleParachute.deploymentStates.DEPLOYED)
            {
                Fail("D31: the canopy did not open");
                yield break;
            }
            yield return ProbeWait.Seconds(6);
            Emit("D31.chute_horizontal_speed_before", pod.horizontalSrfSpeed);

            // Control: no wind.
            forced.EastMs = forced.NorthMs = 0f;
            forced.Level01 = 0.8f;
            double lat0 = pod.latitude, lon0 = pod.longitude;
            var descent = new List<double>();
            double end = Ut() + 15.0;
            Vector3d delta0 = Vector3d.zero;
            int delta0Count = 0;
            while (Ut() < end)
            {
                descent.Add(-pod.verticalSpeed);
                if (end - Ut() < 8.0)
                {
                    delta0 += (Vector3d)chute.part.dragVector - pod.srf_velocity;
                    delta0Count++;
                }
                yield return ProbeWait.Frames(1);
            }
            if (delta0Count > 0) delta0 /= delta0Count;
            double ce, cn;
            Displacement(home, lat0, lon0, pod.latitude, pod.longitude, pod.altitude, out ce, out cn);
            double vt = 0.0;
            int tail = Math.Max(1, descent.Count / 3);
            for (int i = descent.Count - tail; i < descent.Count; i++) vt += descent[i] / tail;
            double g = home.gravParameter / ((home.Radius + pod.altitude) * (home.Radius + pod.altitude));
            Emit("D31.chute_vt_ms", vt);
            Emit("D31.chute_control_along_m", Along(ce, cn));

            double predictedLate = ReconMath.PredictChuteDriftBetween(WindMs, vt, g, 15.0, 30.0);
            Emit("D31.chute_predicted_late_m", predictedLate);

            // H1.
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Auto);
            yield return ProbeWait.Seconds(1);
            Emit("D31.chute_route_h1", (double)(int)WindService.Plan.Route);
            int shifted0 = WindHook.Shifted, forces0 = WindForceModule.Applications;
            double lateH1 = double.NaN, mech = double.NaN;
            WindService.ClearProfile();
            WindService.Profile = true;
            foreach (ProbeWait w in DriftWindow(pod, home, chute.part, delta0, v => lateH1 = v, m => mech = m)) yield return w;
            WindService.Profile = false;
            // The cost of the whole per-tick path (hub sample, frame, state step; and the hook around the base call), small craft: the
            // 200-part number needs a large craft and is not measured here.
            Emit("D31.cost_compute_us", WindService.ComputeMicroseconds);
            Emit("D31.cost_hook_us_per_part", WindService.HookMicroseconds);
            Emit("D31.cost_hook_calls", WindService.HookCalls);
            Emit("D31.chute_h1_mechanism_error_rel", mech);
            Emit("D31.chute_h1_along_late_m", lateH1);
            Emit("D31.chute_h1_error_rel", predictedLate > 0.0 ? Math.Abs(lateH1 - predictedLate) / predictedLate : double.NaN);
            Emit("D31.chute_h1_shifted", WindHook.Shifted - shifted0);
            Emit("D31.chute_h1_force_applications", WindForceModule.Applications - forces0);

            // Calm, then H3.
            forced.EastMs = forced.NorthMs = 0f;
            foreach (ProbeWait w in GameSeconds(20.0)) yield return w;
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Force);
            yield return ProbeWait.Seconds(1);
            Emit("D31.chute_route_h3", (double)(int)WindService.Plan.Route);
            shifted0 = WindHook.Shifted;
            forces0 = WindForceModule.Applications;
            double lateH3 = double.NaN;
            foreach (ProbeWait w in DriftWindow(pod, home, chute.part, delta0, v => lateH3 = v, m => { })) yield return w;
            Emit("D31.chute_h3_along_late_m", lateH3);
            Emit("D31.chute_h3_error_rel", predictedLate > 0.0 ? Math.Abs(lateH3 - predictedLate) / predictedLate : double.NaN);
            Emit("D31.chute_h3_over_h1", lateH1 > 0.0 ? lateH3 / lateH1 : double.NaN);
            Emit("D31.chute_h3_shifted", WindHook.Shifted - shifted0);
            Emit("D31.chute_h3_force_applications", WindForceModule.Applications - forces0);

            // The forced absurd wind, through H1: the clamp and the cap.
            SetSetting(AtmospheresSettings.Keys.WindInjection, (double)(int)WindInjection.Auto);
            forced.EastMs = forced.NorthMs = 0f;
            foreach (ProbeWait w in GameSeconds(15.0)) yield return w;
            // The clamp is lowered to 0.3 m/s2 for this window (the seam), so that the steps of the wind-up of a canopy (about 0.6 m/s2 at the
            // slew rate) are over it; at the node's 1.5 m/s2 they are not, and the clamp then has nothing to cut.
            WindService.MaxAccelOverride = 0.3;
            SetWind(900.0);
            double minShare = 1.0, maxApplied = 0.0;
            end = Ut() + 20.0;
            while (Ut() < end)
            {
                minShare = Math.Min(minShare, WindService.LastClampShare);
                maxApplied = Math.Max(maxApplied, WindService.LastApplied.HorizontalLength);
                yield return ProbeWait.Frames(1);
            }
            Emit("D31.absurd_clamp_min_share", minShare);
            Emit("D31.absurd_applied_max_ms", maxApplied);
            Emit("D31.absurd_finite", WindService.LastApplied.IsFinite);
            WindService.MaxAccelOverride = null;

            // The soak: physics warp 3x for the rest of the descent, the wind on.
            SetWind(WindMs);
            foreach (ProbeWait w in Soak(pod)) yield return w;
        }

        private void SetWind(double speed)
        {
            double b = coastBearing * Math.PI / 180.0;
            forced.EastMs = (float)(speed * Math.Sin(b));
            forced.NorthMs = (float)(speed * Math.Cos(b));
        }

        // 30 s of the 15 m/s wind; the drift of the second half along the bearing goes to the callback.
        // The mechanism is measured as well: the canopy's dragVector less the vessel's surface velocity, against the same difference in the
        // control, is the air's velocity the aerodynamics saw; it must be the wind applied, with the sign of the air's velocity inverted.
        private IEnumerable<ProbeWait> DriftWindow(Vessel pod, CelestialBody home, Part canopy, Vector3d delta0, Action<double> late, Action<double> mechanism)
        {
            SetWind(WindMs);
            double start = Ut(), midLat = double.NaN, midLon = double.NaN;
            Vector3d sumDelta = Vector3d.zero, sumWind = Vector3d.zero;
            int count = 0;
            while (Ut() < start + 30.0)
            {
                if (Ut() - start >= 12.0)
                {
                    sumDelta += (Vector3d)canopy.dragVector - pod.srf_velocity - delta0;
                    Vec3 a = WindService.LastApplied;
                    sumWind += new Vector3d(a.X, a.Y, a.Z);
                    count++;
                }
                if (double.IsNaN(midLat) && Ut() - start >= 15.0)
                {
                    midLat = pod.latitude;
                    midLon = pod.longitude;
                }
                yield return ProbeWait.Frames(1);
            }
            double e, n;
            Displacement(home, midLat, midLon, pod.latitude, pod.longitude, pod.altitude, out e, out n);
            late(Along(e, n));
            if (count > 0)
            {
                Vector3d d = sumDelta / count, w = sumWind / count;
                mechanism(w.magnitude > 1e-6 ? (d + w).magnitude / w.magnitude : double.NaN);
            }
            Say("drift window: " + Num(Along(e, n)) + " m along the bearing in the second half");
        }

        private IEnumerable<ProbeWait> Soak(Vessel pod)
        {
            TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
            TimeWarp.SetRate(2, true, false);
            yield return ProbeWait.Frames(10);
            Emit("D31.soak_warp_rate", TimeWarp.CurrentRate);
            int bad = 0, failures0 = WindService.FailureCount + WindHook.Failures;
            double maxSpeed = 0.0, started = Ut(), lastLog = started;
            double realEnd = Time.realtimeSinceStartup + 300.0;
            while (!(pod.Splashed || pod.Landed || pod.altitude < 4.0) && Ut() - started < 600.0 && Time.realtimeSinceStartup < realEnd)
            {
                Vector3d p = pod.GetWorldPos3D();
                Vector3d vel = pod.srf_velocity;
                if (double.IsNaN(p.x) || double.IsNaN(vel.x) || double.IsInfinity(vel.x) || double.IsNaN(pod.altitude)) bad++;
                else maxSpeed = Math.Max(maxSpeed, vel.magnitude);
                if (Ut() - lastLog >= 10.0)
                {
                    lastLog = Ut();
                    Say("soak: alt " + Num(pod.altitude) + " m, speed " + Num(vel.magnitude) + " m/s, wind " + Num(WindService.LastApplied.Length) + " m/s");
                }
                yield return ProbeWait.Frames(1);
            }
            TimeWarp.SetRate(0, true, false);
            Emit("D31.soak_game_seconds", Ut() - started);
            Emit("D31.soak_nan_frames", bad);
            Emit("D31.soak_max_speed_ms", maxSpeed);
            Emit("D31.soak_failures", WindService.FailureCount + WindHook.Failures - failures0);
        }

        private bool PodReady()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return false;
            Vessel v = GameGlobals.ActiveVessel;
            return v != null && v.vesselName == PodName && v.loaded && !v.packed;
        }

        private double Along(double east, double north)
        {
            double b = coastBearing * Math.PI / 180.0;
            return east * Math.Sin(b) + north * Math.Cos(b);
        }

        private static void Displacement(CelestialBody body, double lat0, double lon0, double lat1, double lon1, double altitude, out double east, out double north)
        {
            double radius = body.Radius + altitude;
            double dLon = lon1 - lon0;
            while (dLon > 180.0) dLon -= 360.0;
            while (dLon < -180.0) dLon += 360.0;
            north = (lat1 - lat0) * Math.PI / 180.0 * radius;
            east = dLon * Math.PI / 180.0 * radius * Math.Cos(0.5 * (lat0 + lat1) * Math.PI / 180.0);
        }

        private void Say(string message)
        {
            LogLine("Probe: D31: " + message);
        }

        // The first stretch of sea along a ray from the pad that is deep for three steps in a row, nearest bearing first, with 2 km more
        // sea behind it for the drift (the reconnaissance probe's search).
        private bool FindCoast(CelestialBody home, double padLat, double padLon)
        {
            double radius = home.Radius;
            Func<double, double, double> ground = (lat, lon) => home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - radius;
            double best = double.MaxValue;
            for (int degrees = 0; degrees < 360; degrees += 15)
            {
                double bearing = degrees * Math.PI / 180.0;
                int deepSteps = 0;
                for (double distance = 400.0; distance <= 25000.0 && distance < best; distance += 100.0)
                {
                    double lat = padLat + distance * Math.Cos(bearing) / radius * 180.0 / Math.PI;
                    double lon = padLon + distance * Math.Sin(bearing) / (radius * Math.Cos(lat * Math.PI / 180.0)) * 180.0 / Math.PI;
                    deepSteps = ground(lat, lon) < -12.0 ? deepSteps + 1 : 0;
                    if (deepSteps < 3) continue;
                    bool clear = true;
                    for (double extra = 100.0; extra <= 2000.0 && clear; extra += 100.0)
                    {
                        double lat2 = padLat + (distance + extra) * Math.Cos(bearing) / radius * 180.0 / Math.PI;
                        double lon2 = padLon + (distance + extra) * Math.Sin(bearing) / (radius * Math.Cos(lat2 * Math.PI / 180.0)) * 180.0 / Math.PI;
                        clear = ground(lat2, lon2) < -12.0;
                    }
                    if (!clear)
                    {
                        deepSteps = 0;
                        continue;
                    }
                    best = distance;
                    podLat = lat;
                    podLon = lon;
                    coastBearing = degrees;
                    break;
                }
            }
            return best < double.MaxValue;
        }

        // The Mk1 pod with the Mk16 canopy on its top node, in the air over the place with the velocity of the ground under it. The part
        // offsets and nodes are those of the reconnaissance probe.
        private static int AddPod(Game game, CelestialBody body, double latitude, double longitude, double altitude)
        {
            double ut = Planetarium.GetUniversalTime();
            uint podId = ShipConstruction.GetUniqueFlightID(game.flightState);
            uint canopyId = ShipConstruction.GetUniqueFlightID(game.flightState);
            if (canopyId == podId) canopyId = podId + 1;
            ConfigNode pod = ProtoVessel.CreatePartNode("mk1pod.v2", podId);
            ConfigNode canopy = ProtoVessel.CreatePartNode("parachuteSingle", canopyId);
            pod.SetValue("parent", "0", true);
            pod.SetValue("position", "0,0,0", true);
            pod.RemoveValues("attN");
            pod.AddValue("attN", "top, 1");
            pod.AddValue("attN", "bottom, -1");
            canopy.SetValue("parent", "0", true);
            canopy.SetValue("position", "0,0.6544405,0", true);
            canopy.SetValue("rotation", "0,0,0,1", true);
            canopy.RemoveValues("attN");
            canopy.AddValue("attN", "bottom, 0");
            var orbit = new Orbit(0.0, 0.0, body.Radius + 100000.0, 0.0, 0.0, 0.0, ut, body);
            Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, altitude) - body.position;
            orbit.UpdateFromStateVectors(surface.xzy, Vector3d.Cross(body.angularVelocity, surface).xzy, body, ut);
            ConfigNode node = ProtoVessel.CreateVesselNode(PodName, VesselType.Ship, orbit, 0, new[] { pod, canopy });
            node.SetValue("sit", Vessel.Situations.FLYING.ToString());
            node.SetValue("landed", "False");
            node.SetValue("splashed", "False");
            node.SetValue("skipGroundPositioning", "True");
            node.SetValue("lat", latitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("lon", longitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("alt", altitude.ToString(CultureInfo.InvariantCulture));
            Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)body.GetRelSurfaceNVector(latitude, longitude));
            node.SetValue("rot", KSPUtil.WriteQuaternion(upright));
            game.flightState.protoVessels.Add(new ProtoVessel(node, game));
            return game.flightState.protoVessels.Count - 1;
        }
    }
}
