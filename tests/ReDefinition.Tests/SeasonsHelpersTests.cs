using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The small shared parts of the seasons: the numeric helpers, the angles, the retry and failure counters, the fog bookkeeping of a
    // storm, the decisions of the ground refresh and the reflection lookups.
    [TestClass]
    public class SeasonsHelpersTests
    {
        [TestMethod]
        public void ClampKeepsAValueInsideAndNotANumberPassesOrGivesTheMinimum()
        {
            Assert.AreEqual(1.0, Mathx.Clamp(-3.0, 1, 2));
            Assert.AreEqual(2.0, Mathx.Clamp(9.0, 1, 2));
            Assert.AreEqual(1.5, Mathx.Clamp(1.5, 1, 2));
            Assert.IsTrue(double.IsNaN(Mathx.Clamp(double.NaN, 1, 2)), "NaN passes through Clamp");
            Assert.AreEqual(1.0, Mathx.ClampOrMin(double.NaN, 1, 2), "NaN gives the minimum in ClampOrMin");
            Assert.AreEqual(2.0, Mathx.ClampOrMin(double.PositiveInfinity, 1, 2));
            Assert.AreEqual(3, Mathx.Clamp(1, 3, 5));
            Assert.AreEqual(5, Mathx.Clamp(9, 3, 5));
            Assert.AreEqual(0.5f, Mathx.Clamp(0.5f, 0f, 1f));
            Assert.AreEqual(0f, Mathx.Clamp01(-1f));
            Assert.AreEqual(1.0, Mathx.Clamp01(7.0));
        }

        [TestMethod]
        public void LerpSmoothStepAndFracBehave()
        {
            Assert.AreEqual(15.0, Mathx.Lerp(10.0, 20.0, 0.5), 1e-12);
            Assert.AreEqual(10f, Mathx.Lerp(10f, 20f, 0f));
            Assert.AreEqual(0.0, Mathx.SmoothStep(2, 4, 1));
            Assert.AreEqual(1.0, Mathx.SmoothStep(2, 4, 9));
            Assert.AreEqual(0.5, Mathx.SmoothStep(2, 4, 3), 1e-12);
            Assert.AreEqual(0.25f, Mathx.Frac(3.25f), 1e-6f);
            Assert.AreEqual(0.75f, Mathx.Frac(-0.25f), 1e-6f);
        }

        [TestMethod]
        public void TheAnglesConvertAndTheDirectionOfAPlaceIsTheOneThePqsUses()
        {
            Assert.AreEqual(Math.PI, Geo.ToRad(180), 1e-12);
            Assert.AreEqual(90.0, Geo.ToDeg(Math.PI / 2), 1e-12);
            double x, y, z;
            Geo.DirectionOf(Geo.ToRad(30), Geo.ToRad(40), out x, out y, out z);
            Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-12);
            Assert.AreEqual(30.0, Geo.LatitudeOf(y), 1e-9);
            Assert.AreEqual(40.0, Geo.LongitudeOf(x, z), 1e-9);
            // The equator at longitude 0 is +z, at 90 degrees +x.
            Geo.DirectionOf(0, 0, out x, out y, out z);
            Assert.AreEqual(1.0, z, 1e-12);
            Geo.DirectionOf(0, Math.PI / 2, out x, out y, out z);
            Assert.AreEqual(1.0, x, 1e-12);
            Assert.AreEqual(90.0, Geo.LatitudeOf(2.0), "a y past 1 is held at the pole");
            Assert.AreEqual(-90.0, Geo.LatitudeOf(-1.5));
            // The texel direction of the orbit mask is the same function.
            double ox, oy, oz;
            OrbitMask.Direction(0.3, 0.7, out ox, out oy, out oz);
            Geo.DirectionOf((0.7 - 0.5) * Math.PI, 2 * Math.PI * 0.3, out x, out y, out z);
            Assert.AreEqual(ox, x);
            Assert.AreEqual(oy, y);
            Assert.AreEqual(oz, z);
        }

        [TestMethod]
        public void ARetryGateTriesAtItsIntervalAndGivesUpAfterItsTries()
        {
            var gate = new RetryGate(5f, 3);
            Assert.IsTrue(gate.Due(0f));
            Assert.IsFalse(gate.Due(2f), "too soon");
            Assert.IsTrue(gate.Due(5f));
            Assert.IsFalse(gate.GaveUp);
            Assert.IsTrue(gate.Due(100f));
            Assert.AreEqual(3, gate.Tries);
            Assert.IsTrue(gate.GaveUp);
            Assert.IsFalse(gate.Due(1000f), "no more tries");
        }

        [TestMethod]
        public void AFailureStreakSwitchesOffOnlyAfterFailuresInARow()
        {
            var streak = new FailureStreak(3);
            Assert.IsFalse(streak.Failed());
            Assert.IsFalse(streak.Failed());
            streak.Succeeded();
            Assert.AreEqual(0, streak.Failures);
            Assert.IsFalse(streak.Failed());
            Assert.IsFalse(streak.Failed());
            Assert.IsFalse(streak.Disabled);
            Assert.IsTrue(streak.Failed(), "the third in a row reaches the limit");
            Assert.IsTrue(streak.Disabled);
            Assert.IsFalse(streak.Failed(), "the limit is reported once");
        }

        [TestMethod]
        public void TheFogGoesBackOnlyWhileItIsStillTheOneTheStormWrote()
        {
            var fog = new FogController();
            var storm = new Color(0.7f, 0.7f, 0.7f, 1f);
            fog.Save(false, FogMode.Linear, 0.001f, Color.gray);
            fog.Save(true, FogMode.Exponential, 0.5f, Color.red);
            Assert.IsFalse(fog.SavedFog, "the second save keeps the first");
            Assert.AreEqual(0.001f, fog.SavedDensity);
            Assert.IsFalse(fog.FoundOutsideChange(0.02f), "nothing written yet");
            fog.Wrote(0.02f, storm);
            Assert.IsFalse(fog.FoundOutsideChange(0.02f));
            Assert.IsTrue(fog.FoundOutsideChange(0.03f), "another mod changed it");
            Assert.IsFalse(fog.FoundOutsideChange(0.04f), "reported once");
            Assert.IsTrue(fog.Restore(true, FogMode.ExponentialSquared, 0.02f, storm), "still the storm's fog");
            Assert.IsFalse(fog.Saved);

            fog.Save(false, FogMode.Linear, 0.001f, Color.gray);
            fog.Wrote(0.02f, storm);
            Assert.IsFalse(fog.Restore(true, FogMode.ExponentialSquared, 0.05f, storm), "density written by someone else");
            fog.Save(false, FogMode.Linear, 0.001f, Color.gray);
            fog.Wrote(0.02f, storm);
            Assert.IsFalse(fog.Restore(true, FogMode.ExponentialSquared, 0.02f, new Color(0.1f, 0.1f, 0.1f, 1f)), "colour written by someone else");
            fog.Save(false, FogMode.Linear, 0.001f, Color.gray);
            fog.Wrote(0.02f, storm);
            Assert.IsFalse(fog.Restore(false, FogMode.ExponentialSquared, 0.02f, storm), "fog switched off by someone else");
            Assert.IsFalse(fog.Restore(true, FogMode.ExponentialSquared, 0.02f, storm), "nothing saved: nothing to put back");
        }

        [TestMethod]
        public void ALivePassStartsOnTheIntervalOrOnABigMoveAfterTheLeastGap()
        {
            Assert.IsTrue(LiveRepainter.Due(double.NaN, 0.3, 0f, 5f), "never ran");
            Assert.IsFalse(LiveRepainter.Due(0.3, 0.3, 2f, 5f));
            Assert.IsTrue(LiveRepainter.Due(0.3, 0.3, 5f, 5f), "the interval passed");
            Assert.IsFalse(LiveRepainter.Due(0.3, 0.31, 0.5f, 5f), "a big move waits the least gap");
            Assert.IsTrue(LiveRepainter.Due(0.3, 0.31, 1f, 5f));
            Assert.IsFalse(LiveRepainter.Due(0.3, 0.3 + LiveRepainter.PassPhaseTrigger / 2, 2f, 5f), "a small move waits");
            Assert.IsTrue(LiveRepainter.Due(0.995, 0.01, 2f, 5f), "the year wraps");
        }

        [TestMethod]
        public void APassIsLoggedWhenItChangedSomethingAndIsNewOrTheLastLineIsOld()
        {
            Assert.IsFalse(LiveRepainter.ShouldLog(0, 0.3, double.NaN, 1000f), "nothing repainted");
            Assert.IsTrue(LiveRepainter.ShouldLog(5, 0.3, double.NaN, 0f), "the first line");
            Assert.IsFalse(LiveRepainter.ShouldLog(5, 0.301, 0.3, 10f));
            Assert.IsTrue(LiveRepainter.ShouldLog(5, 0.31, 0.3, 10f), "the phase moved");
            Assert.IsTrue(LiveRepainter.ShouldLog(5, 0.301, 0.3, LiveRepainter.LogThrottleSeconds + 1f), "the last line is old");
        }

        [TestMethod]
        public void ARebuildIsDueWhenAskedForOrWhenTheModeWantsItAndThePhaseMovedAndTheLastOneIsOld()
        {
            Assert.IsTrue(GroundRebuilder.Due(true, false, 0.5, 0.5, 0f), "asked for, whatever the rest");
            Assert.IsFalse(GroundRebuilder.Due(false, false, 0.9, 0.1, 1000f), "the mode does not want it");
            Assert.IsFalse(GroundRebuilder.Due(false, true, 0.51, 0.5, 1000f), "the phase has not moved enough");
            Assert.IsFalse(GroundRebuilder.Due(false, true, 0.6, 0.5, 10f), "the last rebuild is too young");
            Assert.IsTrue(GroundRebuilder.Due(false, true, 0.6, 0.5, GroundRebuilder.RebuildMinSeconds));
            Assert.IsTrue(GroundRebuilder.Due(false, true, 0.6, double.NaN, 1000f), "ground built without a phase");
            Assert.IsTrue(GroundRebuilder.Due(false, true, 0.01, 0.99 - 0.1, 1000f), "the year wraps");
        }

        [TestMethod]
        public void ARebuildRunsOnlyWhereNothingStandsOnTheGround()
        {
            Assert.IsFalse(GroundRebuilder.SafeToRebuild(false, false, GameScenes.FLIGHT, false), "no sphere");
            Assert.IsTrue(GroundRebuilder.SafeToRebuild(true, false, GameScenes.SPACECENTER, true), "a sphere that is not started builds with the mod");
            Assert.IsTrue(GroundRebuilder.SafeToRebuild(true, true, GameScenes.FLIGHT, false));
            Assert.IsTrue(GroundRebuilder.SafeToRebuild(true, true, GameScenes.TRACKSTATION, false));
            Assert.IsFalse(GroundRebuilder.SafeToRebuild(true, true, GameScenes.FLIGHT, true), "a vessel is on the ground");
            Assert.IsFalse(GroundRebuilder.SafeToRebuild(true, true, GameScenes.SPACECENTER, false), "never in the space centre");
        }

        private sealed class Target
        {
            public int Number;
            public string Text;
            public int Count() { return 1; }
            public int Count(int by) { return by; }
        }

        [TestMethod]
        public void TheReflectionLookupFindsMembersAndRecordsTheMissingOnes()
        {
            var link = new ReflectLink();
            Type type = typeof(Target);
            Assert.IsNotNull(link.Field(type, "Target", "Number", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, typeof(int)));
            Assert.IsNull(link.Field(type, "Target", "Text", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, typeof(int)), "a field of another type");
            Assert.IsNull(link.Field(type, "Target", "Nope", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
            Assert.IsNull(link.Field(null, "Gone", "Number", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance), "a type that is not there");
            Assert.IsNotNull(link.Method(type, "Target", "Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, new[] { typeof(int) }));
            Assert.IsNotNull(link.Method(type, "Target", "Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes));
            Assert.IsNull(link.Property(type, "Target", "Number", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance), "a field is not a property");
            Assert.IsFalse(link.Complete);
            Assert.AreEqual("Target.Text, Target.Nope, Gone.Number, Target.Number", link.MissingText());
            Assert.AreEqual("none", new ReflectLink().MissingText());
        }
    }
}
