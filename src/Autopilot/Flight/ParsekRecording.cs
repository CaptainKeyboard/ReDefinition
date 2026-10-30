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
        // What the ids of the recordings made here begin with.
        private const string IdPrefix = "redefinition-";

        // The format and schema generation of a recording that the code was written for (Parsek's RecordingSchema).
        private const int FormatVersion = 1;
        private const int SchemaGeneration = 4;

        // Commits a recording of the plan: the orbits it flies from now to its end, and the delta-v of its burns. With `takeVesselOut` the vessel
        // is then taken out of the world as Parsek takes out a vessel its recording stands for: the recording shows it as a ghost, and puts it back
        // on the orbit it ends on. Null where it was committed; else the reason it was not.
        internal static string CommitPlan(Vessel vessel, RoutePlan plan, bool takeVesselOut = false)
        {
            if (plan == null || plan.Legs.Count == 0) return plan != null && plan.Ended != null ? plan.Ended : "The plan has no orbit to record.";
            if (plan.Failed) return "The plan does not reach a state to record: " + plan.Ended;
            // The vessel is put on the last orbit: it must be a closed one over the ground, not the climb of an ascent or the approach to a pass.
            Orbit end = plan.Legs[plan.Legs.Count - 1].Orbit;
            if (end.eccentricity >= 1.0 || end.PeR <= end.referenceBody.Radius) return "The plan ends " + plan.Ended + " It is not on an orbit to put the vessel on.";
            return Commit(vessel, plan.Legs, plan.DeltaV, takeVesselOut);
        }

        // Commits a recording of the legs for the vessel. The vessel ends on the last leg's orbit, with the fuel the burns of deltaV take from it.
        // Null where it was committed; else the reason it was not.
        internal static string Commit(Vessel vessel, IList<PlannedLeg> legs, double deltaV, bool takeVesselOut = false)
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
                MethodInfo applied = store.GetMethod("MarkTreeAsApplied", TypeLookup.Any, null, new[] { treeType }, null);
                Type ledger = TypeLookup.Find("Parsek.LedgerOrchestrator");
                MethodInfo notify = ledger != null ? ledger.GetMethod("NotifyLedgerTreeCommitted", TypeLookup.Any, null, new[] { treeType }, null) : null;
                if (applied == null || notify == null) return "Parsek cannot file the recording in its ledger in this build.";
                // Whether the vessel can be taken out is known before anything is committed.
                MethodInfo remove = null;
                ConstructorInfo target = null;
                if (takeVesselOut)
                {
                    Type removal = TypeLookup.Find("Parsek.ClaimedVesselRemoval");
                    Type liveTarget = TypeLookup.Find("Parsek.LiveClaimedVesselRemovalTarget");
                    Type contract = TypeLookup.Find("Parsek.IClaimedVesselRemovalTarget");
                    remove = removal != null && contract != null ? removal.GetMethod("Remove", TypeLookup.Any, null, new[] { contract, typeof(string) }, null) : null;
                    target = liveTarget != null ? liveTarget.GetConstructor(TypeLookup.Any, null, new[] { typeof(Vessel), typeof(ProtoVessel), typeof(IList<ProtoVessel>) }, null) : null;
                    if (remove == null || target == null) return "Parsek cannot take the vessel out of the world in this build.";
                }
                ConfigNode source = (ConfigNode)backup.Invoke(null, new object[] { vessel });
                if (source == null) return "Parsek could not take the vessel's snapshot.";
                if (takeVesselOut) KeepSnapshot(vessel, source);
                ConfigNode snapshot = source.CreateCopy();
                ConfigNode ghost = source.CreateCopy();
                Fuel.Burn(vessel, snapshot, deltaV);
                Fuel.Burn(vessel, ghost, deltaV);

                PlannedLeg last = legs[legs.Count - 1];
                string id = IdPrefix + DateTime.UtcNow.Ticks;
                object recording = Activator.CreateInstance(recordingType);
                Set(recording, "RecordingId", id);
                Set(recording, "TreeId", id);
                Set(recording, "VesselName", vessel.vesselName);
                // Not the vessel's own id: with it Parsek takes the recording for the flight of the vessel that is there, makes it the one it records,
                // and puts the vessel back where it was when the flight is left. Parsek's own test derives one the same way.
                uint other = vessel.persistentId ^ 0x5A5AA5A5u;
                Set(recording, "VesselPersistentId", other != 0u && other != vessel.persistentId ? other : 950001u);
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
                // One point at the end: Parsek carries the terminal orbit forward to the UT it is played at from it. The space centre plays no
                // recording that ends in an orbit; the tracking station and the flight do.
                ListOf(recording, "Points").Add(Point(pointType, last.Orbit, last.EndUT));

                object tree = Activator.CreateInstance(treeType);
                Set(tree, "Id", id);
                Set(tree, "TreeName", vessel.vesselName + " route");
                Set(tree, "RootRecordingId", id);
                Set(tree, "ActiveRecordingId", id);
                ((IDictionary)Get(tree, "Recordings"))[id] = recording;
                commit.Invoke(null, new[] { tree });
                if (!Committed(store, recordingType, id)) return "Parsek did not keep the recording.";
                KeepInScope();
                // As Parsek's own commit goes on: the recording is marked as applied, and filed in the ledger, where the crew is reserved for it.
                try
                {
                    applied.Invoke(null, new[] { tree });
                    notify.Invoke(null, new[] { tree });
                }
                catch (Exception e)
                {
                    // The recording is kept; what is left of the ledger is filed by Parsek when the game is loaded again.
                    Debug.LogWarning(Log.Tag + " The recording could not be filed in Parsek's ledger: " + e);
                }
                Debug.Log(Log.Tag + " Parsek has a recording of the route: " + legs.Count + " leg(s) from UT " + legs[0].StartUT.ToString("0") + " to "
                          + last.EndUT.ToString("0") + ".");
                if (takeVesselOut)
                {
                    object removal = target.Invoke(new object[] { vessel, vessel.protoVessel, HighLogic.CurrentGame.flightState.protoVessels });
                    remove.Invoke(null, new[] { removal, "ReDefinition route" });
                    // The game is saved with the vessel gone: a scene that is loaded from the saved game would bring the vessel back.
                    try
                    {
                        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(Log.Tag + " The game could not be saved after the vessel was taken out: " + e.Message);
                    }
                    Debug.Log(Log.Tag + " " + vessel.vesselName + " is out of the world; Parsek puts it back where the recording ends.");
                }
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The recording for Parsek failed: " + e);
                return "The recording for Parsek failed: " + e.Message;
            }
        }

        // Parsek puts a vessel back at the end of a recording only if the recording was in its replay scope. That is, it was seen at its start,
        // or within two seconds of it. A recording not seen so is taken for past, and nothing of it is played. The scope is kept in memory only.
        // So every recording made here is told to the scope from its own start, whatever the time is, when it is committed and at each scene's start.
        internal static void KeepInScope()
        {
            if (!ForeignAssumptions.ParsekRecording.Holds) return;
            try
            {
                Type store = TypeLookup.Find("Parsek.RecordingStore");
                Type recordingType = TypeLookup.Find("Parsek.Recording");
                Type tracker = TypeLookup.Find("Parsek.PlaybackScopeTracker");
                MethodInfo note = tracker.GetMethod("NotePlayhead", TypeLookup.Any, null, new[] { typeof(string), typeof(double), typeof(double) }, null);
                IEnumerable list = store.GetProperty("CommittedRecordings", TypeLookup.Any).GetValue(null, null) as IEnumerable;
                FieldInfo idField = recordingType.GetField("RecordingId", TypeLookup.Any);
                PropertyInfo startProperty = recordingType.GetProperty("StartUT", TypeLookup.Any);
                if (note == null || list == null) return;
                foreach (object item in list)
                {
                    string id = (string)idField.GetValue(item);
                    if (id == null || !id.StartsWith(IdPrefix)) continue;
                    double start = (double)startProperty.GetValue(item, null);
                    note.Invoke(null, new object[] { id, start, start });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Parsek could not be told the recordings are in scope: " + e.Message);
            }
        }

        // The UT the latest of the recordings made here ends at; zero where there is none. For the probes, which read it after the flight is left.
        internal static double LatestEnd()
        {
            double latest = 0.0;
            if (!ForeignAssumptions.ParsekRecording.Holds) return latest;
            try
            {
                Type store = TypeLookup.Find("Parsek.RecordingStore");
                Type recordingType = TypeLookup.Find("Parsek.Recording");
                IEnumerable list = store.GetProperty("CommittedRecordings", TypeLookup.Any).GetValue(null, null) as IEnumerable;
                FieldInfo idField = recordingType.GetField("RecordingId", TypeLookup.Any);
                PropertyInfo end = recordingType.GetProperty("EndUT", TypeLookup.Any);
                if (list == null) return latest;
                foreach (object item in list)
                {
                    string id = (string)idField.GetValue(item);
                    if (id != null && id.StartsWith(IdPrefix)) latest = Math.Max(latest, (double)end.GetValue(item, null));
                }
            }
            catch (Exception)
            {
                return 0.0;
            }
            return latest;
        }

        // The snapshot of the vessel in a file of its own, before it is taken out of the world: what the player gets it back from if the recording is lost.
        private static void KeepSnapshot(Vessel vessel, ConfigNode snapshot)
        {
            string name = string.Concat(vessel.vesselName.Split(System.IO.Path.GetInvalidFileNameChars()));
            string path = PluginData.Path(System.IO.Path.Combine("route-backups", name + "-" + Planetarium.GetUniversalTime().ToString("0") + ".cfg"));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            snapshot.Save(path);
            Debug.Log(Log.Tag + " The vessel's snapshot is kept in " + path + ".");
        }

        // Whether the store holds a recording of that id among those committed.
        private static bool Committed(Type store, Type recordingType, string id)
        {
            IEnumerable list = store.GetProperty("CommittedRecordings", TypeLookup.Any).GetValue(null, null) as IEnumerable;
            if (list == null) return false;
            FieldInfo field = recordingType.GetField("RecordingId", TypeLookup.Any);
            foreach (object item in list)
                if ((string)field.GetValue(item) == id) return true;
            return false;
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

        // The point on the orbit at `ut`, as latitude, longitude and altitude over the body, the body turned on from now to then.
        private static object Point(Type type, Orbit orbit, double ut)
        {
            CelestialBody body = orbit.referenceBody;
            Vector3d relative = orbit.getRelativePositionAtUT(ut).xzy;
            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut).xzy;
            double latitude = body.GetLatitude(body.position + relative);
            double turned = body.rotationPeriod > 0.0 ? (ut - Planetarium.GetUniversalTime()) / body.rotationPeriod * 360.0 : 0.0;
            double longitude = body.GetLongitude(body.position + relative) - turned;
            longitude = (((longitude + 180.0) % 360.0) + 360.0) % 360.0 - 180.0;
            object point = Activator.CreateInstance(type);
            Set(point, "ut", ut);
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
