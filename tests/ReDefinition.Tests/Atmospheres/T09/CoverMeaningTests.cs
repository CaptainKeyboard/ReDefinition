using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 COVER-MEANING prototype (design note .local/plans/atmospheres/E40-cover-meaning-design.md): on the CPU model, the proposed mapping (CoverMapping.AmountShare, the noise read once
    // per column) makes the closed share of a layer's columns (tau >= 0.92) equal to the map value c, and the mapping of the shipped code (CoverMapping.Current) does not.
    // EXPECTED RESULTS, written before the first run of this file (the numbers in the note came from the exploration run of the same model; the test bounds are the owner's +-0.05):
    //   - the proposed mapping: for the rain deck, the cumulus layer and the cumulonimbus layer, over two noise volumes (seeds 1 and 2) and 40 sites each, the mean absolute difference
    //     between the closed share and c over c = 0.1, 0.3, 0.5, 0.7, 0.9 is at most 0.05 and no single value is off by more than 0.08; the closed share rises with c;
    //   - the shift: lowering the map by 0.15 lowers the closed share by 0.15 +- 0.06 for each of the three layers at c = 0.5 and 0.7;
    //   - the noise amount does not move the cover: the rain deck at amount 0.3, 0.6 and 1 gives closed shares within 0.04 of each other;
    //   - the shipped mapping misses c by more than 0.15 on average for the rain deck and by more than 0.25 for the cumulonimbus layer (what the note's removal list removes);
    //   - the model's Current mapping is CloudShapeModel.Share (so the table of the note is the product's), and the rank table makes the shape value uniform to 0.02.
    [TestClass]
    public class CoverMeaningTests
    {
        [TestCleanup]
        public void BackToTheDefaultDensity()
        {
            OldDensity.End();
        }

        private static NoiseVolumeSet[] volumes = new NoiseVolumeSet[4];

        private static NoiseVolumeSet Volumes(uint seed)
        {
            return volumes[seed] ?? (volumes[seed] = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), seed));
        }

        private static NoiseLayerParams ParamsOf(CloudLayerDef layer, int index, uint seed, bool columnConstant, double typeValue)
        {
            double bx, by, bz;
            NoiseSeeds.BaseOffset(seed, index, out bx, out by, out bz);
            var prm = new NoiseLayerParams { ScaleM = layer.EffectiveNoiseScaleM, Untile = true, Curl = true, OffsetX = bx, OffsetY = by, OffsetZ = bz, Seed = NoiseSeeds.LayerSeed(seed, index), SphereScale = 1.0, ReferenceRadiusM = 600000.0 };
            if (columnConstant)
            {
                // The shape noise read at one radius per column (the layer's own P03 switch SphereScale, here for the cover lookup).
                double eb, et;
                new LayerTypeTable(layer).InterpolatedHeights(typeValue, out eb, out et);
                prm.SphereScale = 1e-6;
                prm.ReferenceRadiusM = 600000.0 + layer.InnerAltitudeM + 0.5 * (eb + et) * layer.ThicknessM;
            }
            return prm;
        }

        // The mean closed share over the sites for the map value c.
        private static double Closed(CoverMapping m, CloudLayerDef layer, int index, uint seed, double c, double type, int sites, double amountOverride = -1.0, double shift = 0.0)
        {
            var table = new LayerTypeTable(layer);
            NoiseLayerParams prm = ParamsOf(layer, index, seed, m == CoverMapping.AmountShare, type);
            CoverNumbers k = CoverNumbers.Of(layer);
            if (amountOverride >= 0.0) k.Amount = amountOverride;
            k.Shift = shift;
            k.Cdf = CoverCdf.Of(Volumes(seed), prm, 32, 60000, 11);
            double sum = 0.0;
            for (int s = 0; s < sites; s++)
                sum += CoverMeaningModel.SiteClosedShare(m, layer, table, Volumes(seed), prm, k, c, type, s, 14, prm.ScaleM * 6.0, true);
            return sum / sites;
        }

        private static readonly double[] Cs = { 0.1, 0.3, 0.5, 0.7, 0.9 };

        [TestMethod]
        public void TheProposedMappingGivesAClosedShareOfAboutTheMapValue()
        {
            // The model's mapping is built from the layer's own numbers (CoverNumbers.Of reads the Effective* getters), which are the old density's.
            OldDensity.Begin();
            CloudLayerDef[] layers = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus() };
            double[] types = { 0.5, 0.0, 0.229 };
            uint[] seeds = { 1u, 2u, 3u };
            for (int li = 0; li < layers.Length; li++)
            {
                double err = 0.0, max = 0.0, previous = -1.0, worstDraw = 0.0;
                string seen = "";
                foreach (double c in Cs)
                {
                    double mean = 0.0;
                    string draws = "";
                    foreach (uint seed in seeds)
                    {
                        double v = Closed(CoverMapping.AmountShare, layers[li], li, seed, c, types[li], 60);
                        mean += v / seeds.Length;
                        draws += v.ToString("0.00") + " ";
                        worstDraw = Math.Max(worstDraw, Math.Abs(v - c));
                    }
                    seen += c.ToString("0.0") + "->" + mean.ToString("0.000") + " (" + draws.Trim() + ")  ";
                    err += Math.Abs(mean - c);
                    max = Math.Max(max, Math.Abs(mean - c));
                    Assert.IsTrue(mean >= previous - 0.02, layers[li].Name + ": the closed share does not rise with c at " + c + " (" + mean + " after " + previous + ")");
                    previous = mean;
                }
                Console.WriteLine(layers[li].Name + " " + seen);
                string report = Environment.GetEnvironmentVariable("COVER_REPORT");
                if (!string.IsNullOrEmpty(report)) System.IO.File.AppendAllText(report, "LINEARITY " + layers[li].Name + ": mean |err| " + (err / Cs.Length).ToString("0.000") + ", max " + max.ToString("0.000") + ", worst single draw " + worstDraw.ToString("0.000") + "  " + seen + Environment.NewLine);
                Assert.IsTrue(err / Cs.Length <= 0.05, layers[li].Name + ": mean |closed - c| over three noise draws " + err / Cs.Length + " in " + seen);
                Assert.IsTrue(max <= 0.08, layers[li].Name + ": largest |closed - c| " + max + " in " + seen);
                // A single noise draw is a sample of a few dozen cells for the layers with large cells: within 0.15.
                Assert.IsTrue(worstDraw <= 0.15, layers[li].Name + ": a single draw is off by " + worstDraw + " in " + seen);
            }
        }

        [TestMethod]
        public void ALowerMapOpensTheSkyByTheShift()
        {
            CloudLayerDef[] layers = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus() };
            double[] types = { 0.5, 0.0, 0.229 };
            for (int li = 0; li < layers.Length; li++)
                foreach (double c in new[] { 0.5, 0.7 })
                {
                    double fall = Closed(CoverMapping.AmountShare, layers[li], li, 1u, c, types[li], 40) - Closed(CoverMapping.AmountShare, layers[li], li, 1u, c, types[li], 40, -1.0, -0.15);
                    Assert.AreEqual(0.15, fall, 0.06, layers[li].Name + " at c " + c + ": the shift -0.15 lowers the closed share by " + fall);
                }
        }

        [TestMethod]
        public void TheNoiseAmountDoesNotMoveTheCover()
        {
            CloudLayerDef rain = CloudLookFixtures.Rain();
            // The property of the mapping for a column of a given thickness: the fitted opacity of the stratus (B3, density scale 0.22: a column of optical depth 5) is the data, not the subject.
            foreach (CloudTypeDef t in rain.Types) t.DensityScale = 1f;
            double a = Closed(CoverMapping.AmountShare, rain, 0, 1u, 0.5, 0.5, 40, 0.3);
            double b = Closed(CoverMapping.AmountShare, rain, 0, 1u, 0.5, 0.5, 40, 0.6);
            double c = Closed(CoverMapping.AmountShare, rain, 0, 1u, 0.5, 0.5, 40, 1.0);
            Assert.AreEqual(a, b, 0.04, "amount 0.3 against 0.6");
            Assert.AreEqual(b, c, 0.04, "amount 0.6 against 1");
        }

        // The mapping of the shipped code for the layers whose amount is below 1 and whose coverage constant was chosen by hand: a step in the map, not a share (the rain deck is empty at
        // 0.2 and closed from 0.6; the cumulonimbus layer shows nothing below 0.7).
        [TestMethod]
        public void TheShippedMappingDoesNotGiveTheMapValueForTheRainDeckAndTheCumulonimbusLayer()
        {
            CloudLayerDef rain = CloudLookFixtures.Rain(), cb = CloudLookFixtures.Cumulonimbus();
            double errRain = 0.0, errCb = 0.0;
            foreach (double c in Cs)
            {
                errRain += Math.Abs(Closed(CoverMapping.Current, rain, 0, 1u, c, 0.5, 20) - c);
                errCb += Math.Abs(Closed(CoverMapping.Current, cb, 2, 1u, c, 0.229, 20) - c);
            }
            Assert.IsTrue(errRain / Cs.Length > 0.15, "rain deck mean error " + errRain / Cs.Length);
            Assert.IsTrue(errCb / Cs.Length > 0.25, "cumulonimbus mean error " + errCb / Cs.Length);
        }

        // The model's copy of the share (the part this file replaces) is the product's function where it is the shipped mapping, so the numbers above are about the product.
        [TestMethod]
        public void TheModelsCurrentMappingIsTheProductsShare()
        {
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            NoiseLayerParams prm = ParamsOf(l1, 1, 1u, false, 0.0);
            CoverNumbers k = CoverNumbers.Of(l1);
            var rng = new Random(5);
            for (int i = 0; i < 300; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 2.0e5, y = (rng.NextDouble() - 0.5) * 2.0e5, z = 600000.0 + 1700.0 + rng.NextDouble() * 600.0;
                double c = rng.NextDouble(), foot = rng.NextDouble() * 200.0;
                double product = CloudShapeModel.Share(Volumes(1u), ref prm, x, y, z, c, k.Coverage, k.Edge, k.Amount, 0.3, true, foot);
                double model = CoverMeaningModel.Share(CoverMapping.Current, Volumes(1u), ref prm, x, y, z, c, k, 0.3, true, foot);
                Assert.AreEqual(product, model, 1e-9, "sample " + i);
            }
        }

        [TestMethod]
        public void TheRankTableMakesTheShapeValueUniform()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            NoiseLayerParams prm = ParamsOf(cb, 2, 1u, false, 0.0);
            CoverCdf cdf = CoverCdf.Of(Volumes(1u), prm, 32, 100000, 11);
            // Other places than the table's own sample (another random sequence): the share of values whose rank is below p is p.
            var rng = new Random(77);
            double period = NoiseModel.ShapePeriodScales * prm.ScaleM;
            var values = new double[40000];
            for (int i = 0; i < values.Length; i++)
                values[i] = CloudShapeModel.ShapeAt(Volumes(1u), ref prm, rng.NextDouble() * period, rng.NextDouble() * period, rng.NextDouble() * period);
            foreach (double p in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
            {
                int below = 0;
                foreach (double v in values) if (cdf.Rank(v) < p) below++;
                Assert.AreEqual(p, below / (double)values.Length, 0.02, "rank below " + p);
            }
        }
    }
}
