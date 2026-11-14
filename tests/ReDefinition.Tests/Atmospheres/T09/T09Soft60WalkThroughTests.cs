using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The start soft60 of K-T2 (T09ProbeSoft60.cs, E40 after start 59): the variant table with the weave diagnosis, the plan against the time limit, the type-placed views against a fake
    // type map, the extra looks, the light rows, and the whole scenario against the fake world.
    public partial class T09WalkThroughTests
    {
        private const double Soft60SiteLat = -0.0972, Soft60SiteLon = -74.5577, Soft60Radius = 600000.0;

        private Run StartSoft60(Action<Run> tweak = null)
        {
            return Start(new[] { "plain" }, x =>
            {
                x.World.LayerReport = FourLayers(false);
                if (tweak != null) tweak(x);
            }, "1707", "soft60", "K-T2");
        }

        private static T09LayerReport Soft60Report(string cumulusType, float cumulusCover, string cbType = "Fog", float cbCover = 0.05f, string cirrusType = "Cirrus", float cirrusCover = 0.1f)
        {
            var report = new T09LayerReport { StackKey = "fake", Cold = false };
            string[] names = { "Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" };
            string[] types = { "Fog", cumulusType, cbType, cirrusType };
            float[] covers = { 0.05f, cumulusCover, cbCover, cirrusCover };
            for (int k = 0; k < 4; k++)
                report.Layers.Add(new T09LayerInfo { Index = k, Name = names[k], TypeName = types[k], CpuCover = covers[k], GpuCover = covers[k] });
            return report;
        }

        // A map with a patch of each type: humilis 40 km east of the site, congestus 110 km north-west, cirrus 245 km south-south-west; the cumulonimbus 48 km north-east only when cbHere says so
        // (the clock scene); everywhere else stratocumulus. A patch is 15 km wide.
        private static Func<double, double, T09LayerReport> Soft60Map(Func<bool> cbHere)
        {
            Func<double, double, double, double, bool> inside = (la, lo, bearing, km) =>
            {
                double plat, plon;
                T09Probe.Soft60Destination(Soft60SiteLat, Soft60SiteLon, bearing, km, Soft60Radius, out plat, out plon);
                return T09Probe.Soft60DistanceKm(la, lo, plat, plon, Soft60Radius) < 15.0;
            };
            return (la, lo) =>
            {
                string cumulus = inside(la, lo, 90.0, 40.0) ? "Cumulus humilis" : inside(la, lo, 315.0, 110.0) ? "Cumulus congestus" : "Stratocumulus";
                bool cirrus = inside(la, lo, 195.0, 245.0);
                bool cb = cbHere() && inside(la, lo, 45.0, 48.0);
                return Soft60Report(cumulus, 0.8f, cb ? "Cumulonimbus calvus" : "Fog", cb ? 0.6f : 0.05f, cirrus ? "Cirrus" : "Cirrostratus", cirrus ? 0.7f : 0.1f);
            };
        }

        private static void Soft60Drive(Run r, Action<Run> also = null)
        {
            Drive(r, 0.04, 3600.0, x =>
            {
                SoftFrame(x, new Color(1.4f, 1.2f, 0.97f), new Color(2.0f, 1.9f, 1.85f), new Color(1.95f, 1.86f, 1.8f));
                if (also != null) also(x);
            });
        }

        [TestMethod]
        public void TheSoft60TableHoldsTheWeaveCandidatesAndTheCostVariantsAndUsesNoRemovedKey()
        {
            var names = T09Probe.Soft60Variants.Select(v => v.Name).ToList();
            CollectionAssert.AreEqual(new[] { "soft", "softr33", "softcut16", "soft100s24", "softhash", "softacc10", "softacc05", "ref256" }, names);
            Assert.AreEqual(names.Count, names.Distinct().Count());
            // { fair cost, clock cost, fair views run, clock views run, frames, weave frames (the fair ground view) }
            var expected = new Dictionary<string, string[]>
            {
                { "soft", new[] { "ga", "gias", "ga", "gias", "3", "8" } }, { "softr33", new[] { "ga", "gias", "ga", "gias", "1", "8" } },
                { "softcut16", new[] { "g", "g", "g", "g", "1", "0" } }, { "soft100s24", new[] { "g", "", "g", "", "1", "0" } },
                { "softhash", new[] { "", "", "g", "", "1", "8" } }, { "softacc10", new[] { "", "", "g", "", "1", "8" } },
                { "softacc05", new[] { "", "", "g", "", "1", "8" } }, { "ref256", new[] { "", "", "g", "", "1", "8" } }
            };
            foreach (T09Probe.SoftVariant v in T09Probe.Soft60Variants)
            {
                string[] row = expected[v.Name];
                Assert.AreEqual(row[0], v.FairCost, v.Name + " fair cost");
                Assert.AreEqual(row[1], v.ClockCost, v.Name + " clock cost");
                foreach (char c in "ga") Assert.AreEqual(row[2].IndexOf(c) >= 0, v.Runs(true, c), v.Name + " fair view " + c);
                foreach (char c in "gias") Assert.AreEqual(row[3].IndexOf(c) >= 0, v.Runs(false, c), v.Name + " clock view " + c);
                Assert.AreEqual(int.Parse(row[4]), v.Frames, v.Name + " frames");
                Assert.AreEqual(int.Parse(row[5]), v.WeaveFrames, v.Name + " weave frames");
                Assert.AreEqual(int.Parse(row[5]) > 0 ? int.Parse(row[5]) : v.Frames, v.FramesIn(true, 'g'), v.Name + " frames in the fair ground view");
                Assert.AreEqual(v.Frames, v.FramesIn(true, 'a'), v.Name + " frames in the fair above view");
                Assert.AreEqual(v.Frames, v.FramesIn(false, 'g'), v.Name + " frames at the clock ground (the weave count is the fair ground's)");
                Assert.IsNull(v.TableProblem(), v.Name + " is a sound row");
                foreach (string pair in v.Pairs)
                {
                    bool setting;
                    string key;
                    float value;
                    Assert.IsTrue(T09Probe.SoftSplit(pair, out setting, out key, out value), v.Name + ": " + pair);
                    if (setting) Assert.IsNotNull(AtmospheresSettings.Definition(key), v.Name + ": the setting " + key + " exists");
                    foreach (string removed in SoftRemoved) Assert.IsFalse(key.Contains(removed), v.Name + ": " + key + " is on the removal list");
                }
            }
            // Every cost view the lead asked for: soft and softr33 in all views, softcut16 at the two grounds, soft100s24 at the fair ground.
            Assert.AreEqual("gias", T09Probe.Soft60Variants[0].ClockCost);
            Assert.AreEqual("gias", T09Probe.Soft60Variants[1].ClockCost);
            // The weave variants are the fair ground view only; the reference is the 256-step march.
            T09Probe.SoftExpectation soft = T09Probe.SoftExpect(T09Probe.Soft60Variants[0]);
            Func<string, T09Probe.SoftExpectation> of = n => T09Probe.SoftExpect(T09Probe.Soft60Variants.First(v => v.Name == n));
            Assert.AreEqual(0.2f, soft.Accumulate, 1e-6f, "the default history weight");
            Assert.AreEqual(0.1f, of("softacc10").Accumulate, 1e-6f);
            Assert.AreEqual(0.05f, of("softacc05").Accumulate, 1e-6f);
            Assert.AreEqual(1, of("softhash").DitherSequence);
            Assert.AreEqual(256, of("ref256").GridPoints, "the converged reference marches 256 grid points");
            Assert.AreEqual(48, soft.GridPoints);
            Assert.AreEqual(0.33f, of("softr33").Resolution, 1e-6f);
            Assert.AreEqual(0.5f, of("softacc05").Resolution, 1e-6f, "the history variants keep the half-size target");
            foreach (T09Probe.SoftVariant v in T09Probe.Soft60ExtraTable) Assert.AreEqual("", v.FairCost + v.ClockCost, "the extra looks have no cost window");
        }

        [TestMethod]
        public void TheSoft60PlanFitsTheGuardAndEveryPartIsPriced()
        {
            double estimate = T09Probe.Soft60EstimateS(T09Probe.Soft60Variants);
            Console.WriteLine("soft60 estimate " + estimate.ToString("0.0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0") + " s");
            Assert.IsTrue(estimate <= T09Probe.SoftMaxSeconds - 60.0, "the plan needs " + estimate.ToString("0") + " s of " + T09Probe.SoftMaxSeconds.ToString("0") + " (the pin is 1020)");
            Assert.AreEqual(1007.5, estimate, 1e-6, "the plan as built: 1007.5 s (1003 s before the slow history variants' extra settle seconds: 1.5 and 3)");
            // The evidence for the constants: a placement was measured at 3.6 s and a frame with its picture at 0.7 s in start 59.
            Assert.IsTrue(T09Probe.SoftPlaceS >= 1.9 * 3.6 && T09Probe.SoftFrameReadS >= 1.35 * 0.7);
            // A type view is priced with its placement, switch and frames; the cumulus bodies add the baseline frame.
            T09Probe.Soft60Target humilis = T09Probe.Soft60Targets[0], cirrus = T09Probe.Soft60Targets[3];
            Assert.IsTrue(humilis.Light && !cirrus.Light);
            Assert.AreEqual(T09Probe.Soft60TypeViewS(cirrus) + T09Probe.SoftFrameReadS + T09Probe.Soft60BaselineS, T09Probe.Soft60TypeViewS(humilis), 1e-9);
            // A table without the weave frames is shorter by the frames it does not read.
            var plain = T09Probe.Soft60Variants.Select(v => new { v.Name, v.WeaveFrames }).ToArray();
            try
            {
                foreach (T09Probe.SoftVariant v in T09Probe.Soft60Variants) v.WeaveFrames = 0;
                Assert.IsTrue(T09Probe.Soft60EstimateS(T09Probe.Soft60Variants) < estimate - 20.0, "the weave frames cost seconds the plan counts");
            }
            finally
            {
                for (int i = 0; i < plain.Length; i++) T09Probe.Soft60Variants[i].WeaveFrames = plain[i].WeaveFrames;
            }
            Assert.AreEqual(estimate, T09Probe.Soft60EstimateS(T09Probe.Soft60Variants), 1e-9);
        }

        [TestMethod]
        public void TheTypeGeometryPutsTheCellAtTheViewCentreFromTheSiteSide()
        {
            // The great circle: a place 40 km east of the site is 40 km away at a bearing near 90, and the way back is the same distance.
            double lat, lon;
            T09Probe.Soft60Destination(Soft60SiteLat, Soft60SiteLon, 90.0, 40.0, Soft60Radius, out lat, out lon);
            Assert.AreEqual(40.0, T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, lat, lon, Soft60Radius), 1e-6);
            Assert.AreEqual(90.0, T09Probe.Soft60Bearing(Soft60SiteLat, Soft60SiteLon, lat, lon), 0.5);
            double back, backLon;
            T09Probe.Soft60Destination(lat, lon, 270.0, 40.0, Soft60Radius, out back, out backLon);
            Assert.AreEqual(0.0, T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, back, backLon, Soft60Radius), 0.5, "forty kilometres back is the site");
            // A negative distance is the way back.
            double neg, negLon;
            T09Probe.Soft60Destination(lat, lon, 90.0, -40.0, Soft60Radius, out neg, out negLon);
            Assert.AreEqual(0.0, T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, neg, negLon, Soft60Radius), 0.5);
            // The longitude wraps.
            double wl, wlon;
            T09Probe.Soft60Destination(0.0, 179.9, 90.0, 50.0, Soft60Radius, out wl, out wlon);
            Assert.IsTrue(wlon < -170.0 && wlon > -180.0, "wrapped to " + wlon);

            var cell = new T09Probe.Soft60Cell { LatDeg = lat, LonDeg = lon, DistKm = 40.0, BearingDeg = 90.0, Cover = 0.8f, LayerName = "Kerbin-cumulus", TypeName = "Cumulus humilis" };
            T09Probe.Soft60Target humilis = T09Probe.Soft60Targets[0];
            // From the ground the view's centre meets the cloud 2300 m up at 20 degrees: (2300 - 200) / tan 20 = 5.8 km from the vessel, on the site's side of the cell.
            T09Probe.Soft60Pose ground = T09Probe.Soft60PoseFor(humilis, false, cell, Soft60SiteLat, Soft60SiteLon, Soft60Radius, 0.0);
            Assert.AreEqual(2100.0 / Math.Tan(20.0 * Math.PI / 180.0) / 1000.0, ground.ReachKm, 1e-9);
            Assert.AreEqual(ground.ReachKm, T09Probe.Soft60DistanceKm(ground.VesselLatDeg, ground.VesselLonDeg, lat, lon, Soft60Radius), 1e-6, "the vessel stands the view's reach from the cell");
            Assert.AreEqual(40.0 - ground.ReachKm, ground.MovedKm, 0.1, "and that much nearer the site than the cell");
            Assert.AreEqual(90.0, ground.View.HeadingDeg, 1.5, "the camera looks along the line from the site to the cell");
            Assert.AreEqual(20.0, ground.View.PitchDeg, 1e-9);
            Assert.AreEqual(200.0, ground.View.AglM);
            Assert.AreEqual("ty_humilis_g", ground.View.Name);
            // From above: 8300 m over the terrain, looking down 25 degrees at 2300 m: (8300 - 2300) / tan 25.
            T09Probe.Soft60Pose above = T09Probe.Soft60PoseFor(humilis, true, cell, Soft60SiteLat, Soft60SiteLon, Soft60Radius, 0.0);
            Assert.AreEqual(6000.0 / Math.Tan(25.0 * Math.PI / 180.0) / 1000.0, above.ReachKm, 1e-9);
            Assert.AreEqual(-25.0, above.View.PitchDeg, 1e-9);
            Assert.AreEqual(8300.0, above.View.AglM);
            Assert.AreEqual("ty_humilis_a", above.View.Name);
            // The terrain under the cell raises the camera: the reach is the height difference.
            T09Probe.Soft60Pose high = T09Probe.Soft60PoseFor(humilis, false, cell, Soft60SiteLat, Soft60SiteLon, Soft60Radius, 1000.0);
            Assert.IsTrue(high.ReachKm < ground.ReachKm);
            // A cell on the site: the vessel stands west of it and the camera looks east, as the other views do.
            var here = new T09Probe.Soft60Cell { LatDeg = Soft60SiteLat, LonDeg = Soft60SiteLon, DistKm = 0.0 };
            T09Probe.Soft60Pose onSite = T09Probe.Soft60PoseFor(humilis, false, here, Soft60SiteLat, Soft60SiteLon, Soft60Radius, 0.0);
            Assert.AreEqual(90.0, onSite.View.HeadingDeg, 1.5);
            Assert.AreEqual(onSite.ReachKm, onSite.MovedKm, 0.01);
            // The reach is clamped: a cirrus sheet seen at 35 degrees from 200 m is 14 km away; a deep view of Cb stays under the cap.
            foreach (T09Probe.Soft60Target t in T09Probe.Soft60Targets)
                foreach (bool up in new[] { false, true })
                {
                    T09Probe.Soft60Pose p = T09Probe.Soft60PoseFor(t, up, cell, Soft60SiteLat, Soft60SiteLon, Soft60Radius, 0.0);
                    Assert.IsTrue(p.ReachKm >= T09Probe.Soft60MinViewKm && p.ReachKm <= T09Probe.Soft60MaxViewKm, t.Name + (up ? " above" : " ground") + " reach " + p.ReachKm);
                    Assert.IsTrue(up ? p.View.PitchDeg < 0.0 : p.View.PitchDeg > 0.0);
                }
        }

        [TestMethod]
        public void TheTypeMatchAndTheCellChoiceFollowTheRules()
        {
            T09Probe.Soft60Target humilis = T09Probe.Soft60Targets[0], congestus = T09Probe.Soft60Targets[1], cb = T09Probe.Soft60Targets[2], cirrus = T09Probe.Soft60Targets[3];
            Assert.IsTrue(humilis.Matches("Kerbin-cumulus", "Cumulus humilis") && !humilis.Matches("Kerbin-cumulus", "Cumulus congestus") && !humilis.Matches("Kerbin-cumulonimbus", "Cumulus humilis"));
            Assert.IsTrue(congestus.Matches("Kerbin-cumulus", "Cumulus congestus"));
            Assert.IsTrue(cb.Matches("Kerbin-cumulonimbus", "Cumulonimbus calvus") && cb.Matches("Kerbin-cumulonimbus", "Cumulonimbus incus") && !cb.Matches("Kerbin-cumulus", "Cumulonimbus incus"));
            Assert.IsTrue(cirrus.Matches("Kerbin-cirrus", "Cirrus") && !cirrus.Matches("Kerbin-cirrus", "Cirrostratus"));
            Assert.IsFalse(humilis.Matches(null, "Cumulus humilis"));
            // A cell that reaches the cover beats one that does not, whatever its distance; among good ones the nearer wins; among weak ones the stronger.
            var weakNear = new T09Probe.Soft60Cell { DistKm = 6.0, Cover = 0.4f, Weak = true };
            var goodFar = new T09Probe.Soft60Cell { DistKm = 100.0, Cover = 0.6f };
            Assert.IsTrue(T09Probe.Soft60Better(null, 10.0, 0.4f, false));
            Assert.IsTrue(T09Probe.Soft60Better(weakNear, 100.0, 0.6f, true), "a good cell replaces a weak one");
            Assert.IsFalse(T09Probe.Soft60Better(goodFar, 6.0, 0.4f, false), "a weak cell does not replace a good one");
            Assert.IsTrue(T09Probe.Soft60Better(goodFar, 70.0, 0.56f, true), "the nearer good cell wins");
            Assert.IsFalse(T09Probe.Soft60Better(goodFar, 140.0, 0.9f, true), "the farther one loses whatever its cover");
            Assert.IsTrue(T09Probe.Soft60Better(goodFar, 100.0, 0.7f, true), "within one ring the stronger wins");
            Assert.IsTrue(T09Probe.Soft60Better(weakNear, 48.0, 0.45f, false), "among weak cells the stronger wins");
            Assert.IsFalse(T09Probe.Soft60Better(weakNear, 12.0, 0.3f, false));
        }

        [TestMethod]
        public void TheLightRowsSeparateTheSunlitSideFromTheBaseAndTheDeckFromTheSky()
        {
            int n = 100 * 100;
            var off = new Color[n];
            var raw = new Color[n];
            for (int i = 0; i < n; i++)
            {
                off[i] = new Color(0.30f, 0.40f, 0.60f, 1f);
                // The left half is clear sky; the right half a body: the upper rows lit (0.80), the lower rows its base (0.20).
                int x = i % 100, y = i / 100;
                raw[i] = x < 50 ? off[i] : (y >= 50 ? new Color(0.80f, 0.80f, 0.80f, 1f) : new Color(0.20f, 0.20f, 0.20f, 1f));
            }
            T09Probe.SoftLightStats s;
            Assert.IsTrue(T09Probe.SoftLightOf(raw, off, out s));
            Assert.AreEqual(0.5, s.CloudShare, 1e-9);
            Assert.AreEqual(0.5, s.BodyShare, 1e-9);
            Assert.AreEqual(0.80, s.TopLuma, 0.002, "the 90th percentile of the body is the lit side");
            Assert.AreEqual(0.20, s.BaseLuma, 0.002, "the 10th is the base");
            Assert.AreEqual(4.0, s.TopOverBase, 0.05);
            Assert.AreEqual(T09Probe.SoftLuma(off[0]), s.SkyLuma, 0.002, "the sky behind the body is the baseline's");
            Assert.AreEqual(s.MedianLuma / s.SkyLuma, s.DeckOverSky, 1e-9);
            // No pixel changed by the body threshold, or pictures of two sizes: no stats.
            Assert.IsFalse(T09Probe.SoftLightOf(off, off, out s));
            Assert.IsFalse(T09Probe.SoftLightOf(raw, new Color[10], out s));
            Assert.IsFalse(T09Probe.SoftLightOf(null, off, out s));
            // A change under the body threshold counts as cloud (0.05) but not as body (0.15).
            var faint = new Color[n];
            for (int i = 0; i < n; i++) faint[i] = new Color(off[i].r + 0.08f, off[i].g, off[i].b, 1f);
            Assert.IsFalse(T09Probe.SoftLightOf(faint, off, out s));
            Assert.AreEqual(1.0, s.CloudShare, 1e-9);
            Assert.AreEqual(0.0, s.BodyShare, 1e-9);
        }

        [TestMethod]
        public void TheSoft60ArgumentsReachItsScenario()
        {
            Run r = StartSoft60();
            Assert.AreEqual("soft60", r.Probe.StartId);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: series K-T2 start soft60 begins")));
            Run other = Start(new[] { "plain" }, null, "1707", "soft59", "K-T2");
            Assert.AreEqual("soft59", other.Probe.StartId);
            SeriesProbe.ForgetRunningProbes();
        }

        // The whole start against the fake world and a map with a patch of each type.
        [TestMethod]
        public void TheSoft60StartReadsTheWeaveFramesTheExtraLooksAndTheTypeViews()
        {
            bool cbHere = false;
            Run r = StartSoft60(x =>
            {
                x.World.LayerReportAt = Soft60Map(() => cbHere);
                x.Probe.MarchSizeReader = own =>
                {
                    int mw, mh;
                    T09Probe.SoftMarchSize(1707, 960, Tuning.Get(OwnCloudOptions.TuneReconResolution, 0.5f), out mw, out mh);
                    return new[] { mw, mh };
                };
            });
            // The map of the clock scene holds a cumulonimbus; the fair scene's does not (the flag turns on when the log says the clock scene began).
            int seen = 0;
            Soft60Drive(r, x =>
            {
                for (; seen < x.Log.Count; seen++)
                    if (x.Log[seen].StartsWith("T09 soft scene clock")) cbHere = true;
            });
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1, r.Quits);
            Assert.AreEqual("Probe: PASSED", r.Log[r.Log.Count - 1]);
            Assert.IsTrue(r.World.RealS < T09Probe.SoftMaxSeconds, "the scenario ended on its own at " + r.World.RealS.ToString("0") + " s");
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("Probe: check") && l.Contains("FAILED")), "no failed check");
            Assert.AreEqual(T09Probe.Soft60EstimateS(T09Probe.Soft60Variants), Number(r, "T09s.plan_estimate_s"), 1e-6);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 soft plan (soft60): ") && l.Contains("ref256 [") && l.Contains("history 0.05")), "the plan line names the variants and their history weights");

            // The weave diagnosis: eight consecutive frames in the fair ground view for six variants, the converged reference among them.
            string pv = "ground_fair";
            foreach (string name in new[] { "soft", "softr33", "softhash", "softacc10", "softacc05", "ref256" })
            {
                foreach (char letter in "abcdefgh")
                    Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + pv + "-" + name + "_" + letter + "-")), name + " frame " + letter + " in the fair ground view");
                Assert.AreEqual(8.0, Number(r, "T09s.fair.ground." + name + ".frames"), name + " frames");
                Assert.AreEqual(1.0, Number(r, "T09s.fair.ground." + name + ".effective_ok"), name + " numbers");
            }
            foreach (string name in new[] { "softcut16", "soft100s24" })
            {
                Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + pv + "-" + name + "_a-")) && !r.World.Saved.Any(f => f.Contains("-" + pv + "-" + name + "_b-")), name + " has one frame");
                Assert.AreEqual(1.0, Number(r, "T09s.fair.ground." + name + ".frames"));
            }
            Assert.IsFalse(r.World.Saved.Any(f => f.Contains("-ground-softhash_") || f.Contains("-ground-ref256_")), "the weave variants are not in the clock scene");
            Assert.IsFalse(r.World.Saved.Any(f => f.Contains("-above_fair-softhash_") || f.Contains("-above_fair-ref256_")), "nor in the fair above view");
            Assert.AreEqual(3.0, Number(r, "T09s.fair.above.soft.frames"));
            Assert.AreEqual(1.0, Number(r, "T09s.fair.above.softr33.frames"));
            Assert.AreEqual(256.0, Number(r, "T09s.fair.ground.ref256.grid_points"));
            Assert.AreEqual(0.05, Number(r, "T09s.fair.ground.softacc05.accumulate"), 1e-6);
            Assert.AreEqual(0.2, Number(r, "T09s.fair.ground.soft.accumulate"), 1e-6, "the history weight is back at its default after the variants that changed it");
            Assert.AreEqual(1.0, Number(r, "T09s.fair.ground.softhash.dither_sequence"));
            Assert.AreEqual(2.0, Number(r, "T09s.fair.ground.softr33.dither_sequence"), "the other variants keep the gradient noise");
            // The cost windows of the lead's list: soft in six views, softr33 in six, softcut16 at both grounds, soft100s24 at the fair ground; none for the weave variants.
            Func<string, int> begins = name => r.Log.Count(l => l.StartsWith("T09 view begin: ") && l.Contains(" " + name + " 1707x960"));
            Assert.AreEqual(2, begins("fw_soft"));
            Assert.AreEqual(4, begins("k_soft"));
            Assert.AreEqual(2, begins("fw_softr33"));
            Assert.AreEqual(4, begins("k_softr33"));
            Assert.AreEqual(1, begins("fw_softcut16"));
            Assert.AreEqual(1, begins("k_softcut16"));
            Assert.AreEqual(1, begins("fw_soft100s24"));
            Assert.AreEqual(0, begins("k_soft100s24"));
            foreach (string none in new[] { "softhash", "softacc10", "softacc05", "ref256" })
                Assert.AreEqual(0, begins("fw_" + none) + begins("k_" + none), none + " has no cost window");
            Assert.AreEqual(6 + 6 + 6 + 2 + 1, (int)Number(r, "T09s.cost_windows"), "six baselines, soft and softr33 in six views, softcut16 at two, soft100s24 at one");

            // The extra looks: a baseline frame and the variant soft's three frames, no cost window.
            foreach (string look in new[] { "horizon_fair", "fog_fair", "rain" })
            {
                Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + look + "-off_a-")), look + " baseline frame");
                foreach (char letter in "abc") Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + look + "-soft_" + letter + "-")), look + " frame " + letter);
                Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 view begin: " + look.Replace("_fair", "") + " ")), look + " has no cost window");
            }
            Assert.AreEqual(1.0, Number(r, "T09s.fair.horizon.placed_ok"));
            Assert.AreEqual(1.0, Number(r, "T09s.fair.fog.placed_ok"));
            Assert.AreEqual(1.0, Number(r, "T09s.clock.rain.placed_ok"));

            // The light rows: every view that has a baseline writes them for the variant soft.
            foreach (string head in new[] { "T09s.fair.ground.soft.", "T09s.fair.above.soft.", "T09s.clock.ground.soft.", "T09s.fair.horizon.soft.", "T09s.clock.rain.soft." })
            {
                Assert.IsTrue(Number(r, head + "light_top") > 0.0 && Number(r, head + "light_base") > 0.0, head + " light");
                Assert.IsTrue(Number(r, head + "light_top_over_base") >= 1.0, head + " the sunlit side is not darker than the base");
            }
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 light T09s.fair.ground.soft.: ")), "the measurement line");

            // The type views: humilis, congestus and cirrus stand in the fair scene's map, the cumulonimbus only in the clock scene's. Each from the ground and from above.
            Assert.AreEqual(8.0, Number(r, "T09s.type_views"));
            Assert.AreEqual(0.0, Number(r, "T09s.types_absent"));
            foreach (string t in new[] { "humilis", "congestus", "cirrus" })
                foreach (string v in new[] { "g", "a" })
                {
                    string view = "ty_" + t + "_" + v;
                    Assert.AreEqual(1.0, Number(r, "T09s.fair." + view + ".placed_ok"), view);
                    foreach (char letter in "abc") Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + view + "_fair-soft_" + letter + "-")), view + " frame " + letter);
                    Assert.AreEqual(1.0, Number(r, "T09s.fair." + view + ".centre_match"), view + " the target's type is under the view's centre");
                    Assert.AreEqual(t == "cirrus" ? 0 : 1, r.World.Saved.Count(f => f.Contains("-" + view + "_fair-off_a-")), view + " baseline frame only for the cumulus bodies");
                }
            foreach (string v in new[] { "g", "a" })
            {
                string view = "ty_cumulonimbus_" + v;
                Assert.AreEqual(1.0, Number(r, "T09s.clock." + view + ".placed_ok"), view);
                Assert.IsTrue(r.World.Saved.Any(f => f.Contains("-" + view + "-soft_a-")), view + " in the clock scene (no _fair)");
                Assert.AreEqual(1.0, Number(r, "T09s.clock." + view + ".centre_match"));
            }
            Assert.IsFalse(r.World.Saved.Any(f => f.Contains("ty_cumulonimbus_g_fair") || f.Contains("ty_humilis_g-")), "a type is shown once, in the scene that has it first");
            Assert.AreEqual(0.0, Number(r, "T09s.types.fair.cumulonimbus.found"));
            Assert.AreEqual(1.0, Number(r, "T09s.types.clock.cumulonimbus.found"));
            Assert.AreEqual(1.0, Number(r, "T09s.types.fair.humilis.found"));
            Assert.AreEqual(32.0, Number(r, "T09s.types.fair.humilis.dist_km"), "the nearest ring that holds humilis: 32 km east");
            Assert.AreEqual(100.0, Number(r, "T09s.types.fair.congestus.dist_km"));
            Assert.AreEqual(245.0, Number(r, "T09s.types.fair.cirrus.dist_km"));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 types (fair): ") && l.Contains("humilis 32 km at ") && l.Contains("cover 0.80 [Cumulus humilis]") && l.Contains("cumulonimbus absent within 300 km")), "the scan line says what stands where and what is absent");
            // The vessel was moved to the cells: the type views were placed at places away from the site, the others at the site.
            var away = r.World.PlacedAt.Where(p => T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, p[0], p[1], Soft60Radius) > 3.0).ToList();
            Assert.AreEqual(8, away.Count, "the eight type views were placed away from the site, the others at it");
            Assert.AreEqual(8, r.World.PlacedAt.Count(p => T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, p[0], p[1], Soft60Radius) > 3.0));
            Assert.IsTrue(away.Count(p => T09Probe.Soft60DistanceKm(Soft60SiteLat, Soft60SiteLon, p[0], p[1], Soft60Radius) > 90.0) >= 3, "the congestus view from the ground and the two cirrus views stand 90 km or more from the site");
            // The maps were read at the rings: 265 places a scene.
            Assert.IsTrue(r.World.LayersReadAt.Count >= 2 * 265, "the scan read " + r.World.LayersReadAt.Count + " places");
            Assert.AreEqual(265.0, Number(r, "T09s.types.fair.cells"));
        }

        [TestMethod]
        public void ATypeThatTheMapDoesNotHoldWithin300KmIsLoggedAndNotAFailure()
        {
            Run r = StartSoft60(x => x.World.LayerReportAt = (la, lo) => Soft60Report("Stratocumulus", 0.9f));
            Soft60Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0.0, Number(r, "T09s.type_views"));
            Assert.AreEqual(4.0, Number(r, "T09s.types_absent"));
            Assert.AreEqual(4, r.Log.Count(l => l.StartsWith("T09 types: ") && l.Contains("was absent within 300 km in both scenes")), "one line per absent type");
            Assert.IsFalse(r.World.Saved.Any(f => f.Contains("-ty_")), "no type picture");
            Assert.AreEqual(0.0, Number(r, "T09s.types.fair.humilis.found"));
            Assert.AreEqual(0.0, Number(r, "T09s.types.clock.humilis.found"));
            // The rest of the start ran: the cost windows and the extra looks.
            Assert.AreEqual(21.0, Number(r, "T09s.cost_windows"));
            Assert.AreEqual(1.0, Number(r, "T09s.clock.rain.placed_ok"));
            AssertNoUnhandled(r);
        }

        [TestMethod]
        public void AWeakCellIsUsedWhereNoCellReachesTheCoverAndSaysSo()
        {
            // The only humilis in the map covers 0.4: below the 0.55 of the target but above its 0.30 floor.
            Run r = StartSoft60(x => x.World.LayerReportAt = (la, lo) =>
            {
                double d = T09Probe.Soft60DistanceKm(la, lo, Soft60SiteLat, Soft60SiteLon, Soft60Radius);
                return Soft60Report(d > 10.0 && d < 14.0 ? "Cumulus humilis" : "Stratocumulus", d > 10.0 && d < 14.0 ? 0.4f : 0.9f);
            });
            Soft60Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1.0, Number(r, "T09s.types.fair.humilis.found"));
            Assert.AreEqual(1.0, Number(r, "T09s.types.fair.humilis.weak"));
            Assert.AreEqual(12.0, Number(r, "T09s.types.fair.humilis.dist_km"));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 types (fair): ") && l.Contains("humilis 12 km") && l.Contains("weak")), "the line names the weak cell");
            Assert.AreEqual(2.0, Number(r, "T09s.type_views"), "humilis from the ground and from above");
        }

        [TestMethod]
        public void ATypeViewThatTheTimeCannotHoldIsLeftOutAndNamed()
        {
            // A table with a cost window of every variant in every view: more than the scenario's time, so that the last views (the type views) are left out and named.
            var saved = T09Probe.Soft60Variants.Select(v => new { v.FairCost, v.ClockCost, v.FairViews, v.ClockViews }).ToArray();
            try
            {
                foreach (T09Probe.SoftVariant v in T09Probe.Soft60Variants)
                {
                    v.FairCost = "ga";
                    v.ClockCost = "gias";
                    v.FairViews = "";
                    v.ClockViews = "";
                }
                Run r = StartSoft60(x => x.World.LayerReportAt = Soft60Map(() => true));
                Soft60Drive(r);
                Assert.IsFalse(r.Probe.Passed, "the plan does not fit the time");
                Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("T09s.section_")), r.Joined);
                Assert.IsTrue(Number(r, "T09s.sections_skipped") >= 1.0);
                AssertNoUnhandled(r);
                Assert.IsTrue(r.World.RealS < T09Probe.SoftMaxSeconds + 30, "the start ended in its time: " + r.World.RealS.ToString("0"));
                Assert.IsTrue(r.Log.Any(l => l.Contains("left out") || r.Probe.Failures.Any(f => f.Contains("left out"))), "the skipped view is named");
            }
            finally
            {
                for (int i = 0; i < T09Probe.Soft60Variants.Length; i++)
                {
                    T09Probe.Soft60Variants[i].FairCost = saved[i].FairCost;
                    T09Probe.Soft60Variants[i].ClockCost = saved[i].ClockCost;
                    T09Probe.Soft60Variants[i].FairViews = saved[i].FairViews;
                    T09Probe.Soft60Variants[i].ClockViews = saved[i].ClockViews;
                }
            }
        }
    }
}
