using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34: the orbit overlay host's choice of bodies, the frame of latitude and longitude, and the rules of the code of the
    // package that a grep can check (no SnowMask, no second cold, no ScenarioModule, nothing unguarded from FlightGlobals).
    [TestClass]
    public class CoverPlatformTests
    {
        // ---- the host ------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheFocusedBodyIsDrawnFirstAndAloneInFocusedMode()
        {
            List<int> p = OrbitOverlayHost.Pick(6, 2, OrbitBodiesMode.Focused, i => true, 0);
            CollectionAssert.AreEqual(new[] { 2 }, p);
            Assert.AreEqual(0, OrbitOverlayHost.Pick(6, -1, OrbitBodiesMode.Focused, i => true, 0).Count, "no focus, nothing");
            Assert.AreEqual(0, OrbitOverlayHost.Pick(0, -1, OrbitBodiesMode.All, i => true, 0).Count);
        }

        [TestMethod]
        public void TheOthersFollowOneByOneAndOnlyTheVisibleOnesInVisibleMode()
        {
            var seen = new HashSet<int>();
            for (int turn = 0; turn < 10; turn++)
            {
                List<int> p = OrbitOverlayHost.Pick(6, 2, OrbitBodiesMode.All, i => true, turn);
                Assert.AreEqual(2, p[0], "the focus stays first");
                Assert.AreEqual(2, p.Count, "one other body a round");
                Assert.AreNotEqual(2, p[1]);
                seen.Add(p[1]);
            }
            Assert.AreEqual(5, seen.Count, "every other body gets its turn");
            seen.Clear();
            for (int turn = 0; turn < 10; turn++)
            {
                List<int> p = OrbitOverlayHost.Pick(6, 2, OrbitBodiesMode.Visible, i => i == 1 || i == 4, turn);
                foreach (int i in p.Skip(1)) seen.Add(i);
            }
            CollectionAssert.AreEquivalent(new[] { 1, 4 }, seen.ToArray(), "only the visible ones");
            Assert.AreEqual(1, OrbitOverlayHost.Pick(6, 2, OrbitBodiesMode.Visible, i => false, 0).Count, "none visible: the focus alone");
            Assert.AreEqual(1, OrbitOverlayHost.Pick(6, 2, OrbitBodiesMode.Visible, null, 0).Count);
            Assert.AreEqual(2, OrbitOverlayHost.Pick(3, -1, OrbitBodiesMode.All, i => true, 1).Count + 1, "without a focus one body a round");
        }

        private sealed class Layer : IOrbitOverlayLayer
        {
            internal readonly List<string> Log;
            internal readonly string Id;
            internal readonly int At;
            internal bool Throws;
            internal bool Wanted = true;

            internal Layer(List<string> log, string id, int order)
            {
                Log = log;
                Id = id;
                At = order;
            }

            public string Name { get { return Id; } }
            public int Order { get { return At; } }
            public bool Wants(CelestialBody body) { return Wanted; }

            public void Draw(CelestialBody body, bool focused, double ut)
            {
                if (Throws) throw new InvalidOperationException("broken");
                Log.Add(Id + (focused ? "!" : ""));
            }
        }

        [TestMethod]
        public void TheHostDrawsTheRegisteredLayersInOrderAndOneBrokenLayerDoesNotStopTheOthers()
        {
            var log = new List<string>();
            var cover = new Layer(log, "cover", OrbitCover.LayerOrder);
            var ice = new Layer(log, "ice", 20);
            var cloud = new Layer(log, "cloud", 40);
            var off = new Layer(log, "off", 50) { Wanted = false };
            foreach (Layer l in new[] { cloud, off, cover, ice }) OrbitOverlayLayers.Register(l);
            Action<string, Exception> before = OrbitOverlayHost.OnFailure;
            var failed = new List<string>();
            OrbitOverlayHost.OnFailure = (name, e) => failed.Add(name);
            try
            {
                int drawn = OrbitOverlayHost.Draw(null, true, 0);
                Assert.AreEqual(3, drawn);
                CollectionAssert.AreEqual(new[] { "cover!", "ice!", "cloud!" }, log, "cover, ice, cloud shell, in that order");
                ice.Throws = true;
                log.Clear();
                OrbitOverlayHost.Draw(null, false, 0);
                CollectionAssert.AreEqual(new[] { "cover", "cloud" }, log);
                CollectionAssert.AreEqual(new[] { "ice" }, failed);
            }
            finally
            {
                OrbitOverlayHost.OnFailure = before;
                foreach (Layer l in new[] { cloud, off, cover, ice }) OrbitOverlayLayers.Unregister(l);
            }
        }

        [TestMethod]
        public void TheCoverLayerDrawsBeforeTheCloudShellAndTheOrbitTickCallsTheHost()
        {
            Assert.IsTrue(OrbitCover.LayerOrder < 40, "after the planet, before the cloud shell");
            string tick = File.ReadAllText(RepoRoot.Locate("src", "Atmospheres", "Cover", "OrbitSnow.cs"));
            Assert.IsTrue(tick.Contains("OrbitOverlayHost.DrawRound"), "the orbit painter's tick calls the host");
            Assert.IsTrue(tick.Contains("OrbitOverlayLayers.Register"), "and registers the cover layer");
        }

        // ---- the frame of latitude and longitude -----------------------------------------------------------------------------------

        [TestMethod]
        public void TheSphereFrameIsTheIdentityOfTheSphereConvention()
        {
            for (double lat = -80; lat <= 80; lat += 20)
                for (double lon = -170; lon <= 170; lon += 35)
                {
                    double x, y, z, la, lo;
                    Geo.DirectionOf(lat * Math.PI / 180.0, lon * Math.PI / 180.0, out x, out y, out z);
                    KspFrame.Sphere.LatLon(x, y, z, out la, out lo);
                    Assert.AreEqual(lat, la, 1e-9);
                    Assert.AreEqual(lon, lo, 1e-9);
                }
            Assert.IsTrue(KspFrame.Sphere.Valid);
        }

        [TestMethod]
        public void AFrameMeasuredFromTheGamesOwnDirectionsFindsTheGamesLongitude()
        {
            // A body whose longitude 0 lies along +x, whose longitude 90 lies along -z (east runs the other way round the pole axis)
            // and whose pole is +y.
            var f = new KspFrame(1, 0, 0, 0, 0, -1, 0, 1, 0);
            Assert.IsTrue(f.Valid);
            for (double lat = -70; lat <= 70; lat += 35)
                for (double lon = -170; lon <= 170; lon += 40)
                {
                    double x, y, z, la, lo;
                    f.Direction(lat, lon, out x, out y, out z);
                    Assert.AreEqual(1.0, x * x + y * y + z * z, 1e-9);
                    f.LatLon(x, y, z, out la, out lo);
                    Assert.AreEqual(lat, la, 1e-9);
                    Assert.AreEqual(lon, lo, 1e-9);
                }
            double ax, ay, az, bx, by, bz;
            f.Direction(0, 0, out ax, out ay, out az);
            f.Direction(0, 90, out bx, out by, out bz);
            Assert.AreEqual(1.0, ax, 1e-9);
            Assert.AreEqual(-1.0, bz, 1e-9);
            Assert.IsFalse(new KspFrame(1, 0, 0, 1, 0, 0, 0, 1, 0).Valid, "two equal directions are no frame");
            double la0, lo0;
            f.LatLon(0, 0, 0, out la0, out lo0);
            Assert.AreEqual(0.0, la0, 0.0, "a zero direction is handled");
        }

        // ---- the rules of the package, checked in the source --------------------------------------------------------------------------

        private static IEnumerable<string> CodeLines(string file)
        {
            foreach (string line in File.ReadAllLines(file))
            {
                string t = line.Trim();
                if (t.StartsWith("//") || t.Length == 0) continue;
                int c = t.IndexOf("//", StringComparison.Ordinal);
                yield return c >= 0 ? t.Substring(0, c) : t;
            }
        }

        private static IEnumerable<string> Sources(string folder)
        {
            return Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        }

        [TestMethod]
        public void NoCodeReferencesSnowMaskExceptTheShimAndItsLastCallers()
        {
            string src = RepoRoot.Locate("src");
            string tests = RepoRoot.Locate("tests", "ReDefinition.Tests");
            // The shim, the one caller of package C20 and its test, and the default of the old setting that caller reads.
            var allowed = new HashSet<string> { "SnowMaskShim.cs", "StormEffect.cs", "StormSkyAndSnowfallTests.cs", "AtmospheresSettings.Legacy.cs", "CoverPlatformTests.cs" };
            var offenders = new List<string>();
            foreach (string file in Sources(src).Concat(Sources(tests)))
            {
                if (allowed.Contains(Path.GetFileName(file))) continue;
                if (CodeLines(file).Any(l => l.Contains("SnowMask"))) offenders.Add(Path.GetFileName(file));
            }
            Assert.AreEqual(0, offenders.Count, "SnowMask is referenced by: " + string.Join(", ", offenders));
            Assert.IsFalse(File.Exists(Path.Combine(src, "Atmospheres", "SnowMask.cs")), "the class is deleted");
            Assert.IsFalse(File.Exists(Path.Combine(tests, "SnowMaskTests.cs")), "and its tests");
        }

        [TestMethod]
        public void TheCoverFolderNamesNoSingleBodyAndNoUnguardedFlightGlobals()
        {
            string root = RepoRoot.Locate("src", "Atmospheres", "Cover");
            var named = new List<string>();
            var unguarded = new List<string>();
            foreach (string file in Sources(root))
            {
                string name = Path.GetFileName(file);
                foreach (string line in CodeLines(file))
                {
                    if (line.Contains("SeasonClock.BodyName") || line.Contains("SeasonClock.Applies") || line.Contains("\"Kerbin\"")) named.Add(name);
                    if (line.Contains("FlightGlobals.")) unguarded.Add(name);
                }
            }
            Assert.AreEqual(0, named.Distinct().Count(), "a body is named in: " + string.Join(", ", named.Distinct()));
            Assert.AreEqual(0, unguarded.Distinct().Count(), "FlightGlobals is read directly (use GameGlobals) in: " + string.Join(", ", unguarded.Distinct()));
        }

        [TestMethod]
        public void NoCoverCodeTouchesTheSaveGame()
        {
            string root = RepoRoot.Locate("src", "Atmospheres", "Cover");
            foreach (string file in Sources(root))
            {
                foreach (string line in CodeLines(file))
                {
                    Assert.IsFalse(line.Contains("ScenarioModule") || line.Contains("KSPScenario") || line.Contains("ConfigNode"),
                        Path.GetFileName(file) + " reaches into the save game: " + line);
                }
            }
        }

        [TestMethod]
        public void NoCoverCodeKeepsAColdOfItsOwn()
        {
            // The depth is the field's: no season phase, no snow line, no winter line in the cover.
            string root = RepoRoot.Locate("src", "Atmospheres", "Cover");
            foreach (string file in Sources(root))
            {
                if (Path.GetFileName(file) == "SnowMaskShim.cs") continue;
                foreach (string line in CodeLines(file))
                {
                    Assert.IsFalse(line.Contains("SeasonState.Phase") || line.Contains("WinterSnowLine") || line.Contains("SeasonClock.Phase"),
                        Path.GetFileName(file) + " reads the season: " + line);
                }
            }
        }

        [TestMethod]
        public void TheCoverReadsTheFieldsSurfaceMapAndNothingElseForItsDepth()
        {
            string resampler = File.ReadAllText(RepoRoot.Locate("src", "Atmospheres", "Cover", "CoverResampler.cs"));
            Assert.IsTrue(resampler.Contains("WeatherMapKind.SnowCover"));
            Assert.IsTrue(resampler.Contains("WeatherHub.Field"));
        }

        [TestMethod]
        public void TheSettingsOfTheDeletedFeaturesAreGoneFromTheLegacyFile()
        {
            string legacy = File.ReadAllText(RepoRoot.Locate("src", "Atmospheres", "Settings", "AtmospheresSettings.Legacy.cs"));
            foreach (string gone in new[] { "SnowStrength", "OrbitSnowWidth", "GroundRefreshMode", "RefreshSeconds", "enum GroundRefresh", "bool OrbitSnow" })
                Assert.IsFalse(legacy.Contains(gone), gone + " is still in the legacy settings");
        }
    }
}
