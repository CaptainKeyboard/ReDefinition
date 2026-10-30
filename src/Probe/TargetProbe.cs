using System;
using System.Globalization;
using System.IO;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeTargetCraft <craft>: before the probe's own craft is launched, that craft (Ships/VAB/<craft>.craft) is launched,
    // put in a circular equatorial orbit as a target for a rendezvous, and left there. The probe then goes on with its normal launch.
    // The flag -redefinitionProbeTargetAltitude <metres> says how high (100 km without it). The flag -redefinitionProbeTargetAngle <degrees> says
    // where on the orbit, as the mean anomaly at the moment it is put there (0 without it). The flag -redefinitionProbeTargetBody <body> names
    // the body it is round, the home body without it.
    // The way: the space centre launches the target (Launch). Its first flight puts it in the orbit and starts the flight again on it.
    // That second flight saves the game and returns to the space centre (Step), where the normal launch follows.
    internal static class TargetProbe
    {
        internal const string CraftArgument = "-redefinitionProbeTargetCraft";
        internal const string AltitudeArgument = "-redefinitionProbeTargetAltitude";
        internal const string AngleArgument = "-redefinitionProbeTargetAngle";
        internal const string BodyArgument = "-redefinitionProbeTargetBody";

        // Whether the target's flights are going on. The flight probe counts them as its own while they do.
        internal static bool Active { get; private set; }
        // Whether the target was launched already, so that it is launched once.
        private static bool launched;
        // Whether the target stands in its orbit already, and the next flight only leaves.
        private static bool inOrbit;

        // The target craft's file, or null where none is asked for or found.
        private static string CraftFile()
        {
            string craft = CommandLine.After(CraftArgument);
            if (craft == null) return null;
            string path = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "Ships"), "VAB"), craft + ".craft");
            return File.Exists(path) ? path : null;
        }

        // Whether the target is yet to be launched.
        internal static bool Wanted
        {
            get { return !launched && CraftFile() != null; }
        }

        // The target's launch from the pad, uncrewed; false where it cannot be launched.
        internal static bool Launch()
        {
            string path = CraftFile();
            ConfigNode craft = path != null ? ConfigNode.Load(path) : null;
            if (craft == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: target: no craft " + CommandLine.After(CraftArgument) + " in Ships/VAB.");
                return false;
            }
            launched = true;
            Active = true;
            Debug.Log(Log.Tag + " Probe: target: a new launch of " + Path.GetFileNameWithoutExtension(path) + ".");
            FlightDriver.StartWithNewLaunch(path, "Squad/Flags/default", "LaunchPad", VesselCrewManifest.FromConfigNode(craft));
            return true;
        }

        // Called in each flight once it stands. In the target's first flight it puts the vessel in its orbit and starts the flight again
        // on it; in the second it saves the game and returns to the space centre. True where the flight is over.
        internal static bool Step()
        {
            if (!Active) return false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
            {
                Debug.LogError(Log.Tag + " Probe: target: no vessel in the flight; quitting.");
                Application.Quit();
                return true;
            }
            if (inOrbit) return Leave();
            return ToOrbit(vessel);
        }

        private static bool ToOrbit(Vessel vessel)
        {
            string bodyName = CommandLine.After(BodyArgument);
            CelestialBody body = bodyName != null ? FlightGlobals.Bodies.Find(b => b.bodyName == bodyName) : FlightGlobals.GetHomeBody();
            if (body == null)
            {
                Debug.LogError(Log.Tag + " Probe: target: no body " + bodyName + "; quitting.");
                Application.Quit();
                return true;
            }
            double altitude, degrees;
            if (!double.TryParse(CommandLine.After(AltitudeArgument) ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out altitude))
                altitude = 100000.0;
            if (!double.TryParse(CommandLine.After(AngleArgument) ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out degrees))
                degrees = 0.0;
            Game game = HighLogic.CurrentGame.Updated();
            double ut = Planetarium.GetUniversalTime();
            var orbit = new Orbit(0.0, 0.0, body.Radius + altitude, 0.0, 0.0, degrees * Math.PI / 180.0, ut, body);
            var node = new ConfigNode("VESSEL");
            vessel.BackupVessel().Save(node);
            node.SetValue("name", CommandLine.After(CraftArgument) + " Target");
            node.SetValue("sit", Vessel.Situations.ORBITING.ToString());
            node.SetValue("landed", "False");
            node.SetValue("splashed", "False");
            node.SetValue("skipGroundPositioning", "True");
            node.SetValue("landedAt", "");
            node.SetValue("displaylandedAt", "");
            node.RemoveNode("ORBIT");
            var orbitNode = new ConfigNode("ORBIT");
            new OrbitSnapshot(orbit).Save(orbitNode);
            node.AddNode(orbitNode);
            game.flightState.protoVessels.RemoveAll(v => v.vesselID == vessel.id);
            game.flightState.protoVessels.Add(new ProtoVessel(node, game));
            inOrbit = true;
            Debug.Log(Log.Tag + " Probe: target: " + vessel.vesselName + " with " + vessel.parts.Count + " parts put in an orbit round " + body.bodyName
                      + " at " + altitude.ToString("0", CultureInfo.InvariantCulture) + " m, " + degrees.ToString("0.#", CultureInfo.InvariantCulture)
                      + " degrees from the periapsis, the flight started again.");
            FlightDriver.StartAndFocusVessel(game, game.flightState.protoVessels.Count - 1);
            return true;
        }

        private static bool Leave()
        {
            Active = false;
            Game game = HighLogic.CurrentGame.Updated();
            HighLogic.CurrentGame = game;
            GamePersistence.SaveGame(game, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            if (StagingProbe.CraftPath() == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: target: in orbit, and no craft to launch after it (" + StagingProbe.LaunchArgument + "); quitting.");
                Application.Quit();
                return true;
            }
            // The space centre launches the probe's own craft next, as it would have at the start.
            StagingProbe.Pending = true;
            Debug.Log(Log.Tag + " Probe: target: saved, to the space centre for the launch.");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            return true;
        }
    }
}
