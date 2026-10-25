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
        // The situations FlightProfiles.Classify tells apart, each with its built-in profile.
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
        // Its name on the tab; its situation; the readouts' keys, in the order shown.
        internal string Name;
        internal PanelSituation Situation;
        internal readonly List<string> Keys = new List<string>();

        // One of the six ReDefinition ships, one per situation: never deleted, so
        // that the panel always has a profile to switch to.
        internal bool Builtin
        {
            get { return Situation != PanelSituation.Manual; }
        }
    }

    // The flight panel's profiles as the player keeps them, in
    // PluginData/flightpanel.cfg, for every save; the defaults where there is no
    // file. A key the catalogue no longer has is dropped as it loads, and a
    // situation's profile missing from the file comes back as shipped.
    internal static class FlightProfiles
    {
        // The file's node that holds the profiles.
        private const string RootName = "FLIGHT_PANEL";

        // The six built-in profiles, as shipped.
        internal static List<FlightProfile> Defaults()
        {
            return new List<FlightProfile>
            {
                // What players watch in each situation (the reasons: docs/player/flight-panel.md).
                Make("Ascent", PanelSituation.Ascent, "apoapsis", "periapsis", "twr", "stageDeltaV", "dynamicPressure"),
                Make("Orbit", PanelSituation.Orbit, "apoapsis", "periapsis", "period", "inclination", "eccentricity", "totalDeltaV"),
                Make("Rendezvous", PanelSituation.Rendezvous, "closestApproach", "approachSpeed", "relativeSpeed", "targetDistance",
                    "transferWindow", "phaseAngle", "relativeInclination", "relativeNodes"),
                Make("Landing", PanelSituation.Landing, "suicideBurn", "altitudeGround", "verticalSpeed", "horizontalSpeed",
                    "stageDeltaV", "suicideDeltaV", "impact", "slope", "biome"),
                Make("Docking", PanelSituation.Docking, "portDistance", "closingSpeed", "portAlignment", "rcsDeltaV"),
                Make("Air", PanelSituation.Air, "altitudeSea", "verticalSpeed", "glideSlope", "angleOfAttack", "mach", "dynamicPressure"),
            };
        }

        // A profile of those readouts.
        private static FlightProfile Make(string name, PanelSituation situation, params string[] keys)
        {
            FlightProfile profile = new FlightProfile { Name = name, Situation = situation };
            profile.Keys.AddRange(keys);
            return profile;
        }

        // The profiles, loaded on first use.
        private static List<FlightProfile> current;

        // The profiles as the panel shows them, loaded from the file on first use.
        internal static List<FlightProfile> Current
        {
            get
            {
                if (current == null) current = Load();
                return current;
            }
        }

        // Where the profiles are kept.
        internal static string Path
        {
            get { return PluginData.Path("flightpanel.cfg"); }
        }

        // The profiles from the file, keys the catalogue does not have dropped, and each situation's missing profile back
        // as shipped.
        private static List<FlightProfile> Load()
        {
            List<FlightProfile> profiles = new List<FlightProfile>();
            ConfigNode root = File.Exists(Path) ? ConfigNode.Load(Path) : null;
            ConfigNode node = root != null ? root.GetNode(RootName) : null;
            if (node != null)
                foreach (ConfigNode entry in node.GetNodes("PROFILE"))
                {
                    FlightProfile profile = new FlightProfile { Name = entry.GetValue("name") ?? "Profile" };
                    // One whose situation cannot be read is the player's own, shown when
                    // picked, rather than one of the situations' in place of its own.
                    PanelSituation situation;
                    profile.Situation = TryParse(entry.GetValue("situation"), out situation) ? situation : PanelSituation.Manual;
                    foreach (string key in (entry.GetValue("readouts") ?? "").Split(','))
                    {
                        string trimmed = key.Trim();
                        if (ReadoutCatalog.Find(trimmed) != null) profile.Keys.Add(trimmed);
                    }
                    profiles.Add(profile);
                }
            // Each situation keeps its profile: one missing from the file comes back
            // as shipped, in its place among the others.
            List<FlightProfile> defaults = Defaults();
            for (int i = 0; i < defaults.Count; i++)
                if (!profiles.Exists(profile => profile.Situation == defaults[i].Situation))
                    profiles.Insert(System.Math.Min(i, profiles.Count), defaults[i]);
            return profiles;
        }

        // Writes the profiles to the file.
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

        // The situation by its name, any case; false where the text names none.
        private static bool TryParse(string text, out PanelSituation situation)
        {
            situation = PanelSituation.Orbit;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                situation = (PanelSituation)System.Enum.Parse(typeof(PanelSituation), text, true);
                return System.Enum.IsDefined(typeof(PanelSituation), situation);
            }
            catch (System.ArgumentException)
            {
                return false;
            }
        }

        // What the situation is worked out from.
        internal struct Facts
        {
            // On the ground, landed, splashed or waiting on the launch site.
            internal bool Grounded;
            // In orbit or escaping (Vessel.Situations ORBITING, ESCAPING).
            internal bool Orbital;
            // In the air (Vessel.Situations FLYING).
            internal bool Flying;
            // Wings enough to fly on (Wings); a target set.
            internal bool Winged;
            internal bool Target;
            // How far the target's docking port is, in metres; NaN without one.
            internal double Port;
            // The vertical speed in m/s, up positive; the height over the ground in metres.
            internal double VerticalSpeed;
            internal double OverGround;
        }

        // Where a situation starts, and the wider bounds it keeps to once it is
        // on, so that a value about a bound does not switch back and forth.
        internal const double DockingEnter = 2000.0, DockingLeave = 2500.0;
        internal const double LandingEnterHeight = 20000.0, LandingLeaveHeight = 25000.0;
        internal const double LandingEnterSpeed = -5.0, LandingLeaveSpeed = 2.0;

        // The situation the vessel is in, given the one before (null at first):
        // a docking port as the target within 2 km (kept to 2.5 km), docking; on
        // the ground, ascent; on wings in the air, flying; coming down faster than
        // 5 m/s below 20 km over the ground without wings, landing, kept while it
        // sinks, hovers or climbs slower than 2 m/s below 25 km; in orbit, or
        // coming from there (a descent from orbit, above the landing's height),
        // rendezvous with a target and orbit without; else ascent, the climb and
        // the coast to a periapsis still under the ground included, past the
        // apoapsis too.
        internal static PanelSituation Classify(Facts facts, PanelSituation? before)
        {
            double dockingReach = before == PanelSituation.Docking ? DockingLeave : DockingEnter;
            if (!double.IsNaN(facts.Port) && facts.Port < dockingReach) return PanelSituation.Docking;
            if (facts.Grounded) return PanelSituation.Ascent;
            if (facts.Flying && facts.Winged) return PanelSituation.Air;
            bool landing = before == PanelSituation.Landing
                ? facts.VerticalSpeed < LandingLeaveSpeed && facts.OverGround < LandingLeaveHeight
                : facts.VerticalSpeed < LandingEnterSpeed && facts.OverGround < LandingEnterHeight;
            if (landing && !facts.Winged) return PanelSituation.Landing;
            bool fromSpace = before == PanelSituation.Orbit || before == PanelSituation.Rendezvous || before == PanelSituation.Docking;
            if (!facts.Orbital && !fromSpace) return PanelSituation.Ascent;
            return facts.Target ? PanelSituation.Rendezvous : PanelSituation.Orbit;
        }

        // The facts of the vessel now.
        internal static Facts Read(Vessel vessel)
        {
            ModuleDockingNode port = vessel.targetObject as ModuleDockingNode;
            return new Facts
            {
                Grounded = vessel.LandedOrSplashed || vessel.situation == Vessel.Situations.PRELAUNCH,
                Orbital = vessel.situation == Vessel.Situations.ORBITING || vessel.situation == Vessel.Situations.ESCAPING,
                Flying = vessel.situation == Vessel.Situations.FLYING,
                Winged = Winged(vessel),
                Target = vessel.targetObject != null,
                Port = port != null && port.transform != null ? (port.transform.position - vessel.CoM).magnitude : double.NaN,
                VerticalSpeed = vessel.verticalSpeed,
                OverGround = vessel.radarAltitude,
            };
        }

        // The situation once it has held for a while: a new one takes over after
        // two seconds, so that a moment about a bound switches nothing. One for
        // the panel and the round gauge alike, followed for the active vessel,
        // afresh for another.
        internal const float Hold = 2f;
        private static readonly HeldVessel heldFor = new HeldVessel();
        private static PanelSituation? last;
        private static PanelSituation held;
        private static float lastSince;
        private static int readFrame = -1;

        // The vessel's situation once held for Hold seconds; read once per frame.
        internal static PanelSituation Held(Vessel vessel)
        {
            if (vessel == null) return PanelSituation.Orbit;
            if (!heldFor.Is(vessel))
            {
                heldFor.Set(vessel);
                last = null;
                readFrame = -1;
            }
            if (readFrame == Time.frameCount) return held;
            readFrame = Time.frameCount;
            PanelSituation now = Classify(Read(vessel), last);
            if (last == null)
            {
                held = now;
                lastSince = Time.unscaledTime;
            }
            else if (now != last.Value) lastSince = Time.unscaledTime;
            else if (now != held && Time.unscaledTime - lastSince >= Hold) held = now;
            last = now;
            return held;
        }

        // The vessel Winged last looked at, when, and what it found.
        private static readonly HeldVessel wingedFor = new HeldVessel();
        private static float wingedAt = -10f;
        private static bool winged;

        // Looked at once a second: the search goes through every part's modules.
        private static bool Winged(Vessel vessel)
        {
            if (wingedFor.Is(vessel) && Time.unscaledTime - wingedAt < 1f) return winged;
            wingedFor.Set(vessel);
            wingedAt = Time.unscaledTime;
            winged = Wings(vessel);
            return winged;
        }

        // Wings enough to fly on: the lift of its lifting surfaces that are no
        // control surfaces, pods or heat shields (ModuleLiftingSurface.
        // deflectionLiftCoeff: a basic fin 0.2, a wing panel 1 and more), at least 2
        // and at least 0.1 per tonne, so that a rocket's fins and a Mk2 fuselage do
        // not make it a plane.
        private static bool Wings(Vessel vessel)
        {
            double lift = 0.0;
            foreach (Part part in vessel.parts)
            {
                if (part.HasModuleImplementing<ModuleControlSurface>() || part.HasModuleImplementing<ModuleCommand>()
                    || part.HasModuleImplementing<ModuleAblator>())
                    continue;
                foreach (ModuleLiftingSurface surface in part.FindModulesImplementing<ModuleLiftingSurface>())
                    if (surface != null) lift += surface.deflectionLiftCoeff;
            }
            return lift >= 2.0 && vessel.totalMass > 0.0 && lift / vessel.totalMass >= 0.1;
        }
    }
}
