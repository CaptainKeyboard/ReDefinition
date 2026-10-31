using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // Parts let go of a vessel that is not loaded: a spent stage, or a satellite that is let go at a stop. The vessel's saved node is split
    // (VesselSplit); the vessel keeps its identity with the parts that stay, and each group of parts that goes is a vessel of its own on the same orbit.
    internal static class VesselDetach
    {
        // How far behind the vessel on its orbit a vessel that was let go is put, in seconds: far enough that the two are not loaded together.
        private const double Behind = 10.0;

        // The parts with these persistent ids let go; the vessels made of them are named `name` and of the `type`. True where something went.
        internal static bool Apply(Vessel vessel, ICollection<uint> parts, string name, VesselType type)
        {
            Game game = HighLogic.CurrentGame;
            ProtoVessel proto = vessel.protoVessel;
            if (game == null || proto == null || parts.Count == 0) return false;
            var node = new ConfigNode("VESSEL");
            proto.Save(node);
            List<ConfigNode> gone;
            ConfigNode kept = VesselSplit.Split(node, parts, out gone);
            if (kept == null || gone.Count == 0) return false;
            // The crew of the parts that leave is no longer the carrier's; the vessels made from the node take them aboard.
            foreach (ProtoPartSnapshot snapshot in proto.protoPartSnapshots)
                if (parts.Contains(snapshot.persistentId))
                    foreach (ProtoCrewMember crew in new List<ProtoCrewMember>(snapshot.protoModuleCrew)) proto.RemoveCrew(crew);
            // The parts' ids are free again, so that the vessels made from the node take them as they are.
            foreach (ProtoPartSnapshot snapshot in proto.protoPartSnapshots) FlightGlobals.PersistentUnloadedPartIds.Remove(snapshot.persistentId);
            // The vessel made from the staying parts only lends its snapshots: it takes a vessel id of its own, which is then taken out of the register.
            kept.SetValue("persistentId", "0");
            var carrier = new ProtoVessel(kept, game);
            FlightGlobals.PersistentVesselIds.Remove(carrier.persistentId);
            proto.protoPartSnapshots = carrier.protoPartSnapshots;
            proto.rootIndex = carrier.rootIndex;
            foreach (ProtoPartSnapshot snapshot in proto.protoPartSnapshots) snapshot.pVesselRef = proto;
            proto.RebuildCrewCounts();
            Orbit orbit = vessel.orbitDriver.orbit;
            var behind = new Orbit(orbit.inclination, orbit.eccentricity, orbit.semiMajorAxis, orbit.LAN, orbit.argumentOfPeriapsis,
                                   orbit.meanAnomalyAtEpoch - orbit.meanMotion * Behind, orbit.epoch, orbit.referenceBody);
            foreach (ConfigNode made in gone)
            {
                made.SetValue("pid", Guid.NewGuid().ToString("N"));
                made.SetValue("persistentId", "0");
                made.SetValue("name", name);
                made.SetValue("type", type.ToString());
                made.SetValue("lastUT", Planetarium.GetUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                // What belongs to the carrier alone: its route, its flight plan, its target.
                made.RemoveNodes("VESSELMODULES");
                made.AddNode("VESSELMODULES");
                made.RemoveNodes("FLIGHTPLAN");
                made.AddNode("FLIGHTPLAN");
                made.RemoveNodes("TARGET");
                made.RemoveNodes("ORBIT");
                var orbitNode = new ConfigNode("ORBIT");
                new OrbitSnapshot(behind).Save(orbitNode);
                made.AddNode(orbitNode);
                var created = new ProtoVessel(made, game);
                game.flightState.protoVessels.Add(created);
                created.Load(game.flightState);
                GameEvents.onNewVesselCreated.Fire(created.vesselRef);
            }
            return true;
        }

        // The persistent ids of the parts a section of the vessel is let go with, where the vessel is loaded and its parts can be told apart: the side of the
        // separating part that the flight is not made on. The side MechJeb can fly stays; where both or none can, the heavier one. A decoupler leaves with
        // the parts below it when its explosive node is the one towards its parent, and stays with the parent side when it is not; a docking port leaves with them.
        internal static List<uint> SectionAway(Vessel vessel, Part separator, ICollection<uint> except)
        {
            var below = new HashSet<Part>();
            Collect(separator, below);
            below.Remove(separator);
            bool withBelow = true;
            ModuleDecouplerBase decoupler = separator.FindModuleImplementing<ModuleDecouplerBase>();
            if (decoupler != null)
            {
                AttachNode explosive = separator.FindAttachNode(decoupler.explosiveNodeID);
                withBelow = explosive != null && explosive.attachedPart == separator.parent;
            }
            var childSide = new HashSet<Part>(below);
            if (withBelow) childSide.Add(separator);
            var parentSide = new HashSet<Part>();
            foreach (Part part in vessel.parts)
                if (!childSide.Contains(part) && !except.Contains(part.persistentId)) parentSide.Add(part);
            childSide.RemoveWhere(part => except.Contains(part.persistentId));
            bool child = Flies(childSide), parent = Flies(parentSide);
            bool childCarries = child != parent ? child : Mass(childSide) > Mass(parentSide);
            var away = new List<uint>();
            foreach (Part part in childCarries ? parentSide : childSide) away.Add(part.persistentId);
            return away;
        }

        private static void Collect(Part part, HashSet<Part> into)
        {
            into.Add(part);
            foreach (Part child in part.children) Collect(child, into);
        }

        private static bool Flies(IEnumerable<Part> side)
        {
            foreach (Part part in side)
                if (part.Modules.Contains("MechJebCore")) return true;
            return false;
        }

        private static double Mass(IEnumerable<Part> side)
        {
            double mass = 0.0;
            foreach (Part part in side) mass += part.mass + part.GetResourceMass();
            return mass;
        }
    }
}
