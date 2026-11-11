using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D35
{
    // Package D35, the sub-grid structure of the sea ice: with a scripted field, the share of ice cells is the field's fraction, the
    // shore freezes first, leads and floes exist, the pattern is deterministic and finite, and no second cold or clock is read.
    [TestClass]
    public class IceMaskTests
    {
        private static IceParams Params(float lead = 0.08f, bool permanent = false, float floeKm = 3f)
        {
            return new IceParams { RadiusM = 600000f, FloeSizeKm = floeKm, LeadFraction = lead, Permanent = permanent, Seed = 4711u };
        }

        // A fixed spread of directions over a band of the sphere (a golden-angle spiral), the same in every run.
        private static void Direction(int i, int n, double latMin, double latMax, out float x, out float y, out float z)
        {
            double t = (i + 0.5) / n;
            double sinMin = Math.Sin(latMin * Math.PI / 180.0), sinMax = Math.Sin(latMax * Math.PI / 180.0);
            double sy = sinMin + (sinMax - sinMin) * t;
            double lon = i * 2.399963229728653;
            double c = Math.Sqrt(Math.Max(0.0, 1.0 - sy * sy));
            x = (float)(c * Math.Sin(lon));
            y = (float)sy;
            z = (float)(c * Math.Cos(lon));
        }

        private static double IceShare(IceParams p, float fraction, int n, Func<int, float> shore, Func<int, float> shoreMean, float ox = 0f)
        {
            int ice = 0;
            for (int i = 0; i < n; i++)
            {
                float x, y, z;
                Direction(i, n, -80, 80, out x, out y, out z);
                if (IceMask.Evaluate(p, x, y, z, fraction, 0.5f, shore(i), shoreMean(i), ox, 0f, 0f).Ice) ice++;
            }
            return ice / (double)n;
        }

        [TestMethod]
        public void TheShareOfIceEqualsTheFieldsFractionWithAndWithoutShoreStructure()
        {
            IceParams p = Params();
            // No shore structure: the shore term equals its mean.
            foreach (float f in new[] { 0.05f, 0.2f, 0.4f, 0.5f, 0.7f, 0.9f, 0.97f })
            {
                double flat = IceShare(p, f, 120000, i => 0.5f, i => 0.5f);
                Assert.AreEqual(f, flat, 0.03, "flat shore at fraction " + f);
                // A shore term that varies between 0 and 1 around the mean 0.5 (cells near the coast against far from it).
                double shored = IceShare(p, f, 120000, i => (i % 8) / 7f, i => 0.5f);
                Assert.AreEqual(f, shored, 0.04, "varying shore at fraction " + f);
            }
        }

        [TestMethod]
        public void TheLeadBandTakesTheShareItShould()
        {
            // The area of {F2 - F1 < w} over the area is the table's share, and WidthFor inverts the table.
            const int N = 150000;
            for (int k = 1; k < IceMask.BandWidths.Length - 1; k++)
            {
                float w = IceMask.BandWidths[k];
                int inside = 0;
                for (int i = 0; i < N; i++)
                {
                    float x = (i * 0.6180339f * 53f) % 40f, y = (i * 0.7548776f * 41f) % 40f, z = (i * 0.5698402f * 37f) % 40f;
                    IceHash.Worley wl = IceHash.WorleyAt(x, y, z, 31u);
                    if (wl.F2 - wl.F1 < w) inside++;
                }
                Assert.AreEqual(IceMask.BandShares[k], inside / (double)N, 0.012, "band share at w " + w);
                Assert.AreEqual(w, IceMask.WidthFor(IceMask.BandShares[k]), 1e-5, "the table inverts at share " + IceMask.BandShares[k]);
            }
            Assert.AreEqual(0f, IceMask.WidthFor(0f));
            Assert.AreEqual(0f, IceMask.WidthFor(float.NaN));
            Assert.AreEqual(0.8f, IceMask.WidthFor(2f));
        }

        [TestMethod]
        public void TheShoreFreezesFirstAndTheMeanStays()
        {
            IceParams p = Params();
            const int N = 160000;
            // Cells with a high shore term (near the coast) and with a low one (offshore), the footprint mean 0.5 for both.
            int nearIce = 0, nearN = 0, farIce = 0, farN = 0;
            for (int i = 0; i < N; i++)
            {
                float x, y, z;
                Direction(i, N, -80, 80, out x, out y, out z);
                bool near = i % 2 == 0;
                float shore = near ? 0.9f : 0.1f;
                IceState s = IceMask.Evaluate(p, x, y, z, 0.4f, 0.5f, shore, 0.5f, 0f, 0f, 0f);
                if (near) { nearN++; if (s.Ice) nearIce++; }
                else { farN++; if (s.Ice) farIce++; }
            }
            double near01 = nearIce / (double)nearN, far01 = farIce / (double)farN;
            Assert.IsTrue(near01 > far01 + 0.2, "the coast freezes first: near " + near01 + " far " + far01);
            Assert.AreEqual(0.4, 0.5 * (near01 + far01), 0.03, "the mean over the footprint stays the field's fraction");
        }

        [TestMethod]
        public void NothingFreezesAtZeroAndEverythingAtOne()
        {
            IceParams p = Params();
            for (int i = 0; i < 20000; i++)
            {
                float x, y, z;
                Direction(i, 20000, -85, 85, out x, out y, out z);
                Assert.IsFalse(IceMask.Evaluate(p, x, y, z, 0f, 1f, 1f, 0f, 0f, 0f, 0f).Ice, "ice at fraction 0");
                IceState full = IceMask.Evaluate(p, x, y, z, 1f, 1f, 0f, 1f, 0f, 0f, 0f);
                Assert.IsTrue(full.Ice && !full.Lead, "open water at fraction 1");
            }
        }

        [TestMethod]
        public void PermanentSeaIsFrozenWithoutAField()
        {
            IceParams p = Params(permanent: true);
            for (int i = 0; i < 5000; i++)
            {
                float x, y, z;
                Direction(i, 5000, -85, 85, out x, out y, out z);
                IceState s = IceMask.Evaluate(p, x, y, z, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                Assert.IsTrue(s.Ice, "the permanent sea has open water");
                Assert.AreEqual(p.PermanentThicknessM, s.ThicknessM, p.PermanentThicknessM * 0.3f + 0.01f, "thickness of a permanent sea");
            }
        }

        [TestMethod]
        public void LeadsAndFloesBothExistAtMidFractionAndLeadsVanishAtOne()
        {
            IceParams p = Params(lead: 0.12f);
            int ice = 0, lead = 0, water = 0;
            for (int i = 0; i < 100000; i++)
            {
                float x, y, z;
                Direction(i, 100000, -80, 80, out x, out y, out z);
                IceState s = IceMask.Evaluate(p, x, y, z, 0.7f, 0.5f, 0.5f, 0.5f, 0f, 0f, 0f);
                if (s.Ice) ice++;
                else if (s.Lead) lead++;
                else water++;
            }
            Assert.IsTrue(ice > 0 && lead > 0 && water > 0, "ice " + ice + " leads " + lead + " water " + water);
            // The band takes leadFraction x (1 - f) = 0.036 of an ice cell, and ice cells are 0.7 / (1 - 0.036) of the sea.
            Assert.AreEqual(0.12 * 0.3 * 0.7 / (1 - 0.036), lead / 100000.0, 0.008, "the share of leads");
        }

        [TestMethod]
        public void TheCoverageChangesContinuouslyOverTheYearWrap()
        {
            IceParams p = Params();
            // A scripted field whose fraction follows a cosine of the season phase: the phase 0.999 and 0.001 are 0.002 of a year apart.
            Func<double, float> field = phase => (float)(0.5 + 0.45 * Math.Cos(2 * Math.PI * phase));
            double before = IceShare(p, field(0.999), 150000, i => 0.5f, i => 0.5f);
            double after = IceShare(p, field(0.001), 150000, i => 0.5f, i => 0.5f);
            Assert.AreEqual(before, after, 0.01, "coverage across the year wrap");
            // And in small steps of the fraction the coverage never jumps.
            double last = IceShare(p, 0.1f, 60000, i => 0.5f, i => 0.5f);
            for (float f = 0.11f; f < 0.95f; f += 0.01f)
            {
                double now = IceShare(p, f, 60000, i => 0.5f, i => 0.5f);
                Assert.IsTrue(Math.Abs(now - last) < 0.03, "a jump of " + (now - last) + " at fraction " + f);
                Assert.IsTrue(now > last - 0.01, "coverage fell while the fraction rose at " + f);
                last = now;
            }
        }

        [TestMethod]
        public void NotANumberAndInfinityNeverGiveIceOrANumberThatIsNotOne()
        {
            IceParams p = Params();
            float nan = float.NaN, inf = float.PositiveInfinity;
            IceState a = IceMask.Evaluate(p, 0f, 1f, 0f, nan, nan, nan, nan, 0f, 0f, 0f);
            Assert.IsFalse(a.Ice);
            IceState b = IceMask.Evaluate(p, 0.6f, 0.8f, 0f, 0.5f, inf, 0.5f, 0.5f, 0f, 0f, 0f);
            Assert.IsFalse(float.IsNaN(b.Opacity) || float.IsNaN(b.ThicknessM) || float.IsNaN(b.LocalFraction));
            IceState c = IceMask.Evaluate(p, 0.6f, 0.8f, 0f, 2f, -3f, -4f, 7f, 0f, 0f, 0f);
            Assert.IsTrue(c.LocalFraction >= 0f && c.LocalFraction <= 1f);
        }

        [TestMethod]
        public void TheSamePointGivesTheSameStateAndTheDriftMovesThePattern()
        {
            IceParams p = Params();
            IceState a = IceMask.Evaluate(p, 0.3f, 0.5f, 0.8124f, 0.5f, 0.4f, 0.5f, 0.5f, 0f, 0f, 0f);
            IceState b = IceMask.Evaluate(p, 0.3f, 0.5f, 0.8124f, 0.5f, 0.4f, 0.5f, 0.5f, 0f, 0f, 0f);
            Assert.AreEqual(a.Ice, b.Ice);
            Assert.AreEqual(a.ThicknessM, b.ThicknessM);
            Assert.AreEqual(a.Opacity, b.Opacity);
            // A drift of a few cells changes the state at many points, and the share stays the field's.
            int differ = 0;
            for (int i = 0; i < 4000; i++)
            {
                float x, y, z;
                Direction(i, 4000, -80, 80, out x, out y, out z);
                bool s0 = IceMask.Evaluate(p, x, y, z, 0.5f, 0.4f, 0.5f, 0.5f, 0f, 0f, 0f).Ice;
                bool s1 = IceMask.Evaluate(p, x, y, z, 0.5f, 0.4f, 0.5f, 0.5f, 3.3f, 0f, 0f).Ice;
                if (s0 != s1) differ++;
            }
            Assert.IsTrue(differ > 400, "the drift changed " + differ + " of 4000 points");
            Assert.AreEqual(0.5, IceShare(p, 0.5f, 100000, i => 0.5f, i => 0.5f, 3.3f), 0.03);
        }

        [TestMethod]
        public void ThickIceIsOpaqueAndThinIceNearlyClear()
        {
            Assert.AreEqual(IceMask.ThinOpacity, IceMask.OpacityOf(0f), 1e-6);
            Assert.AreEqual(IceMask.ThinOpacity, IceMask.OpacityOf(IceMask.ThinM), 1e-6);
            Assert.AreEqual(1f, IceMask.OpacityOf(IceMask.ThickM), 1e-6);
            Assert.AreEqual(1f, IceMask.OpacityOf(3f), 1e-6);
            float last = 0f;
            for (float t = 0f; t < 0.4f; t += 0.01f)
            {
                float o = IceMask.OpacityOf(t);
                Assert.IsTrue(o >= last - 1e-6f, "opacity fell at " + t);
                last = o;
            }
            float r1, g1, b1, r2, g2, b2;
            IceMask.ColourOf(0.02f, 0.85f, 0.92f, 0.97f, 0f, out r1, out g1, out b1);
            IceMask.ColourOf(1.0f, 0.85f, 0.92f, 0.97f, 0f, out r2, out g2, out b2);
            Assert.IsTrue(r1 < r2 && g1 < g2 && b1 < b2, "thin ice is darker than thick ice");
            Assert.AreEqual(0.85f, r2, 0.01f);
            float rs, gs, bs;
            IceMask.ColourOf(1.0f, 0.85f, 0.92f, 0.97f, 1f, out rs, out gs, out bs);
            Assert.IsTrue(rs > r2, "snow on the ice makes it whiter");
        }

        [TestMethod]
        public void TheIceCodeReadsNoSeasonClockNoOldSnowLineAndNoColdCurve()
        {
            string folder = Path.Combine(RepoRoot.Root("src", "Atmospheres", "Ice"), "src", "Atmospheres", "Ice");
            string[] banned = { "Snow" + "Mask", "SeasonClock", "SeasonModel", "SeasonState", "LineDegrees", "IceLag", "freezeLineDeg", "lagYears", "iceWinterLine", "SeasonRules" };
            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (string word in banned)
                    Assert.IsFalse(StripComments(text).Contains(word), Path.GetFileName(file) + " reads " + word);
            }
        }

        // The text of a source file without its // comments (the header may say what the class does not read).
        internal static string StripComments(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int at = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (at >= 0) lines[i] = lines[i].Substring(0, at);
            }
            return string.Join("\n", lines);
        }
    }
}
