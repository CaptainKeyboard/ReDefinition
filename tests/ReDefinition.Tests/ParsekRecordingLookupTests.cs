using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Flight;

namespace ReDefinition.Tests
{
    // Where the recording ahead of the flight looks for Parsek's snapshot and commit calls: the 1.0 places first (FlightRecorder, TreeCommitService),
    // then the 0.10 places (VesselSpawner, RecordingStore). Stand-in types take the place of Parsek's.
    [TestClass]
    public class ParsekRecordingLookupTests
    {
        public class FakeTree { }

        public class NewRecorder
        {
            public static ConfigNode TryBackupSnapshot(Vessel vessel) { return null; }
        }

        public class OldSpawner
        {
            public static ConfigNode TryBackupSnapshot(Vessel vessel) { return null; }
        }

        public class NewCommitService
        {
            public static void CommitTree(FakeTree tree) { }
        }

        public class OldStore
        {
            public static void CommitTree(FakeTree tree) { }
        }

        public class WrongSignature
        {
            public static void CommitTree(string tree) { }
            public static ConfigNode TryBackupSnapshot(string name) { return null; }
        }

        private static Func<string, Type> Find(params KeyValuePair<string, Type>[] known)
        {
            var map = new Dictionary<string, Type>();
            foreach (KeyValuePair<string, Type> pair in known) map[pair.Key] = pair.Value;
            return name => { Type type; return map.TryGetValue(name, out type) ? type : null; };
        }

        private static KeyValuePair<string, Type> Is(string name, Type type)
        {
            return new KeyValuePair<string, Type>(name, type);
        }

        [TestMethod]
        public void Parsek10TakesTheNewPlaces()
        {
            var find = Find(Is("Parsek.FlightRecorder", typeof(NewRecorder)), Is("Parsek.TreeCommitService", typeof(NewCommitService)));
            Assert.AreEqual(typeof(NewRecorder), ParsekRecording.FindBackupSnapshot(find).DeclaringType);
            Assert.AreEqual(typeof(NewCommitService), ParsekRecording.FindCommitTree(find, typeof(FakeTree)).DeclaringType);
        }

        [TestMethod]
        public void Parsek0106TakesTheOldPlaces()
        {
            var find = Find(Is("Parsek.VesselSpawner", typeof(OldSpawner)), Is("Parsek.RecordingStore", typeof(OldStore)));
            Assert.AreEqual(typeof(OldSpawner), ParsekRecording.FindBackupSnapshot(find).DeclaringType);
            Assert.AreEqual(typeof(OldStore), ParsekRecording.FindCommitTree(find, typeof(FakeTree)).DeclaringType);
        }

        [TestMethod]
        public void TheNewPlaceWinsWhereBothHaveTheCall()
        {
            var find = Find(Is("Parsek.FlightRecorder", typeof(NewRecorder)), Is("Parsek.VesselSpawner", typeof(OldSpawner)),
                            Is("Parsek.TreeCommitService", typeof(NewCommitService)), Is("Parsek.RecordingStore", typeof(OldStore)));
            Assert.AreEqual(typeof(NewRecorder), ParsekRecording.FindBackupSnapshot(find).DeclaringType);
            Assert.AreEqual(typeof(NewCommitService), ParsekRecording.FindCommitTree(find, typeof(FakeTree)).DeclaringType);
        }

        [TestMethod]
        public void NothingIsFoundWhereNeitherPlaceHasTheCallWithThatSignature()
        {
            var find = Find(Is("Parsek.FlightRecorder", typeof(WrongSignature)), Is("Parsek.TreeCommitService", typeof(WrongSignature)));
            Assert.IsNull(ParsekRecording.FindBackupSnapshot(find));
            Assert.IsNull(ParsekRecording.FindCommitTree(find, typeof(FakeTree)));
            Assert.IsNull(ParsekRecording.FindBackupSnapshot(Find()));
            Assert.IsNull(ParsekRecording.FindCommitTree(Find(), typeof(FakeTree)));
            Assert.IsNull(ParsekRecording.FindCommitTree(Find(Is("Parsek.RecordingStore", typeof(OldStore))), null));
        }
    }
}
