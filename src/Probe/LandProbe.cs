using System;
using System.Collections;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeLand <body>: the probe's flight is a Mk1 command pod on that body's ground, not on the
    // launch pad, to look at its light with the raytracing off and on (ViewProbe).
    // -redefinitionProbeLandLongitude <degrees> says where. The nearest dry, level place on the equator or near it is taken.
    // The sun's height there depends on the time and the longitude, and the probe reports it in the flight.
    // With -redefinitionProbeLandAdjust it quits where the sun stands below 20 or above 50 degrees.
    // It names the longitude that puts the sun at 30 degrees in the morning, for tools/planet_shots.ps1 to start again with.
    internal static class LandProbe
    {
        internal const string Argument = "-redefinitionProbeLand";
        private const string VesselName = "ReDefinition land probe";
        // The sun's height the report asks for, and the lowest and highest the first run accepts, in degrees.
        private const double WantedSun = 30.0, LowestSun = 20.0, HighestSun = 50.0;

        // The body asked for, or null.
        internal static string BodyName
        {
            get { return CommandLine.After(Argument); }
        }

        // -redefinitionProbeOrbit <body>: the pod in a circular orbit round the body instead, for the gas giants,
        // which have no ground. -redefinitionProbeOrbitAltitude <metres> says how high, 600 km without it.
        // The pod stands where the sun lights the side of the body the camera looks at, 55 degrees from the sub-solar point.
        internal const string OrbitArgument = "-redefinitionProbeOrbit";
        private const double OrbitPhase = 55.0;

        // The body of the orbit asked for, or null.
        internal static string OrbitBodyName
        {
            get { return CommandLine.After(OrbitArgument); }
        }

        // -redefinitionProbeHover <metres>, with -redefinitionProbeOrbit: in the flight the pod is set down in the air at that
        // altitude over the point where the sun stands 35 degrees high, and held there at rest in the air. A pod
        // let fall would be crushed or burnt in seconds. Jool's air reaches 200 km and crushes below 0 m.
        internal const string HoverArgument = "-redefinitionProbeHover";

        // Whether the pod is to hang in the air, and how high.
        internal static bool HoverAltitude(out double metres)
        {
            metres = 0.0;
            string text = CommandLine.After(HoverArgument);
            return text != null && OrbitBodyName != null
                   && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out metres);
        }

        // Moves the active vessel to the altitude over the lit side of its body and holds it there, upright, at rest
        // in the air. The sun stands 35 degrees above the horizon there (OrbitPhase). Runs for as long as the flight does.
        internal static IEnumerator Hover(double altitude)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) yield break;
            CelestialBody body = vessel.mainBody;
            Vector3d toSun = (FlightGlobals.Bodies[0].position - body.position).normalized;
            Vector3d up = (QuaternionD.AngleAxis(OrbitPhase, body.RotationAxis) * toSun).normalized;
            Vector3d place = body.position + up * (body.Radius + altitude);
            Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)up);
            vessel.SetPosition(place);
            vessel.SetRotation(upright);
            FloatingOrigin.SetOffset(place);
            Debug.Log(Log.Tag + " Probe: hover: the capsule set down over " + body.bodyName + " at " + altitude.ToString("0", CultureInfo.InvariantCulture)
                      + " m, the sun " + (90.0 - OrbitPhase).ToString("0", CultureInfo.InvariantCulture) + " degrees high.");
            // Krakensbane carries the orbital speed in the world's frame; it goes into the bodies and the pod is set at rest in
            // the air, which turns with the body.
            Krakensbane.ResetVelocityFrame(true);
            vessel.SetWorldVelocity(body.getRFrmVel(vessel.GetWorldPos3D()));
            // Held in place: its bodies take no forces, so that it neither falls nor is slowed. The air's heat on it, at a
            // speed over the turning body, would destroy it, so the game's own cheat for that is on.
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            while (vessel != null && vessel.state != Vessel.State.DEAD)
            {
                foreach (Part part in vessel.parts)
                    if (part.rb != null && !part.rb.isKinematic)
                    {
                        part.rb.velocity = Vector3.zero;
                        part.rb.angularVelocity = Vector3.zero;
                        part.rb.isKinematic = true;
                    }
                yield return new WaitForFixedUpdate();
            }
        }

        // The pod in a circular equatorial orbit round the body, its index in the game's vessels, or -1.
        internal static int OrbitCapsule(Game game)
        {
            string name = OrbitBodyName;
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == name);
            if (body == null || body.orbit == null || body.referenceBody == null)
            {
                Debug.LogError(Log.Tag + " Probe: orbit: no body " + name + " that orbits another.");
                return -1;
            }
            double altitude;
            string text = CommandLine.After("-redefinitionProbeOrbitAltitude");
            if (text == null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out altitude)) altitude = 600000.0;
            // The game's own time at the flight's start, which -redefinitionProbeUniversalTime sets: the orbit is made for it.
            double ut = game.flightState.universalTime;
            double semiMajorAxis = body.Radius + altitude;
            // Both orbits give their positions in the same axes: the sun lies opposite the body's own position.
            Vector3d toSun = (-body.orbit.getRelativePositionAtUT(ut)).normalized;
            // -redefinitionProbeReentry: an orbit that dips into the air instead (EntryOrbit).
            Orbit entry = CommandLine.Has(ReentryProbe.Argument) ? EntryOrbit(body, ut, toSun) : null;
            // -redefinitionProbeOrbitToward <body>: the pod on the line from the planet to that body instead, so that the body
            // stands over it, to look at (MoonSkyProbe).
            string towardName = CommandLine.After("-redefinitionProbeOrbitToward");
            CelestialBody toward = towardName != null ? FlightGlobals.Bodies.Find(b => b.bodyName == towardName) : null;
            Vector3d aimed = toward != null ? toward.orbit.getRelativePositionAtUT(ut).normalized : toSun;
            // -redefinitionProbeOrbitAngle <degrees>: how far from that line the pod stands, so that the body is that far
            // under the zenith and the camera need not look straight up (the camera's heading is lost there).
            double wantedAngle = OrbitPhase, angleAsked;
            if (toward != null)
                wantedAngle = double.TryParse(CommandLine.After("-redefinitionProbeOrbitAngle") ?? "", NumberStyles.Float,
                                              CultureInfo.InvariantCulture, out angleAsked) ? angleAsked : 0.0;
            double bestAnomaly = 0.0, bestError = double.MaxValue, bestAngle = 0.0;
            for (int degrees = 0; degrees < 360; degrees++)
            {
                var trial = new Orbit(0.0, 0.0, semiMajorAxis, 0.0, 0.0, degrees * Math.PI / 180.0, ut, body);
                double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(trial.getRelativePositionAtUT(ut).normalized, aimed)))) * 180.0 / Math.PI;
                double error = Math.Abs(angle - wantedAngle);
                // Toward a body, the pod stands on the night side where it can: of the places that are as far from the line as
                // asked, the one farthest from the sun.
                if (toward != null)
                {
                    double sunAngle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(trial.getRelativePositionAtUT(ut).normalized, toSun))));
                    error -= sunAngle * 0.1;
                }
                if (error < bestError)
                {
                    bestError = error;
                    bestAngle = angle;
                    bestAnomaly = degrees * Math.PI / 180.0;
                }
            }
            game.flightState.protoVessels.RemoveAll(v => v.vesselName == VesselName);
            ConfigNode part = ProtoVessel.CreatePartNode("mk1pod.v2", ShipConstruction.GetUniqueFlightID(game.flightState));
            Orbit orbit = entry ?? new Orbit(0.0, 0.0, semiMajorAxis, 0.0, 0.0, bestAnomaly, ut, body);
            ConfigNode node = ProtoVessel.CreateVesselNode(VesselName, VesselType.Ship, orbit, 0, new[] { part });
            node.SetValue("sit", Vessel.Situations.ORBITING.ToString());
            node.SetValue("landed", "False");
            node.SetValue("splashed", "False");
            game.flightState.protoVessels.Add(new ProtoVessel(node, game));
            Debug.Log(Log.Tag + " Probe: orbit: a capsule round " + name + " at " + altitude.ToString("0", CultureInfo.InvariantCulture)
                      + " m, " + (bestAnomaly * 180.0 / Math.PI).ToString("0", CultureInfo.InvariantCulture) + " degrees from the periapsis, "
                      + bestAngle.ToString("0", CultureInfo.InvariantCulture) + " degrees from the sub-solar point.");
            return game.flightState.protoVessels.Count - 1;
        }

        // An equatorial orbit that comes down into the air as a return from the Mun's height does: its periapsis
        // (-redefinitionProbeReentryPeriapsis, 45 km up without it) in the air, its apoapsis out at 11 500 km. The pod starts on its
        // way down at 120 km, over the side of the planet the sun lights, 55 degrees from the sub-solar point.
        private static Orbit EntryOrbit(CelestialBody body, double ut, Vector3d toSun)
        {
            double periapsis;
            if (!double.TryParse(CommandLine.After("-redefinitionProbeReentryPeriapsis") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out periapsis))
                periapsis = 45000.0;
            double rp = body.Radius + periapsis, ra = body.Radius + 11500000.0, r0 = body.Radius + 120000.0;
            double a = 0.5 * (rp + ra), e = (ra - rp) / (ra + rp);
            double eccentric = Math.Acos(Math.Max(-1.0, Math.Min(1.0, (1.0 - r0 / a) / e)));
            double mean = 2.0 * Math.PI - (eccentric - e * Math.Sin(eccentric));
            double bestError = double.MaxValue, bestArgument = 0.0;
            for (int degrees = 0; degrees < 360; degrees++)
            {
                var trial = new Orbit(0.0, e, a, 0.0, degrees, mean, ut, body);
                double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(trial.getRelativePositionAtUT(ut).normalized, toSun)))) * 180.0 / Math.PI;
                if (Math.Abs(angle - OrbitPhase) < bestError)
                {
                    bestError = Math.Abs(angle - OrbitPhase);
                    bestArgument = degrees;
                }
            }
            Debug.Log(Log.Tag + " Probe: reentry: an orbit from " + (r0 - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " m down to a periapsis at "
                      + periapsis.ToString("0", CultureInfo.InvariantCulture) + " m, apoapsis " + (ra - body.Radius).ToString("0", CultureInfo.InvariantCulture) + " m.");
            return new Orbit(0.0, e, a, 0.0, bestArgument, mean, ut, body);
        }

        // Where the pod is asked to stand, in degrees north: -redefinitionProbeLandLatitude, the equator without it.
        private static double Latitude()
        {
            double value;
            string text = CommandLine.After("-redefinitionProbeLandLatitude");
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0.0;
        }

        // Where the pod is asked to stand, in degrees east.
        private static double Longitude()
        {
            double value;
            string text = CommandLine.After("-redefinitionProbeLandLongitude");
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0.0;
        }

        // The pod on the body's ground, made as KSP makes a vessel and set on the ground by KSP; its index in the game's
        // vessels, or -1 where the body has no ground to stand on.
        internal static int Capsule(Game game)
        {
            string name = BodyName;
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.bodyName == name);
            if (body == null || body.pqsController == null)
            {
                Debug.LogError(Log.Tag + " Probe: land: no body " + name + " with ground.");
                return -1;
            }
            double wanted = Longitude();
            double flat = Latitude();
            double[] latitudes = { flat, flat + 8.0, flat - 8.0, flat + 16.0, flat - 16.0 };
            for (double away = 0.0; away <= 60.0; away += 2.0)
                for (int side = 0; side < (away == 0.0 ? 1 : 2); side++)
                    foreach (double lat in latitudes)
                    {
                        double lon = wanted + (side == 0 ? away : -away);
                        double here;
                        if (!Level(body, lat, lon, out here)) continue;
                        game.flightState.protoVessels.RemoveAll(v => v.vesselName == VesselName);
                        ConfigNode part = ProtoVessel.CreatePartNode("mk1pod.v2", ShipConstruction.GetUniqueFlightID(game.flightState));
                        var orbit = new Orbit(0.0, 0.0, body.Radius + 100000.0, 0.0, 0.0, 0.0, Planetarium.GetUniversalTime(), body);
                        ConfigNode node = ProtoVessel.CreateVesselNode(VesselName, VesselType.Ship, orbit, 0, new[] { part });
                        node.SetValue("sit", Vessel.Situations.LANDED.ToString());
                        node.SetValue("landed", "True");
                        node.SetValue("splashed", "False");
                        node.SetValue("skipGroundPositioning", "False");
                        node.SetValue("lat", lat.ToString(CultureInfo.InvariantCulture));
                        node.SetValue("lon", lon.ToString(CultureInfo.InvariantCulture));
                        node.SetValue("alt", (here + 1.0).ToString(CultureInfo.InvariantCulture));
                        Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)body.GetRelSurfaceNVector(lat, lon));
                        node.SetValue("rot", KSPUtil.WriteQuaternion(upright));
                        game.flightState.protoVessels.Add(new ProtoVessel(node, game));
                        Debug.Log(Log.Tag + " Probe: land: a capsule on " + name + " at latitude " + lat.ToString(CultureInfo.InvariantCulture)
                                  + ", longitude " + lon.ToString(CultureInfo.InvariantCulture) + ", the ground " + here.ToString("0", CultureInfo.InvariantCulture)
                                  + " m above the datum.");
                        return game.flightState.protoVessels.Count - 1;
                    }
            Debug.LogError(Log.Tag + " Probe: land: no level ground found on " + name + " near longitude " + wanted + ".");
            return -1;
        }

        // Whether the ground there is dry and level over 5 x 5 samples 20 m apart; its height above the datum.
        private static bool Level(CelestialBody body, double lat, double lon, out double height)
        {
            Func<double, double, double> ground = (la, lo) => body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(la, lo)) - body.Radius;
            height = ground(lat, lon);
            if (body.ocean && height < 5.0) return false;
            double step = 20.0 / body.Radius * 180.0 / Math.PI;
            double cos = Math.Max(0.1, Math.Cos(lat * Math.PI / 180.0));
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                    if (Math.Abs(ground(lat + i * step, lon + j * step / cos) - height) > 8.0) return false;
            return true;
        }

        // In the flight: the sun's height above the pod's horizon and the longitude that would put it at 30 degrees in the
        // morning, into the log. True where the probe is to quit because the sun is too low or too high (Adjust).
        internal static bool Report()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (BodyName == null || vessel == null) return false;
            Vector3d toSun = (FlightGlobals.Bodies[0].position - vessel.GetWorldPos3D()).normalized;
            double height = Math.Asin(Vector3d.Dot(toSun, vessel.upAxis)) * 180.0 / Math.PI;
            double east = Vector3d.Dot(toSun, vessel.east);
            // On the equator: how far east of the pod the sun's longitude lies.
            double offset = (east >= 0.0 ? 1.0 : -1.0) * (90.0 - height);
            double next = vessel.longitude + offset - (90.0 - WantedSun);
            next = ((next + 540.0) % 360.0) - 180.0;
            Debug.Log(Log.Tag + " Probe: land: " + vessel.mainBody.bodyName + " at latitude " + vessel.latitude.ToString("0.00", CultureInfo.InvariantCulture)
                      + ", longitude " + vessel.longitude.ToString("0.00", CultureInfo.InvariantCulture) + ": the sun stands "
                      + height.ToString("0.0", CultureInfo.InvariantCulture) + " degrees high; longitude for 30 degrees: "
                      + next.ToString("0.0", CultureInfo.InvariantCulture) + ".");
            return CommandLine.Has("-redefinitionProbeLandAdjust") && (height < LowestSun || height > HighestSun);
        }
    }
}
