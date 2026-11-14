using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 L3: the cover and type maps of a layer have the sizes of a planet of Kerbin's radius and a mix of types that differs by region. The numbers of the measured test are the audit's targets
    // (E40-type-mix-audit.md section 5) with margin, taken from tools/e40_type_mix.py on the same maps; TVC's base maps on the same grid are 74 / 48 km correlation length, fields of 238 km.
    [TestClass]
    public class TypeMixTests
    {
        private const int W = 512, H = 256;

        private static CloudLayerDef Cumulus() { return TypeMixFixture.ShippedLayers()[1]; }
        private static CloudLayerDef Cirrus() { return TypeMixFixture.ShippedLayers()[3]; }

        private static CloudMesoInputs Inputs(byte[] precip, byte[] humidity, byte[] surface, double ut = 3.0e6, double drift = 0.0, ulong seed = 1UL)
        {
            return new CloudMesoInputs { Width = W, Height = H, Precip = precip, Humidity = humidity, Surface = surface, Ut = ut, DriftRadS = drift, Seed = seed, RadiusKm = 600.0 };
        }

        private static byte[] Flat(byte v)
        {
            var b = new byte[W * H];
            for (int i = 0; i < b.Length; i++) b[i] = v;
            return b;
        }

        private static double Mean(byte[] a, int rowFrom = 40, int rowTo = 216)
        {
            double s = 0.0;
            int n = 0;
            for (int j = rowFrom; j < rowTo; j++) for (int i = 0; i < W; i++) { s += a[j * W + i] / 255.0; n++; }
            return s / n;
        }

        // ---- the cover pattern ----

        [TestMethod]
        public void TheCoverPatternKeepsTheMeanOfARegionAndMakesFieldsAndGaps()
        {
            CloudLayerDef cumulus = Cumulus();
            foreach (byte level in new byte[] { 51, 128, 204 })
            {
                byte[] cover = Flat(level), coverOut = null, typeOut = null;
                CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128)), cover, Flat(64), ref coverOut, ref typeOut);
                Assert.AreEqual(level / 255.0, Mean(coverOut), 0.03, "the mean cover of a region of " + level / 255.0);
                double lo = 0, hi = 0;
                for (int j = 40; j < 216; j++) for (int i = 0; i < W; i++) { double v = coverOut[j * W + i] / 255.0; if (v < 0.05) lo++; if (v > 0.95) hi++; }
                double n = 176.0 * W;
                Assert.IsTrue(lo / n > 0.05 || level > 200, "clear gaps appear in a region of cover " + level / 255.0 + ": " + lo / n);
                Assert.IsTrue(hi / n > 0.05 || level < 60, "closed fields appear in a region of cover " + level / 255.0 + ": " + hi / n);
            }
        }

        [TestMethod]
        public void ClearStaysClearAndClosedStaysClosed()
        {
            CloudLayerDef cumulus = Cumulus();
            byte[] coverOut = null, typeOut = null;
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128)), Flat(0), Flat(0), ref coverOut, ref typeOut);
            Assert.AreEqual(0, coverOut.Max(), "no cloud is made from a clear sky");
            coverOut = null;
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128)), Flat(255), Flat(0), ref coverOut, ref typeOut);
            Assert.AreEqual(255, coverOut.Min(), "a closed sky stays closed");
        }

        [TestMethod]
        public void TheStageIsDeterministicAndTheSeedChangesThePattern()
        {
            CloudLayerDef cumulus = Cumulus();
            byte[] a = null, b = null, c = null, t = null;
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128), seed: 5UL), Flat(128), Flat(64), ref a, ref t);
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128), seed: 5UL), Flat(128), Flat(64), ref b, ref t);
            CollectionAssert.AreEqual(a, b, "the same inputs, the same bytes");
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128), seed: 6UL), Flat(128), Flat(64), ref c, ref t);
            Assert.IsFalse(a.SequenceEqual(c), "another seed, another pattern");
        }

        [TestMethod]
        public void ThePatternMovesWithTheLayerWind()
        {
            CloudLayerDef cumulus = Cumulus();
            // a shift of a quarter of the globe: 128 columns of the 512 (the drift is the angle, so the same at every latitude)
            double quarter = Math.PI / 2.0;
            byte[] still = null, moved = null, t = null;
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128), ut: 0.0, drift: 0.0), Flat(128), Flat(64), ref still, ref t);
            CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128), ut: 1000.0, drift: quarter / 1000.0), Flat(128), Flat(64), ref moved, ref t);
            double same = 0.0, shifted = 0.0;
            int n = 0;
            for (int j = 40; j < 216; j++)
                for (int i = 0; i < W; i++)
                {
                    double v = moved[j * W + i];
                    same += Math.Abs(v - still[j * W + i]);
                    shifted += Math.Abs(v - still[j * W + (i + W - 128) % W]);
                    n++;
                }
            Assert.IsTrue(shifted / n < 0.1 * same / n, "the pattern is the same one, east by a quarter of the globe: " + shifted / n + " against " + same / n);
        }

        [TestMethod]
        public void ARainyTexelKeepsItsCoverAndALayerWithoutTheNodeIsUnchanged()
        {
            CloudLayerDef cumulus = Cumulus();
            byte[] cover = Flat(180), precip = Flat(200), coverOut = null, typeOut = null;
            cumulus.Meso.RainKeep = 1f;
            CloudMesoStage.Apply(cumulus, Inputs(precip, Flat(200), Flat(128)), cover, Flat(64), ref coverOut, ref typeOut);
            for (int k = 0; k < coverOut.Length; k++) Assert.IsTrue(coverOut[k] >= 180 - 1, "where it rains the cover is not opened up (rainKeep 1): " + coverOut[k]);
            // the cumulonimbus of the file keeps half of it
            CloudLayerDef cb = TypeMixFixture.ShippedLayers()[2];
            coverOut = null;
            CloudMesoStage.Apply(cb, Inputs(precip, Flat(200), Flat(128)), cover, Flat(64), ref coverOut, ref typeOut);
            for (int k = 0; k < coverOut.Length; k++) Assert.IsTrue(coverOut[k] >= 90 - 1, "the cumulonimbus keeps rainKeep of the cover under rain: " + coverOut[k]);
            // no rain: the cover of the same layer is opened up
            coverOut = null;
            CloudMesoStage.Apply(cb, Inputs(Flat(0), Flat(200), Flat(128)), cover, Flat(64), ref coverOut, ref typeOut);
            Assert.IsTrue(coverOut.Min() < 30, "without rain the pattern has gaps");
            CloudLayerDef rain = TypeMixFixture.ShippedLayers()[0];
            byte[] c2 = null, t2 = null, ci = Flat(100), ti = Flat(77);
            CloudMesoStage.Apply(rain, Inputs(Flat(0), null, null), ci, ti, ref c2, ref t2);
            Assert.AreSame(ci, c2, "the rain deck has no pattern: the cover is the very same array");
            Assert.AreSame(ti, t2, "and no climate: the type is the very same array");
        }

        // ---- the types ----

        private static byte[] TypeMap(CloudLayerDef layer, byte[] humidity, byte[] surface, byte[] precip, byte[] altoMid = null, byte[] altoLow = null)
        {
            byte[] coverOut = null, typeOut = null;
            CloudMesoInputs inp = Inputs(precip, humidity, surface);
            inp.AltoMid = altoMid;
            inp.AltoLow = altoLow;
            CloudMesoStage.Apply(layer, inp, Flat(128), Flat(64), ref coverOut, ref typeOut);
            return typeOut;
        }

        private static double[] Shares(byte[] type, int count, int rowFrom, int rowTo)
        {
            var s = new double[count];
            int n = 0;
            for (int j = rowFrom; j < rowTo; j++)
                for (int i = 0; i < W; i++)
                {
                    s[Math.Max(0, Math.Min(count - 1, (int)Math.Round(type[j * W + i] / 255.0 * (count - 1))))]++;
                    n++;
                }
            for (int k = 0; k < count; k++) s[k] /= n;
            return s;
        }

        [TestMethod]
        public void StratocumulusAndCongestusAreReachableAndSeveralTypesShareARegion()
        {
            CloudLayerDef cumulus = Cumulus();
            int n = cumulus.Types.Count;
            // cool humid air over the sea at high latitude: stratocumulus is among the types; the humid warm belt over land: congestus is.
            double[] sea = Shares(TypeMap(cumulus, Flat(220), Flat(0), Flat(0)), n, 20, 60);
            Assert.IsTrue(sea[0] > 0.05, "stratocumulus over the cool humid sea at high latitude: " + sea[0]);
            double[] tropics = Shares(TypeMap(cumulus, Flat(230), Flat(150), Flat(90)), n, 100, 156);
            Assert.IsTrue(tropics[3] > 0.05, "congestus in the humid wet tropics over land: " + tropics[3]);
            Assert.IsTrue(sea.Count(x => x > 0.05) >= 2 && tropics.Count(x => x > 0.05) >= 2, "a region holds more than one type");
        }

        [TestMethod]
        public void LatitudeAndLandAgainstSeaChangeTheMix()
        {
            CloudLayerDef cumulus = Cumulus();
            int n = cumulus.Types.Count;
            byte[] humid = Flat(160);
            double[] seaMid = Shares(TypeMap(cumulus, humid, Flat(0), Flat(20)), n, 70, 100);
            double[] landMid = Shares(TypeMap(cumulus, humid, Flat(128), Flat(20)), n, 70, 100);
            Assert.IsTrue(seaMid[0] > landMid[0] + 0.10, "more stratocumulus over the sea than over land: " + seaMid[0] + " against " + landMid[0]);
            Assert.IsTrue(landMid[2] + landMid[3] > seaMid[2] + seaMid[3] + 0.10, "more growing cumulus over land: " + (landMid[2] + landMid[3]) + " against " + (seaMid[2] + seaMid[3]));
            double[] tropics = Shares(TypeMap(cumulus, humid, Flat(128), Flat(20)), n, 100, 156);
            double[] polar = Shares(TypeMap(cumulus, humid, Flat(128), Flat(20)), n, 10, 40);
            Assert.IsTrue(tropics[3] > polar[3] + 0.05, "congestus in the tropics, not at high latitude: " + tropics[3] + " against " + polar[3]);
            Assert.IsTrue(polar[0] > tropics[0] + 0.05, "stratocumulus further from the equator: " + polar[0] + " against " + tropics[0]);
        }

        [TestMethod]
        public void TheMidLevelSheetIsAShareCutHardAndNeverBlendedThroughTheClimateTypes()
        {
            CloudLayerDef cumulus = Cumulus();
            int n = cumulus.Types.Count;
            byte[] mid = Flat(200), low = Flat(10);
            double[] sheet = Shares(TypeMap(cumulus, Flat(128), Flat(128), Flat(0), mid, low), n, 20, 236);
            Assert.IsTrue(sheet[n - 1] > 0.85, "where the air aloft is moist and the low air is not, the sheet shows: " + sheet[n - 1]);
            double[] none = Shares(TypeMap(cumulus, Flat(128), Flat(128), Flat(0), low, mid), n, 20, 236);
            Assert.IsTrue(none[n - 1] < 0.01, "no sheet where the low air has more cloud: " + none[n - 1]);
            // the type walk of the other types: between two neighbouring patches the type value passes no third type, except where a type has no weight at all
            byte[] type = TypeMap(cumulus, Flat(128), Flat(128), Flat(0));
            int bad = 0, steps = 0;
            for (int j = 100; j < 156; j++)
                for (int i = 0; i < W - 1; i++)
                {
                    double a = type[j * W + i] / 255.0 * (n - 1), b = type[j * W + i + 1] / 255.0 * (n - 1);
                    steps++;
                    if (Math.Abs(a - b) > 1.01) bad++;
                }
            Assert.IsTrue(bad < steps * 0.002, "neighbouring texels differ by more than one type step in " + bad + " of " + steps + " steps");
        }

        // ---- the measured maps of the generator ----

        [TestMethod, TestCategory("TypeMix")]
        public void TheCloudMapsOfAGameDayHaveTheSizesAndTheMixOfAPlanetOfKerbinsRadius()
        {
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                CloudLayerDef cumulus = layers[1], cirrus = layers[3];
                foreach (int seed in new[] { 1, 2 })
                {
                    GeneratorField field = FieldFixture.Field(seed);
                    int body = FieldFixture.BodyIndex;
                    foreach (double ut in new[] { 3.0e6, 3.0e6 + 0.5 * TypeMixFixture.Day })
                    {
                        TypeMixFixture.Shared shared = TypeMixFixture.Maps(field, body, ut);
                        byte[] offCover, offType, onCover, onType;
                        TypeMixFixture.Layer(field, body, shared, cumulus, false, out offCover, out offType);
                        TypeMixFixture.Layer(field, body, shared, cumulus, true, out onCover, out onType);
                        string tag = " (seed " + seed + ", ut " + ut + ")";
                        TypeMixMetrics.Sizes on = TypeMixMetrics.Of(onCover, 0.5), off = TypeMixMetrics.Of(offCover, 0.5);
                        Assert.AreEqual(Mean(offCover, 0, H), Mean(onCover, 0, H), 0.03, "the mean cover of the layer stays" + tag);
                        // Measured over 3 seeds x 8 times by tools/e40_type_mix.py: corr length 45 / 41 km, 31 % of the variance under 100 km, fields 267 km, largest 26 %; before 330 / 198, 2 %, 1318, 73 %.
                        // TVC's base maps on the same grid: 74 / 48 km, 32 %, 238 km, 14 %. The bounds are the targets of the audit with the spread of single maps.
                        Assert.IsTrue(on.CorrZonalKm < 100 && on.CorrMeridionalKm < 90, "cumulus corr length " + on.CorrZonalKm + " / " + on.CorrMeridionalKm + " km (before " + off.CorrZonalKm + " / " + off.CorrMeridionalKm + ")" + tag);
                        Assert.IsTrue(on.VarianceUnder100 >= 0.2, "variance under 100 km " + on.VarianceUnder100 + " (before " + off.VarianceUnder100 + ")" + tag);
                        Assert.IsTrue(on.FieldDiameterKm < 450, "field diameter " + on.FieldDiameterKm + " km (before " + off.FieldDiameterKm + ")" + tag);
                        Assert.IsTrue(on.LargestFieldShare <= 0.4, "largest field " + on.LargestFieldShare + " of the field area (before " + off.LargestFieldShare + ")" + tag);
                        TypeMixMetrics.Mix mix = TypeMixMetrics.MixOf(onCover, onType, cumulus.Types.Count, 0.2);
                        Assert.IsTrue(mix.TypesIn300kmWindow >= 3.2, "types in a 300 km window " + mix.TypesIn300kmWindow + tag);
                        Assert.IsTrue(mix.DominantShare <= 0.55, "dominant type " + mix.DominantShare + tag);
                        Assert.IsTrue(mix.LargestTypeComponentShare <= 0.15, "largest single-type component " + mix.LargestTypeComponentShare + tag);
                        Assert.IsTrue(mix.TypeCorrZonalKm < 130 && mix.TypeCorrMeridionalKm < 120, "type map corr length " + mix.TypeCorrZonalKm + " / " + mix.TypeCorrMeridionalKm + tag);
                        Assert.IsTrue(mix.ShareOfType[0] > 0.0 && mix.ShareOfType[3] > 0.0, "stratocumulus " + mix.ShareOfType[0] + " and congestus " + mix.ShareOfType[3] + " are reachable" + tag);
                        byte[] cirrusCover, cirrusType;
                        TypeMixFixture.Layer(field, body, shared, cirrus, true, out cirrusCover, out cirrusType);
                        TypeMixMetrics.Mix cmix = TypeMixMetrics.MixOf(cirrusCover, cirrusType, 2, 0.2);
                        Assert.IsTrue(cmix.TypesIn300kmWindow >= 1.5, "cirrus types in a 300 km window " + cmix.TypesIn300kmWindow + tag);
                    }
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
        }
    }
}
