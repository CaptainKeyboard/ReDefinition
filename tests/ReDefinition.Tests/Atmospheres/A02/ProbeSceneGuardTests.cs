using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The first-contact guard of the series probes. The first K-T2 start failed in the main menu: T09Probe's first wait read
    // FlightGlobals.ActiveVessel before the flight scene existed. Two tests keep that from coming back: the wait/step logic is driven with a fake scene
    // (menu, loading, flight not ready, flight ready, flight lost), and the source of every probe is parsed for waits that read the flight without
    // the scene guard.
    [TestClass]
    public class ProbeSceneGuardTests
    {
        private enum FakeScene { Menu, Loading, FlightNotReady, FlightReady, SpaceCentre }

        private static FakeScene scene;
        private static bool vesselPresent;
        private static readonly List<string> Log = new List<string>();
        private static Func<bool> savedState;

        // A probe with the shape of T09Probe: a first wait on the flight, then a step that needs the vessel, and the LostGround hook.
        private sealed class FlightProbe : SeriesProbe
        {
            internal int StepsRun;
            internal int MenuReads;
            internal bool Reached;
            internal double Limit = 16 * 60;

            internal FlightProbe() { QuitGame = () => { }; }

            protected override string SeriesName { get { return "K-test"; } }
            protected override double MaxSeconds { get { return Limit; } }
            protected override string[] Args() { return new[] { "KSP_x64.exe", "-atmoSeries", "K-test" }; }

            protected override string LostGround()
            {
                if (!Reached) return null;
                return InFlightReady() ? null : "the flight is gone";
            }

            protected override IEnumerator<ProbeWait> Scenario()
            {
                yield return ProbeWait.Until(() =>
                {
                    // Like the unguarded original: outside the flight the game object does not exist, so reading the vessel throws.
                    if (!InFlightReady()) { MenuReads++; return false; }
                    return true;
                }, 100, "the flight");
                Reached = true;
                yield return ProbeWait.Frames(2);
                StepsRun++;
                yield return ProbeWait.Frames(2);
                StepsRun++;
            }
        }

        // The same first wait without the guard: throws in the menu, as the real one did.
        private sealed class UnguardedProbe : SeriesProbe
        {
            internal UnguardedProbe() { QuitGame = () => { }; }

            protected override string SeriesName { get { return "K-test"; } }
            protected override string[] Args() { return new[] { "KSP_x64.exe", "-atmoSeries", "K-test" }; }

            protected override IEnumerator<ProbeWait> Scenario()
            {
                yield return ProbeWait.Until(() =>
                {
                    if (scene != FakeScene.FlightReady && scene != FakeScene.FlightNotReady) throw new NullReferenceException("FlightGlobals.fetch is null");
                    return scene == FakeScene.FlightReady;
                }, 100, "the flight");
            }
        }

        [TestInitialize]
        public void Capture()
        {
            Log.Clear();
            SeriesProbe.ForgetRunningProbes();
            SeriesProbe.LogLine = Log.Add;
            savedState = SeriesProbe.FlightReadyState;
            scene = FakeScene.Menu;
            vesselPresent = true;
            SeriesProbe.FlightReadyState = () => scene == FakeScene.FlightReady && vesselPresent;
        }

        [TestCleanup]
        public void Release()
        {
            SeriesProbe.FlightReadyState = savedState;
            SeriesProbe.LogLine = message => AtmospheresLog.Say(message);
        }

        private static void Run(SeriesProbe p, int frames)
        {
            for (int i = 0; i < frames; i++) p.Tick(0.1f);
        }

        [TestMethod]
        public void TheProbeWaitsThroughMenuLoadingAndNotReadyAndRunsOnceTheFlightIsReady()
        {
            var p = new FlightProbe();
            p.Init();
            foreach (FakeScene s in new[] { FakeScene.Menu, FakeScene.Loading, FakeScene.SpaceCentre, FakeScene.FlightNotReady })
            {
                scene = s;
                Run(p, 20);
                Assert.IsFalse(p.Finished, "still waiting in " + s);
                Assert.IsFalse(p.Reached, "no step ran in " + s);
            }
            Assert.IsTrue(p.MenuReads > 0);
            scene = FakeScene.FlightReady;
            Run(p, 20);
            Assert.IsTrue(p.Finished);
            Assert.IsTrue(p.Passed, string.Join("; ", p.Failures.ToArray()));
            Assert.AreEqual(2, p.StepsRun);
        }

        [TestMethod]
        public void LeavingTheFlightOrLosingTheVesselMidRunEndsTheProbeWithThatReason()
        {
            var left = new FlightProbe();
            left.Init();
            scene = FakeScene.FlightReady;
            Run(left, 3);
            Assert.IsTrue(left.Reached);
            scene = FakeScene.SpaceCentre;
            Run(left, 2);
            Assert.IsTrue(left.Finished);
            Assert.IsFalse(left.Passed);
            Assert.IsTrue(left.Failures.Any(f => f.Contains("the flight is gone")));
            Assert.IsTrue(left.StepsRun < 2, "no step ran after the scene was left");

            SeriesProbe.ForgetRunningProbes();
            var destroyed = new FlightProbe();
            destroyed.Init();
            scene = FakeScene.FlightReady;
            Run(destroyed, 3);
            vesselPresent = false;
            Run(destroyed, 2);
            Assert.IsTrue(destroyed.Finished);
            Assert.IsTrue(destroyed.Failures.Any(f => f.Contains("the flight is gone")));
        }

        [TestMethod]
        public void AConditionThatThrowsBeforeTheFlightIsAWaitNotAnEndAndStillReportsAtTheTimeout()
        {
            var p = new UnguardedProbe();
            p.Init();
            scene = FakeScene.Menu;
            Run(p, 30);
            Assert.IsFalse(p.Finished, "a menu-time exception no longer ends the probe");
            scene = FakeScene.FlightReady;
            Run(p, 5);
            Assert.IsTrue(p.Passed);

            SeriesProbe.ForgetRunningProbes();
            var never = new UnguardedProbe();
            never.Init();
            scene = FakeScene.Loading;
            Run(never, 1100);
            Assert.IsTrue(never.Finished);
            Assert.IsFalse(never.Passed);
            Assert.IsTrue(never.Failures.Any(f => f.Contains("timeout after 100 s") && f.Contains("NullReferenceException")), "the failure names the exception: " + string.Join("; ", never.Failures.ToArray()));
        }

        [TestMethod]
        public void AnExceptionInAScenarioStepStillFailsTheProbe()
        {
            var p = new StepThrowsProbe();
            p.Init();
            Run(p, 5);
            Assert.IsTrue(p.Finished);
            Assert.IsTrue(p.Failures.Any(f => f.StartsWith("exception in the scenario: InvalidOperationException")));
        }

        private sealed class StepThrowsProbe : SeriesProbe
        {
            internal StepThrowsProbe() { QuitGame = () => { }; }

            protected override string SeriesName { get { return "K-test"; } }
            protected override string[] Args() { return new[] { "KSP_x64.exe", "-atmoSeries", "K-test" }; }

            protected override IEnumerator<ProbeWait> Scenario()
            {
                yield return ProbeWait.Frames(1);
                throw new InvalidOperationException("step");
            }
        }

        // Every wait of every probe that reads the flight (the active vessel, the flight camera, FlightGlobals.ready, the main body) must test the scene first in the same
        // expression, because FlightGlobals does not exist in the main menu.
        [TestMethod]
        public void EveryWaitThatReadsTheFlightTestsTheSceneFirst()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string atmo = Path.Combine(root, "src", "Atmospheres");
            var bad = new List<string>();
            int waits = 0, flightWaits = 0;
            var probeFiles = Directory.GetFiles(atmo, "*.cs", SearchOption.AllDirectories).Where(f => f.Replace('\\', '/').Contains("/Probe/")).ToList();
            Assert.IsTrue(probeFiles.Count >= 10, "the probe files are found (" + probeFiles.Count + ")");
            var guard = new Regex(@"HighLogic\.(LoadedScene|LoadedSceneIsFlight)|InFlightReady\(\)|InFlight\(\)");
            var reads = new Regex(@"FlightGlobals\.(ActiveVessel|ready|currentMainBody|fetch)|FlightCamera\.fetch");
            var end = new Regex(@"[\)\w\}]\s*,\s*\d+(\.\d+)?\s*,\s*""");
            foreach (string file in probeFiles)
            {
                string text = File.ReadAllText(file);
                int at = 0;
                while ((at = text.IndexOf("ProbeWait.Until(", at, StringComparison.Ordinal)) >= 0)
                {
                    waits++;
                    Match m = end.Match(text, at);
                    string span = m.Success ? text.Substring(at, m.Index - at) : text.Substring(at, Math.Min(400, text.Length - at));
                    at += 16;
                    Match r = reads.Match(span);
                    if (!r.Success) continue;
                    flightWaits++;
                    Match g = guard.Match(span);
                    if (!g.Success || g.Index > r.Index)
                    {
                        int line = text.Substring(0, at).Count(c => c == '\n') + 1;
                        bad.Add(Path.GetFileName(file) + ":" + line + " reads " + r.Value + " without the scene test before it");
                    }
                }
            }
            Assert.IsTrue(flightWaits >= 8, "the flight waits are recognised (" + flightWaits + " of " + waits + ")");
            Assert.AreEqual(0, bad.Count, string.Join("\n", bad.ToArray()));
        }
    }
}
