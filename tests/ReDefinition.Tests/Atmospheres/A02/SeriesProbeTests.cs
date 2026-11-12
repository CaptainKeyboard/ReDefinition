using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The base of a series probe: active only with -atmoSeries, stepping of the scenario, variants, the verdict line, and the quit.
    [TestClass]
    public class SeriesProbeTests
    {
        private static readonly List<string> Log = new List<string>();
        private static readonly List<string> Results = new List<string>();

        private sealed class ScriptProbe : SeriesProbe
        {
            internal string[] Arguments = { "KSP_x64.exe", "-atmoSeries", "K-test", "-atmoStart", "run1" };
            internal Func<ScriptProbe, IEnumerator<ProbeWait>> Body;
            internal double Limit = 16 * 60;
            internal readonly List<string> Applied = new List<string>();
            internal int Quits;

            internal ScriptProbe()
            {
                QuitGame = () => Quits++;
            }

            protected override string SeriesName { get { return "K-test"; } }
            protected override double MaxSeconds { get { return Limit; } }
            protected override string[] Args() { return Arguments; }
            protected override IEnumerator<ProbeWait> Scenario() { return Body(this); }
            protected override void ApplyVariant(string name) { Applied.Add("on:" + name); }
            protected override void RevertVariant(string name) { Applied.Add("off:" + name); }

            internal void DoCheck(string name, bool ok) { Check(name, ok); }
            internal void DoEmit(string metric, double value) { Emit(metric, value); }
            internal void DoBegin(string variant) { BeginVariant(variant); }
            internal void DoEnd() { EndVariant(); }
        }

        [TestInitialize]
        public void Capture()
        {
            Log.Clear();
            Results.Clear();
            SeriesProbe.ForgetRunningProbes();
            SeriesProbe.LogLine = Log.Add;
            AtmoResult.Sink = Results.Add;
        }

        [TestCleanup]
        public void Release()
        {
            SeriesProbe.LogLine = message => AtmospheresLog.Say(message);
            AtmoResult.Sink = line => UnityEngine.Debug.Log(line);
        }

        private static void RunFrames(ScriptProbe probe, int frames, float dt)
        {
            for (int i = 0; i < frames; i++) probe.Tick(dt);
        }

        private static IEnumerator<ProbeWait> Empty(ScriptProbe p)
        {
            yield break;
        }

        [TestMethod]
        public void WithoutTheSwitchTheProbeIsInactiveAndSilent()
        {
            var probe = new ScriptProbe { Arguments = new[] { "KSP_x64.exe", "-popupwindow" }, Body = Empty };
            probe.Init();
            RunFrames(probe, 5, 0.1f);
            Assert.IsFalse(probe.Active);
            Assert.AreEqual(0, Log.Count);
            Assert.AreEqual(0, Results.Count);
        }

        [TestMethod]
        public void ASeriesNameMatchesWithoutDashesAndCase()
        {
            Assert.IsTrue(SeriesProbe.IsActive(new[] { "x", "-atmoSeries", "ktest" }, "K-test"));
            Assert.IsTrue(SeriesProbe.IsActive(new[] { "x", "-ATMOSERIES", "K_Test" }, "K-test"));
            Assert.IsFalse(SeriesProbe.IsActive(new[] { "x", "-atmoSeries", "K-other" }, "K-test"));
            Assert.IsFalse(SeriesProbe.IsActive(new[] { "x", "-atmoSeries" }, "K-test"));
        }

        private static IEnumerator<ProbeWait> Stepping(ScriptProbe p)
        {
            p.DoEmit("a", 1);
            yield return ProbeWait.Frames(2);
            p.DoEmit("b", 2);
            yield return ProbeWait.Seconds(1.0);
            p.DoEmit("c", 3);
            bool ready = false;
            Func<bool> cond = () => ready;
            yield return ProbeWait.Until(() => { ready = true; return true; }, 5, "ready");
            p.DoCheck("done", ready);
        }

        [TestMethod]
        public void TheScenarioStepsThroughFramesSecondsAndConditionsThenPasses()
        {
            var probe = new ScriptProbe { Body = Stepping };
            probe.Init();
            Assert.IsTrue(probe.Active);
            Assert.AreEqual("run1", probe.StartId);
            probe.Tick(0.1f);
            Assert.AreEqual(1, Results.Count, "the first step runs at the first tick");
            probe.Tick(0.1f);
            Assert.AreEqual(1, Results.Count, "two frames are waited");
            probe.Tick(0.1f);
            Assert.AreEqual(2, Results.Count);
            RunFrames(probe, 11, 0.1f);
            Assert.IsTrue(probe.Finished);
            Assert.IsTrue(probe.Passed);
            Assert.AreEqual(3, Results.Count);
            Assert.IsTrue(Log.Any(l => l == "Probe: PASSED"));
            StringAssert.Contains(Results[0], "\"series\":\"K-test\"");
        }

        [TestMethod]
        public void TheGameQuitsAfterTheVerdictUnlessKept()
        {
            var probe = new ScriptProbe { Body = Empty };
            probe.Init();
            probe.Tick(0.1f);
            Assert.IsTrue(probe.Passed);
            Assert.AreEqual(0, probe.Quits);
            RunFrames(probe, 40, 0.1f);
            Assert.AreEqual(1, probe.Quits);
            RunFrames(probe, 40, 0.1f);
            Assert.AreEqual(1, probe.Quits, "it quits once");

            var kept = new ScriptProbe { Body = Empty, Arguments = new[] { "x", "-atmoSeries", "K-test", "-atmoKeep" } };
            kept.Init();
            RunFrames(kept, 60, 0.1f);
            Assert.IsTrue(kept.Passed);
            Assert.AreEqual(0, kept.Quits);
        }

        private static IEnumerator<ProbeWait> Long(ScriptProbe p)
        {
            yield return ProbeWait.Seconds(100);
        }

        [TestMethod]
        public void TheGameQuitsOnlyAfterTheLastProbeOfTheStartHasItsVerdict()
        {
            var shortProbe = new ScriptProbe { Body = Empty };
            var longProbe = new ScriptProbe { Body = Long };
            shortProbe.Init();
            longProbe.Init();
            RunFrames(shortProbe, 60, 0.1f);
            Assert.IsTrue(shortProbe.Finished);
            Assert.AreEqual(0, shortProbe.Quits, "a probe of the start is still running");
            longProbe.Tick(0.1f);
            longProbe.Limit = 1;
            RunFrames(longProbe, 20, 0.1f);
            Assert.IsTrue(longProbe.Finished);
            RunFrames(shortProbe, 40, 0.1f);
            Assert.AreEqual(1, shortProbe.Quits, "the last verdict lets the game quit");
        }

        [TestMethod]
        public void TheProbeSwitchTakesAListOfNames()
        {
            Assert.IsTrue(SeriesProbe.NamesProbe("B12Probe,C25Probe", "C25Probe"));
            Assert.IsTrue(SeriesProbe.NamesProbe("b12probe, c25probe", "C25Probe"));
            Assert.IsFalse(SeriesProbe.NamesProbe("B12Probe,C25Probe", "D30Probe"));
            var probe = new ScriptProbe { Body = Empty, Arguments = new[] { "x", "-atmoSeries", "K-test", "-atmoProbe", "Other,ScriptProbe" } };
            probe.Init();
            Assert.IsTrue(probe.Active);
        }

        private static IEnumerator<ProbeWait> FailingCheck(ScriptProbe p)
        {
            p.DoCheck("fog density", false);
            yield return ProbeWait.Frames(1);
            p.DoEmit("after", 1);
        }

        [TestMethod]
        public void AFailedCheckFailsTheProbeButTheScenarioGoesOn()
        {
            var probe = new ScriptProbe { Body = FailingCheck };
            probe.Init();
            RunFrames(probe, 5, 0.1f);
            Assert.IsTrue(probe.Finished);
            Assert.IsFalse(probe.Passed);
            Assert.AreEqual(1, Results.Count, "the result after the failed check was still written");
            Assert.IsTrue(Log.Any(l => l.StartsWith("Probe: FAILED") && l.Contains("fog density")));
        }

        private static IEnumerator<ProbeWait> NeverReady(ScriptProbe p)
        {
            yield return ProbeWait.Until(() => false, 2.0, "the vessel to land");
        }

        [TestMethod]
        public void AConditionThatNeverHoldsTimesOut()
        {
            var probe = new ScriptProbe { Body = NeverReady };
            probe.Init();
            RunFrames(probe, 40, 0.1f);
            Assert.IsFalse(probe.Passed);
            Assert.IsTrue(Log.Any(l => l.StartsWith("Probe: FAILED") && l.Contains("the vessel to land")));
        }

        private static IEnumerator<ProbeWait> Throws(ScriptProbe p)
        {
            yield return ProbeWait.Frames(1);
            throw new InvalidOperationException("scenario broke");
        }

        [TestMethod]
        public void AnExceptionInTheScenarioFailsTheProbeWithoutEscaping()
        {
            var probe = new ScriptProbe { Body = Throws };
            probe.Init();
            RunFrames(probe, 5, 0.1f);
            Assert.IsTrue(probe.Finished);
            Assert.IsTrue(Log.Any(l => l.StartsWith("Probe: FAILED") && l.Contains("scenario broke")));
        }

        [TestMethod]
        public void TheTimeLimitEndsAScenarioThatNeverEnds()
        {
            var probe = new ScriptProbe { Body = NeverReady, Limit = 1.0 };
            probe.Init();
            RunFrames(probe, 30, 0.1f);
            Assert.IsTrue(probe.Finished);
            Assert.IsTrue(Log.Any(l => l.StartsWith("Probe: FAILED") && l.Contains("did not end within")));
        }

        [TestMethod]
        public void AShutdownBeforeTheEndFailsTheProbe()
        {
            var probe = new ScriptProbe { Body = NeverReady };
            probe.Init();
            probe.Tick(0.1f);
            probe.Shutdown();
            Assert.IsTrue(probe.Finished);
            Assert.IsTrue(Log.Any(l => l.StartsWith("Probe: FAILED") && l.Contains("shut down")));
        }

        private static IEnumerator<ProbeWait> TwoVariants(ScriptProbe p)
        {
            foreach (string v in new List<string>(p.Variants))
            {
                p.DoBegin(v);
                p.DoEmit("sigma", 1);
                yield return ProbeWait.Frames(1);
                p.DoEnd();
            }
            p.DoEmit("after", 0);
        }

        [TestMethod]
        public void VariantsSwitchInsideOneStartAndTheirLinesCarryTheName()
        {
            var probe = new ScriptProbe
            {
                Body = TwoVariants,
                Arguments = new[] { "x", "-atmoSeries", "K-test", "-atmoVariants", "fog, snow" }
            };
            probe.Init();
            CollectionAssert.AreEqual(new[] { "fog", "snow" }, probe.Variants.ToArray());
            RunFrames(probe, 10, 0.1f);
            Assert.IsTrue(probe.Passed);
            CollectionAssert.AreEqual(new[] { "on:fog", "off:fog", "on:snow", "off:snow" }, probe.Applied.ToArray());
            StringAssert.Contains(Results[0], "\"variant\":\"fog\"");
            StringAssert.Contains(Results[1], "\"variant\":\"snow\"");
            Assert.IsFalse(Results[2].Contains("variant"), "the line after the last variant has none");
        }

        [TestMethod]
        public void AVariantStillOnAtTheEndIsRevertedBeforeTheVerdict()
        {
            var probe = new ScriptProbe
            {
                Body = p => OneVariantLeftOn(p),
                Arguments = new[] { "x", "-atmoSeries", "K-test" }
            };
            probe.Init();
            RunFrames(probe, 5, 0.1f);
            Assert.IsTrue(probe.Passed);
            CollectionAssert.AreEqual(new[] { "on:solo", "off:solo" }, probe.Applied.ToArray());
        }

        private static IEnumerator<ProbeWait> OneVariantLeftOn(ScriptProbe p)
        {
            p.DoBegin("solo");
            yield return ProbeWait.Frames(1);
        }
    }
}
