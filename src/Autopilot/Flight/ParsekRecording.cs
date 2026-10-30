using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Autopilot.Flight
{
    // A recording of the route made ahead of the flight, committed to Parsek behind the parsek-recording assumption. Parsek plays a committed
    // recording as a ghost and puts the vessel back where the recording ends, also when the player is not in the flight: this is how a route goes
    // on when the player leaves the vessel. The recording is built the way Parsek's own in-game test builds one (RuntimeTests,
    // TryBuildSyntheticKeepVesselTree, Parsek 0.10.7, source read) and committed with RecordingStore.CommitTree.
    internal static class ParsekRecording
    {
        // The format and schema generation of a recording that the code was written for (Parsek's RecordingSchema).
        private const int FormatVersion = 1;
        private const int SchemaGeneration = 4;

        // Commits a recording of the plan: the orbits it flies from now to its end, and the delta-v of its burns. Null where it was committed; else
        // the reason it was not.
        internal static string CommitPlan(Vessel vessel, RoutePlan plan)
        {
            if (plan == null || plan.Legs.Count == 0) return plan != null && plan.Ended != null ? plan.Ended : "The plan has no orbit to record.";
            return Commit(vessel, plan.Legs, plan.DeltaV);
        }

        // Commits a recording of the legs for the vessel. The vessel ends on the last leg's orbit, with the fuel the burns of deltaV take from it.
        // Null where it was committed; else the reason it was not.
        internal static string Commit(Vessel vessel, IList<PlannedLeg> legs, double deltaV)
        {
            if (!ForeignAssumptions.ParsekRecording.Holds) return "Parsek is not there, or is a build the recording was not checked against.";
            if (vessel == null || legs == null || legs.Count == 0) return "There is no route to record.";
            try
            {
                // The format Parsek reads a recording in: a recording of another version is refused by Parsek when it is loaded again.
                Type schema = TypeLookup.Find("Parsek.RecordingSchema");
                object format = schema.GetField("CurrentRecordingFormatVersion", TypeLookup.Any).GetRawConstantValue();
                object generation = schema.GetField("CurrentRecordingSchemaGeneration", TypeLookup.Any).GetRawConstantValue();
                if (!Equals(format, FormatVersion) || !Equals(generation, SchemaGeneration))
                    return "Parsek reads recordings in format " + format + ", generation " + generation + "; they are made for " + FormatVersion + ", " + SchemaGeneration + ".";
                Type recordingType = TypeLookup.Find("Parsek.Recording");
                Type treeType = TypeLookup.Find("Parsek.RecordingTree");
                Type pointType = TypeLookup.Find("Parsek.TrajectoryPoint");
                Type segmentType = TypeLookup.Find("Parsek.OrbitSegment");
                Type terminalType = TypeLookup.Find("Parsek.TerminalState");
                Type store = TypeLookup.Find("Parsek.RecordingStore");
                Type spawner = TypeLookup.Find("Parsek.VesselSpawner");
                MethodInfo backup = spawner.GetMethod("TryBackupSnapshot", TypeLookup.Any, null, new[] { typeof(Vessel) }, null);
                MethodInfo commit = store.GetMethod("CommitTree", TypeLookup.Any, null, new[] { treeType }, null);
                ConfigNode source = (ConfigNode)backup.Invoke(null, new object[] { vessel });
                if (source == null) return "Parsek could not take the vessel's snapshot.";
                ConfigNode snapshot = source.CreateCopy();
                ConfigNode ghost = source.CreateCopy();
                Fuel.Burn(vessel, snapshot, deltaV);
                Fuel.Burn(vessel, ghost, deltaV);

                PlannedLeg last = legs[legs.Count - 1];
                string id = "redefinition-" + DateTime.UtcNow.Ticks;
                object recording = Activator.CreateInstance(recordingType);
                Set(recording, "RecordingId", id);
                Set(recording, "TreeId", id);
                Set(recording, "VesselName", vessel.vesselName);
                Set(recording, "VesselPersistentId", vessel.persistentId);
                Set(recording, "TerminalStateValue", Enum.Parse(terminalType, "Orbiting"));
                Set(recording, "TerminalOrbitInclination", last.Orbit.inclination);
                Set(recording, "TerminalOrbitEccentricity", last.Orbit.eccentricity);
                Set(recording, "TerminalOrbitSemiMajorAxis", last.Orbit.semiMajorAxis);
                Set(recording, "TerminalOrbitLAN", last.Orbit.LAN);
                Set(recording, "TerminalOrbitArgumentOfPeriapsis", last.Orbit.argumentOfPeriapsis);
                Set(recording, "TerminalOrbitMeanAnomalyAtEpoch", last.Orbit.meanAnomalyAtEpoch);
                Set(recording, "TerminalOrbitEpoch", last.Orbit.epoch);
                Set(recording, "TerminalOrbitBody", last.Orbit.referenceBody.name);
                Set(recording, "VesselSnapshot", snapshot);
                Set(recording, "GhostVisualSnapshot", ghost);

                IList segments = ListOf(recording, "OrbitSegments");
                foreach (PlannedLeg leg in legs) segments.Add(Segment(segmentType, leg));
                // One point at the end: Parsek carries the terminal orbit forward to the UT it is played at from it.
                ListOf(recording, "Points").Add(Point(pointType, last));

                object tree = Activator.CreateInstance(treeType);
                Set(tree, "Id", id);
                Set(tree, "TreeName", vessel.vesselName + " route");
                Set(tree, "RootRecordingId", id);
                Set(tree, "ActiveRecordingId", id);
                ((IDictionary)Get(tree, "Recordings"))[id] = recording;
                commit.Invoke(null, new[] { tree });
                Debug.Log(Log.Tag + " Parsek has a recording of the route: " + legs.Count + " leg(s) from UT " + legs[0].StartUT.ToString("0") + " to "
                          + last.EndUT.ToString("0") + ".");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The recording for Parsek failed: " + e);
                return "The recording for Parsek failed: " + e.Message;
            }
        }

        private static object Segment(Type type, PlannedLeg leg)
        {
            object segment = Activator.CreateInstance(type);
            Set(segment, "startUT", leg.StartUT);
            Set(segment, "endUT", leg.EndUT);
            Set(segment, "inclination", leg.Orbit.inclination);
            Set(segment, "eccentricity", leg.Orbit.eccentricity);
            Set(segment, "semiMajorAxis", leg.Orbit.semiMajorAxis);
            Set(segment, "longitudeOfAscendingNode", leg.Orbit.LAN);
            Set(segment, "argumentOfPeriapsis", leg.Orbit.argumentOfPeriapsis);
            Set(segment, "meanAnomalyAtEpoch", leg.Orbit.meanAnomalyAtEpoch);
            Set(segment, "epoch", leg.Orbit.epoch);
            Set(segment, "bodyName", leg.Orbit.referenceBody.name);
            return segment;
        }

        // The point where the last leg ends, as latitude, longitude and altitude over the body, the body turned on from now to then.
        private static object Point(Type type, PlannedLeg last)
        {
            CelestialBody body = last.Orbit.referenceBody;
            Vector3d relative = last.Orbit.getRelativePositionAtUT(last.EndUT).xzy;
            Vector3d velocity = last.Orbit.getOrbitalVelocityAtUT(last.EndUT).xzy;
            double latitude = body.GetLatitude(body.position + relative);
            double turned = body.rotationPeriod > 0.0 ? (last.EndUT - Planetarium.GetUniversalTime()) / body.rotationPeriod * 360.0 : 0.0;
            double longitude = body.GetLongitude(body.position + relative) - turned;
            longitude = ((longitude + 540.0) % 360.0) - 180.0;
            object point = Activator.CreateInstance(type);
            Set(point, "ut", last.EndUT);
            Set(point, "latitude", latitude);
            Set(point, "longitude", longitude);
            Set(point, "altitude", relative.magnitude - body.Radius);
            Set(point, "rotation", Quaternion.identity);
            Set(point, "velocity", new Vector3((float)velocity.x, (float)velocity.y, (float)velocity.z));
            Set(point, "bodyName", body.name);
            return point;
        }

        private static IList ListOf(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, TypeLookup.Any);
            IList list = (IList)field.GetValue(owner);
            if (list == null)
            {
                list = (IList)Activator.CreateInstance(field.FieldType);
                field.SetValue(owner, list);
            }
            return list;
        }

        private static object Get(object owner, string name)
        {
            return owner.GetType().GetField(name, TypeLookup.Any).GetValue(owner);
        }

        private static void Set(object owner, string name, object value)
        {
            owner.GetType().GetField(name, TypeLookup.Any).SetValue(owner, value);
        }

        // What the burns take from the tanks, by the rocket equation with the exhaust velocity the game's own delta-v gives the vessel.
        private static class Fuel
        {
            // The propellant the engines burn, in every tank of the snapshot, is scaled down by the share the burns use of it.
            internal static void Burn(Vessel vessel, ConfigNode snapshot, double deltaV)
            {
                if (deltaV <= 0.0 || vessel.VesselDeltaV == null) return;
                double total = vessel.VesselDeltaV.TotalDeltaVVac;
                double mass = vessel.GetTotalMass();
                HashSet<string> burned = Propellants(vessel);
                double propellant = 0.0;
                foreach (ConfigNode part in snapshot.GetNodes("PART"))
                    foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                        propellant += Mass(resource, burned);
                double dry = mass - propellant;
                if (total <= 0.0 || propellant <= 0.0 || dry <= 0.0) return;
                double exhaust = total / Math.Log(mass / dry);
                double used = Math.Min(propellant, mass * (1.0 - Math.Exp(-deltaV / exhaust)));
                double keep = 1.0 - used / propellant;
                foreach (ConfigNode part in snapshot.GetNodes("PART"))
                    foreach (ConfigNode resource in part.GetNodes("RESOURCE"))
                    {
                        string name = resource.GetValue("name");
                        double amount;
                        if (name == null || !burned.Contains(name) || !double.TryParse(resource.GetValue("amount"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out amount)) continue;
                        resource.SetValue("amount", (amount * keep).ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
                    }
            }

            private static HashSet<string> Propellants(Vessel vessel)
            {
                var names = new HashSet<string>();
                foreach (Part part in vessel.parts)
                    foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                        foreach (Propellant propellant in engine.propellants) names.Add(propellant.name);
                return names;
            }

            private static double Mass(ConfigNode resource, HashSet<string> burned)
            {
                string name = resource.GetValue("name");
                double amount;
                PartResourceDefinition definition = name != null ? PartResourceLibrary.Instance.GetDefinition(name) : null;
                if (definition == null || !burned.Contains(name) || !double.TryParse(resource.GetValue("amount"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out amount)) return 0.0;
                return amount * definition.density;
            }
        }
    }
}
