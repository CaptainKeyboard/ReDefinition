using System.Collections.Generic;
using System.IO;
using ReDefinition.Core;
using ReDefinition.Flight.Readouts;
using UnityEngine;

namespace ReDefinition.Flight.Panel
{
    // The situations the flight panel has a profile for, and switches to by itself.
    internal enum PanelSituation
    {
        Ascent,
        Orbit,
        Rendezvous,
        Landing,
        Docking,
        Air,
        // A profile of the player's own, shown only when picked.
        Manual,
    }

    // A set of readouts the flight panel shows together (ReadoutCatalog), under a
    // tab of its own; for one situation, which the panel switches to by itself.
    internal sealed class FlightProfile
    {
        internal string Name;
        internal PanelSituation Situation;
        internal readonly List<string> Keys = new List<string>();

        internal FlightProfile Copy()
        {
            FlightProfile copy = new FlightProfile { Name = Name, Situation = Situation };
            copy.Keys.AddRange(Keys);
            return copy;
        }
    }

    // The flight panel's profiles as the player keeps them, in
    // PluginData/flightpanel.cfg, for every save; the defaults where there is no
    // file. A key the catalogue no longer has is dropped as it loads.
    internal static class FlightProfiles
    {
        private const string RootName = "FLIGHT_PANEL";

        internal static List<FlightProfile> Defaults()
        {
            return new List<FlightProfile>
            {
                Make("Ascent", PanelSituation.Ascent, "apoapsis", "periapsis", "twr", "stageDeltaV", "dynamicPressure", "mach"),
                Make("Orbit", PanelSituation.Orbit, "apoapsis", "periapsis", "period", "inclination", "eccentricity", "orbitalSpeed"),
                Make("Rendezvous", PanelSituation.Rendezvous, "closestApproach", "transferWindow", "targetDistance", "relativeSpeed",
                    "phaseAngle", "relativeInclination", "relativeNodes"),
                Make("Landing", PanelSituation.Landing, "suicideBurn", "altitudeGround", "verticalSpeed", "horizontalSpeed",
                    "impact", "suicideDeltaV", "slope", "biome"),
                Make("Docking", PanelSituation.Docking, "portDistance", "closingSpeed", "portAlignment", "relativeSpeed"),
                Make("Air", PanelSituation.Air, "altitudeSea", "verticalSpeed", "mach", "dynamicPressure", "angleOfAttack", "sideslip"),
            };
        }

        private static FlightProfile Make(string name, PanelSituation situation, params string[] keys)
        {
            FlightProfile profile = new FlightProfile { Name = name, Situation = situation };
            profile.Keys.AddRange(keys);
            return profile;
        }

        private static List<FlightProfile> current;

        internal static List<FlightProfile> Current
        {
            get
            {
                if (current == null) current = Load();
                return current;
            }
        }

        internal static string Path
        {
            get { return PluginData.Path("flightpanel.cfg"); }
        }

        private static List<FlightProfile> Load()
        {
            List<FlightProfile> profiles = new List<FlightProfile>();
            ConfigNode root = File.Exists(Path) ? ConfigNode.Load(Path) : null;
            ConfigNode node = root != null ? root.GetNode(RootName) : null;
            if (node != null)
                foreach (ConfigNode entry in node.GetNodes("PROFILE"))
                {
                    FlightProfile profile = new FlightProfile { Name = entry.GetValue("name") ?? "Profile" };
                    PanelSituation situation;
                    profile.Situation = TryParse(entry.GetValue("situation"), out situation) ? situation : PanelSituation.Orbit;
                    foreach (string key in (entry.GetValue("readouts") ?? "").Split(','))
                    {
                        string trimmed = key.Trim();
                        if (ReadoutCatalog.Find(trimmed) != null) profile.Keys.Add(trimmed);
                    }
                    profiles.Add(profile);
                }
            if (profiles.Count == 0) profiles = Defaults();
            return profiles;
        }

        internal static void Save()
        {
            ConfigNode node = new ConfigNode(RootName);
            foreach (FlightProfile profile in Current)
            {
                ConfigNode entry = node.AddNode("PROFILE");
                entry.AddValue("name", profile.Name);
                entry.AddValue("situation", profile.Situation.ToString());
                entry.AddValue("readouts", string.Join(", ", profile.Keys.ToArray()));
            }
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            ConfigNode root = new ConfigNode();
            root.AddNode(node);
            root.Save(Path);
            Debug.Log(Log.Tag + " Flight panel profiles saved: " + Current.Count + ".");
        }

        private static bool TryParse(string text, out PanelSituation situation)
        {
            situation = PanelSituation.Orbit;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                situation = (PanelSituation)System.Enum.Parse(typeof(PanelSituation), text, true);
                return true;
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }

        // The situation the vessel is in, for the panel to switch to: a docking port
        // as the target within 2 km, docking; another target, rendezvous; coming
        // down near a body (below 20 km over the ground, or a periapsis under it),
        // landing; on wings in the air, flying; on the ground or climbing, ascent;
        // else orbit.
        internal static PanelSituation Situation(Vessel vessel)
        {
            if (vessel == null) return PanelSituation.Orbit;
            ModuleDockingNode port = vessel.targetObject as ModuleDockingNode;
            if (port != null && port.transform != null && (port.transform.position - vessel.CoM).magnitude < 2000f)
                return PanelSituation.Docking;
            bool grounded = vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH;
            if (vessel.targetObject != null && !grounded) return PanelSituation.Rendezvous;
            bool low = vessel.radarAltitude < 20000.0 || (vessel.orbit != null && vessel.orbit.PeA < 0.0);
            bool aloft = vessel.situation == Vessel.Situations.FLYING || vessel.situation == Vessel.Situations.SUB_ORBITAL;
            if (aloft && vessel.verticalSpeed < -1.0 && low && !Winged(vessel)) return PanelSituation.Landing;
            if (vessel.situation == Vessel.Situations.FLYING && Winged(vessel)) return PanelSituation.Air;
            if (grounded || (aloft && vessel.verticalSpeed >= -1.0)) return PanelSituation.Ascent;
            if (aloft) return PanelSituation.Landing;
            return PanelSituation.Orbit;
        }

        // Wings: two lifting surfaces or more that are no control surfaces.
        private static bool Winged(Vessel vessel)
        {
            int wings = 0;
            foreach (Part part in vessel.parts)
                if (part.HasModuleImplementing<ModuleLiftingSurface>() && !part.HasModuleImplementing<ModuleControlSurface>()
                    && !part.HasModuleImplementing<ModuleCommand>())
                    wings++;
            return wings >= 2;
        }
    }
}
