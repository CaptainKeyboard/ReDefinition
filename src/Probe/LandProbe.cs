using System;
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
            double[] latitudes = { 0.0, 8.0, -8.0, 16.0, -16.0 };
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
