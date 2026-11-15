using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests
{
    // E40 HKEYS: the keys height* of a body's CLOUDS node are the numbers of the height variation (CloudHeightRules -> HeightTuning.TakeNumbers -> HeightPlan / HeightMapBuilder / the
    // layer pass's uniforms). Every key changes the CPU maps (or the CPU twin of the density) the way the number says, a body without keys is the compiled numbers of before, a second
    // body with other numbers is another map.
    [TestClass]
    public class HeightKeysTests
    {
        private const int W = 256, H = 128;
        private const double R = 600000.0;
        private const string Layer = "LAYER{name = A;innerAltitudeM = 500;outerAltitudeM = 1500;TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}}";

        // The rules of a body whose CLOUDS node holds these keys (and one layer), and the warnings its parse gave.
        private static CloudHeightRules Rules(string keys, out IReadOnlyList<string> warnings)
        {
            BodyParseResult r = CloudLayerSchemaTests.Parse(keys + Layer);
            warnings = r.Warnings;
            return r.Climate.Clouds.Height;
        }

        private static CloudHeightRules Rules(string keys)
        {
            IReadOnlyList<string> warnings;
            CloudHeightRules rules = Rules(keys, out warnings);
            Assert.AreEqual(0, warnings.Count, string.Join("; ", warnings));
            return rules;
        }

        // The tuning the module builds for a body: the switches of the player's settings (as given), the numbers of the body.
        private static HeightTuning Tuning(CloudHeightRules rules, bool variation, bool physics, bool top, bool ragged)
        {
            var t = new HeightTuning { BaseVariation = variation, BasePhysics = physics, TopFromUpdraft = top, BaseRagged = ragged };
            t.TakeNumbers(rules);
            return t;
        }

        private static HeightInputs Uniform(float humidity, float temperature)
        {
            var input = new HeightInputs { Width = W, Height = H, Humidity = new float[W * H], TemperatureK = new float[W * H] };
            for (int i = 0; i < W * H; i++)
            {
                input.Humidity[i] = humidity;
                input.TemperatureK[i] = temperature;
            }
            return input;
        }

        // The western half moist, the eastern half dry.
        private static HeightInputs MoistAndDry()
        {
            HeightInputs input = Uniform(0.85f, 290f);
            for (int y = 0; y < H; y++)
                for (int x = W / 2; x < W; x++) input.Humidity[y * W + x] = 0.55f;
            return input;
        }

        private static double[] Channel(HeightMaps m, int channel)
        {
            double range = channel == 0 ? m.RootRangeM : channel == 1 ? m.TopRangeM : m.VariationRangeM;
            var v = new double[m.Width * m.Height];
            for (int i = 0; i < v.Length; i++) v[i] = HeightMaps.Decode(m.Bytes[i * 3 + channel], range);
            return v;
        }

        private static double Std(IList<double> v)
        {
            double mean = v.Average(), s = 0.0;
            foreach (double x in v) s += (x - mean) * (x - mean);
            return Math.Sqrt(s / v.Count);
        }

        // How much a field changes from texel to texel against its own spread: small for a long wavelength.
        private static double Roughness(IList<double> v)
        {
            double step = 0.0;
            int n = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W - 1; x++)
                {
                    step += Math.Abs(v[y * W + x + 1] - v[y * W + x]);
                    n++;
                }
            return step / n / Std(v);
        }

        [TestMethod]
        public void ABodyWithoutTheKeysHasTheNumbersOfBeforeAndTheSameMapsAndNoWarning()
        {
            IReadOnlyList<string> warnings;
            CloudHeightRules none = Rules("", out warnings);
            Assert.AreEqual(0, warnings.Count, string.Join("; ", warnings));
            // The literals Kerbin's clouds always had.
            Assert.AreEqual(125f, none.LclMPerK);
            Assert.AreEqual(0f, none.ReferenceSpreadK);
            Assert.AreEqual(500f, none.PhysicsMaxM);
            Assert.AreEqual(110f, none.VariationM);
            Assert.AreEqual(12000f, none.WavelengthM);
            Assert.AreEqual(-220f, none.SizeTrendM);
            Assert.AreEqual(40000f, none.SizeRadiusM);
            Assert.AreEqual(110f, none.RaggedM);
            Assert.AreEqual(220f, none.RaggedScaleM);
            Assert.AreEqual(120f, none.MinThicknessM);
            Assert.AreSame(CloudHeightRules.Default, CloudRules.Default.Height);
            // The compiled tuning and the body's tuning are the same numbers, so they build the same bytes.
            var compiled = new HeightTuning();
            HeightTuning body = Tuning(none, true, true, true, true);
            Assert.AreEqual(compiled.LclMPerK, body.LclMPerK);
            Assert.AreEqual(compiled.ReferenceSpreadK, body.ReferenceSpreadK);
            Assert.AreEqual(compiled.PhysicsMaxM, body.PhysicsMaxM);
            Assert.AreEqual(compiled.VariationAmplitudeM, body.VariationAmplitudeM);
            Assert.AreEqual(compiled.VariationWavelengthM, body.VariationWavelengthM);
            Assert.AreEqual(compiled.SizeTrendM, body.SizeTrendM);
            Assert.AreEqual(compiled.SizeRadiusM, body.SizeRadiusM);
            Assert.AreEqual(compiled.RaggedM, body.RaggedM);
            Assert.AreEqual(compiled.RaggedScaleM, body.RaggedScaleM);
            Assert.AreEqual(compiled.MinThicknessM, body.MinThicknessM);
            HeightInputs input = MoistAndDry();
            input.Coverage = new float[W * H];
            for (int i = 0; i < W * H; i++) input.Coverage[i] = (i % W) < W / 3 ? 1f : 0f;
            CollectionAssert.AreEqual(HeightMapBuilder.Build(input, compiled, R, 9u).Bytes, HeightMapBuilder.Build(input, body, R, 9u).Bytes);
        }

        [TestMethod]
        public void TheKeysReadFromTheCloudsNodeAndArePassedToTheBodysTuning()
        {
            CloudHeightRules r = Rules("heightLclMPerK = 100;heightReferenceSpreadK = 3;heightPhysicsMaxM = 300;heightVariationM = 60;heightWavelengthM = 20000;heightSizeTrendM = -80;heightSizeRadiusM = 90000;heightRaggedM = 40;heightRaggedScaleM = 500;heightMinThicknessM = 300;");
            Assert.AreEqual(100f, r.LclMPerK);
            Assert.AreEqual(3f, r.ReferenceSpreadK);
            Assert.AreEqual(300f, r.PhysicsMaxM);
            Assert.AreEqual(60f, r.VariationM);
            Assert.AreEqual(20000f, r.WavelengthM);
            Assert.AreEqual(-80f, r.SizeTrendM);
            Assert.AreEqual(90000f, r.SizeRadiusM);
            Assert.AreEqual(40f, r.RaggedM);
            Assert.AreEqual(500f, r.RaggedScaleM);
            Assert.AreEqual(300f, r.MinThicknessM);
            HeightTuning t = Tuning(r, true, true, true, true);
            Assert.AreEqual(100f, t.LclMPerK);
            Assert.AreEqual(3f, t.ReferenceSpreadK);
            Assert.AreEqual(300f, t.PhysicsMaxM);
            Assert.AreEqual(60f, t.VariationAmplitudeM);
            Assert.AreEqual(20000f, t.VariationWavelengthM);
            Assert.AreEqual(-80f, t.SizeTrendM);
            Assert.AreEqual(90000f, t.SizeRadiusM);
            Assert.AreEqual(40f, t.RaggedM);
            Assert.AreEqual(500f, t.RaggedScaleM);
            Assert.AreEqual(300f, t.MinThicknessM);
            // The switches are not touched by the numbers.
            Assert.IsFalse(Tuning(r, false, false, false, false).Any);
        }

        [TestMethod]
        public void AKeyThatIsNotANumberOrOutOfRangeIsHeldAndWarnedOnceNamingNodeAndKey()
        {
            IReadOnlyList<string> warnings;
            CloudHeightRules r = Rules("heightLclMPerK = abc;heightVariationM = -40;heightWavelengthM = 5;heightPhysicsMaxM = 90000;heightSizeTrendM = 1e9;heightRaggedM = 1e6;heightRaggedScaleM = 0;heightMinThicknessM = 0;", out warnings);
            Assert.AreEqual(CloudHeightRules.DefaultLclMPerK, r.LclMPerK, "not a number: the default");
            Assert.AreEqual(0f, r.VariationM, "below the range: its floor");
            Assert.AreEqual(100f, r.WavelengthM);
            Assert.AreEqual(5000f, r.PhysicsMaxM, "above the range: its ceiling");
            Assert.AreEqual(5000f, r.SizeTrendM);
            Assert.AreEqual(2000f, r.RaggedM);
            Assert.AreEqual(1f, r.RaggedScaleM);
            Assert.AreEqual(1f, r.MinThicknessM);
            foreach (string key in new[] { "heightLclMPerK", "heightVariationM", "heightWavelengthM", "heightPhysicsMaxM", "heightSizeTrendM", "heightRaggedM", "heightRaggedScaleM", "heightMinThicknessM" })
                Assert.AreEqual(1, warnings.Count(w => w.Contains("CLOUDS." + key + ":")), key + " in " + string.Join("; ", warnings));
            Assert.AreEqual(8, warnings.Count, string.Join("; ", warnings));
            // A misspelt key is the reader's unknown-key warning, with the key it was meant to be.
            BodyParseResult typo = CloudLayerSchemaTests.Parse("heightVariationm = 3;" + Layer);
            Assert.IsTrue(typo.Warnings.Any(w => w.Contains("heightVariationm") && w.Contains("heightVariationM")), string.Join("; ", typo.Warnings));
        }

        [TestMethod]
        public void TheLclKeyScalesTheBaseDifferenceBetweenMoistAndDryAir()
        {
            double Offset(string keys)
            {
                HeightTuning t = Tuning(Rules("heightPhysicsMaxM = 5000;" + keys), false, true, false, false);
                double[] root = Channel(HeightMapBuilder.Build(MoistAndDry(), t, R, 1u), 0);
                return root[(H / 2) * W + 3 * W / 4] - root[(H / 2) * W + W / 4];
            }
            double standard = Offset(""), doubled = Offset("heightLclMPerK = 250;"), none = Offset("heightLclMPerK = 0;");
            Assert.IsTrue(standard > 100.0, "dry over moist " + standard);
            Assert.AreEqual(2.0 * standard, doubled, 0.1 * standard);
            Assert.AreEqual(0.0, none, 2.0);
        }

        [TestMethod]
        public void TheReferenceSpreadKeyMovesTheMeanBaseAndZeroIsTheBodysMean()
        {
            double Mean(string keys)
            {
                HeightTuning t = Tuning(Rules("heightPhysicsMaxM = 1000;" + keys), false, true, false, false);
                return Channel(HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 1u), 0).Average();
            }
            // The map holds a byte over the range of 1110 m: a step of 8.7 m.
            double spread = HeightModel.DewPointSpreadK(288.0, 0.7);
            Assert.AreEqual(0.0, Mean("heightReferenceSpreadK = 0;"), 6.0, "the body's own mean: the table's base is the average");
            Assert.AreEqual(125.0 * (spread - 2.0), Mean("heightReferenceSpreadK = 2;"), 6.0);
            Assert.AreEqual(125.0 * (spread - 8.0), Mean("heightReferenceSpreadK = 8;"), 6.0);
        }

        [TestMethod]
        public void ThePhysicsMaxKeyBoundsTheOffsetAndTheRangeOfTheMap()
        {
            HeightTuning loose = Tuning(Rules("heightPhysicsMaxM = 2000;heightSizeTrendM = 0;"), false, true, false, false);
            HeightTuning tight = Tuning(Rules("heightPhysicsMaxM = 100;heightSizeTrendM = 0;"), false, true, false, false);
            HeightMaps a = HeightMapBuilder.Build(MoistAndDry(), loose, R, 1u), b = HeightMapBuilder.Build(MoistAndDry(), tight, R, 1u);
            Assert.AreEqual(2000f, a.RootRangeM, 1e-3f);
            Assert.AreEqual(100f, b.RootRangeM, 1e-3f);
            double far = Channel(a, 0).Max(v => Math.Abs(v)), near = Channel(b, 0).Max(v => Math.Abs(v));
            Assert.IsTrue(far > 150.0, "unbounded here: " + far);
            Assert.AreEqual(100.0, near, 100.0 / 127.0 + 1e-6, "held at the key's metres");
            Assert.AreEqual(100.0, HeightModel.PhysicsOffsetM(tight, 310.0, 0.05, 4.0), 1e-9);
        }

        [TestMethod]
        public void TheVariationKeyIsTheSpreadOfTheBaseAndTheWavelengthKeyItsSmoothness()
        {
            double Spread(string keys)
            {
                HeightTuning t = Tuning(Rules(keys), true, false, false, false);
                return Std(Channel(HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 5u), 2));
            }
            Assert.AreEqual(110.0, Spread(""), 12.0, "Kerbin's spread");
            Assert.AreEqual(40.0, Spread("heightVariationM = 40;"), 6.0);
            Assert.AreEqual(300.0, Spread("heightVariationM = 300;"), 30.0);
            Assert.AreEqual(0.0, Spread("heightVariationM = 0;"), 1e-9);
            double Rough(string keys)
            {
                HeightTuning t = Tuning(Rules(keys), true, false, false, false);
                return Roughness(Channel(HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 5u), 2));
            }
            double shortWave = Rough("heightWavelengthM = 100000;"), longWave = Rough("heightWavelengthM = 1000000;");
            Assert.IsTrue(shortWave > 2.0 * longWave, "100 km " + shortWave + ", 1000 km " + longWave);
            CollectionAssert.AreNotEqual(HeightMapBuilder.Build(Uniform(0.7f, 288f), Tuning(Rules("heightWavelengthM = 20000;"), true, false, false, false), R, 5u).Bytes,
                                         HeightMapBuilder.Build(Uniform(0.7f, 288f), Tuning(Rules(""), true, false, false, false), R, 5u).Bytes);
        }

        [TestMethod]
        public void TheSizeKeysSetTheSlopeOfTheBaseAgainstTheCoverAndTheDistanceItIsFeltAt()
        {
            var input = new HeightInputs { Width = W, Height = H, Coverage = new float[W * H] };
            // A covered band west of the date line's far side and a clear sky east of it.
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) input.Coverage[y * W + x] = x >= W / 4 && x < W / 2 ? 1f : 0f;
            double Root(string keys, int x)
            {
                HeightTuning t = Tuning(Rules("heightVariationM = 0;" + keys), true, false, false, false);
                return Channel(HeightMapBuilder.Build(input, t, R, 2u), 0)[(H / 2) * W + x];
            }
            double Slope(string keys)
            {
                return Root(keys, 3 * W / 8) - Root(keys, 3 * W / 4);
            }
            Assert.AreEqual(-220.0, Slope(""), 0.3 * 220.0, "the default trend");
            Assert.AreEqual(-60.0, Slope("heightSizeTrendM = -60;"), 0.3 * 60.0);
            Assert.AreEqual(150.0, Slope("heightSizeTrendM = 150;"), 0.3 * 150.0, "positive: larger clouds sit higher");
            Assert.AreEqual(0.0, Slope("heightSizeTrendM = 0;"), 1e-9);
            // 10 texels (about 150 km) east of the band: a radius of 40 km does not feel it, one of 400 km does.
            int x0 = W / 2 + 10;
            Assert.IsTrue(Root("heightSizeRadiusM = 400000;", x0) < Root("heightSizeRadiusM = 40000;", x0) - 20.0,
                          "wide " + Root("heightSizeRadiusM = 400000;", x0) + " narrow " + Root("heightSizeRadiusM = 40000;", x0));
        }

        // A layer of 1500 m to 3000 m with one type from its inner altitude to its outer one at an erosion of 0.5 (the reference of heightRaggedM).
        private static CloudLayerDef Deck()
        {
            var type = new CloudTypeDef("t", 0f, 1f, new Curve(new[] { 0f, 1f }, new[] { 1f, 1f }), 0.5f);
            return new CloudLayerDef("l", 1, 1500f, 3000f, new List<CloudTypeDef> { type }, 0.01f) { Coverage = 0.5f, EdgeSoftness = 1e-4f };
        }

        [TestMethod]
        public void TheRaggedKeyMovesTheBaseByTheKeysMetresAndMinThicknessIsTheThinnestCloud()
        {
            CloudLayerDef layer = Deck();
            var table = new LayerTypeTable(layer);
            double Density(string keys, double altitude, LocalHeights heights, double ragged)
            {
                HeightTuning t = Tuning(Rules(keys), false, false, false, true);
                return HeightDensity.Extinction(layer, table, t, altitude, 1.0, 0.0, heights, ragged);
            }
            var flat = new LocalHeights();
            // At the boundary's unit value 1 and the erosion 0.5 the base moves up by heightRaggedM.
            Assert.AreEqual(0.0, Density("heightRaggedM = 300;", 1700.0, flat, 1.0), "the base is at 1800 m");
            Assert.IsTrue(Density("heightRaggedM = 300;", 1900.0, flat, 1.0) > 0.0);
            Assert.IsTrue(Density("heightRaggedM = 100;", 1700.0, flat, 1.0) > 0.0, "the base is at 1600 m");
            Assert.AreEqual(0.0, Density("heightRaggedM = 100;", 1550.0, flat, 1.0), "the base is at 1600 m");
            Assert.IsTrue(Density("heightRaggedM = 0;", 1510.0, flat, 1.0) > 0.0, "no displacement");
            // The top falls 2000 m (more than the layer holds): what stays is the thinnest cloud above the base.
            var lowered = new LocalHeights { TopM = -2000f };
            Assert.IsTrue(Density("heightMinThicknessM = 600;", 1500.0 + 550.0, lowered, 0.0) > 0.0);
            Assert.AreEqual(0.0, Density("heightMinThicknessM = 600;", 1500.0 + 650.0, lowered, 0.0));
            Assert.IsTrue(Density("heightMinThicknessM = 100;", 1500.0 + 80.0, lowered, 0.0) > 0.0);
            Assert.AreEqual(0.0, Density("heightMinThicknessM = 100;", 1500.0 + 150.0, lowered, 0.0));
        }

        [TestMethod]
        public void TheRaggedScaleKeyIsTheSizeOfTheLumpsInThePlanAndInTheCpuTwinOfTheShader()
        {
            HeightTuning small = Tuning(Rules("heightRaggedScaleM = 100;"), false, false, false, true), large = Tuning(Rules("heightRaggedScaleM = 5000;"), false, false, false, true);
            Assert.AreEqual(100f, small.RaggedScaleM);
            Assert.AreEqual(5000f, large.RaggedScaleM);
            // The lattice is the position over the scale: along a line of 3 km the small scale crosses many lumps, the large one a part of one.
            double Wiggle(HeightTuning t)
            {
                double total = 0.0, previous = HeightModel.RaggedAt(R + 1500.0, 0.0, 0.0, R + 1500.0, t.RaggedScaleM, 5u);
                for (int i = 1; i <= 300; i++)
                {
                    double v = HeightModel.RaggedAt(R + 1500.0, i * 10.0, 0.0, R + 1500.0, t.RaggedScaleM, 5u);
                    total += Math.Abs(v - previous);
                    previous = v;
                }
                return total;
            }
            Assert.IsTrue(Wiggle(small) > 3.0 * Wiggle(large), "100 m " + Wiggle(small) + ", 5 km " + Wiggle(large));
            var layer = new CloudLayerDef("l", 1, 1000f, 3000f, new List<CloudTypeDef> { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f);
            var source = new LayerStackSource(1, R, true);
            source.Add(layer, null, null);
            Assert.AreEqual(5000f, HeightPlan.Make(source, large, 0.0, 3u, W, H).Tunings[0].RaggedScaleM, "the plan's copy carries the body's scale on to the layer pass");
        }

        [TestMethod]
        public void ASecondBodyWithOtherNumbersHasOtherMapsThroughThePlanOfTheStack()
        {
            var layer = new CloudLayerDef("l", 1, 1000f, 3000f, new List<CloudTypeDef> { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f, LayerKind.Weather);
            var cheap = new CloudLayerDef("c", 1, 8000f, 9000f, new List<CloudTypeDef> { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f, LayerKind.Cheap);
            var source = new LayerStackSource(1, R, true);
            source.Add(layer, null, null);
            source.Add(cheap, null, null);
            HeightTuning kerbin = Tuning(Rules(""), true, true, true, true), other = Tuning(Rules("heightVariationM = 50;heightLclMPerK = 60;heightWavelengthM = 30000;"), true, true, true, true);
            HeightPlan a = HeightPlan.Make(source, kerbin, 0.0, 7u, W, H), b = HeightPlan.Make(source, other, 0.0, 7u, W, H);
            Assert.AreEqual(110f, a.Tunings[0].VariationAmplitudeM);
            Assert.AreEqual(50f, b.Tunings[0].VariationAmplitudeM);
            Assert.AreEqual(100f, b.Tunings[1].VariationAmplitudeM, "a cheap layer varies twice as much as the body says");
            Assert.AreEqual(60f, b.Tunings[0].LclMPerK);
            Assert.AreEqual(30000f, b.Tunings[0].VariationWavelengthM);
            HeightInputs input = MoistAndDry();
            CollectionAssert.AreNotEqual(HeightMapBuilder.Build(input, a.Tunings[0], R, a.Seeds[0]).Bytes, HeightMapBuilder.Build(input, b.Tunings[0], R, b.Seeds[0]).Bytes);
        }
    }
}
