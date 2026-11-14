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
            return TypeMixFixture.HomeScales(new CloudMesoInputs { Width = W, Height = H, Precip = precip, Humidity = humidity, Surface = surface, Ut = ut, DriftRadS = drift, Seed = seed });
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

        // The edge of a field is a density of small cells (E40 L5 maps): the step's shift makes the mean over the scatter noise the ramp's value, and a region keeps its cover.
        [TestMethod]
        public void TheScatterStepHasTheRampsValueAsItsMean()
        {
            foreach (float w in new[] { 0.1f, 0.3f, 0.6f })
                for (float r = 0f; r <= 1.0001f; r += 0.05f)
                {
                    float p = CloudMesoStage.ThinShift(r, w);
                    double mean = 0.0;
                    const int n = 20000;
                    for (int k = 0; k < n; k++)
                    {
                        float u = (k + 0.5f) / n;
                        float s = (p - u) / w + 0.5f;
                        mean += s <= 0f ? 0f : s >= 1f ? 1f : s;
                    }
                    Assert.AreEqual(Math.Min(1f, r), mean / n, 0.002, "mean of the cell step at ramp value " + r + " and softness " + w);
                }
        }

        [TestMethod]
        public void TheEdgeOfAFieldThinsOutIntoCellsAndTheRegionKeepsItsCover()
        {
            CloudLayerDef cumulus = Cumulus();
            Assert.IsTrue(cumulus.Meso.Scatter > 0f, "the shipped cumulus has a scattered edge");
            float shipped = cumulus.Meso.Scatter;
            try
            {
                foreach (byte level in new byte[] { 51, 102, 153 })
                {
                    byte[] ramp = null, cells = null, t = null;
                    cumulus.Meso.Scatter = 0f;
                    CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128)), Flat(level), Flat(64), ref ramp, ref t);
                    cumulus.Meso.Scatter = 1f;
                    CloudMesoStage.Apply(cumulus, Inputs(Flat(0), Flat(128), Flat(128)), Flat(level), Flat(64), ref cells, ref t);
                    Assert.AreEqual(Mean(ramp), Mean(cells), 0.01, "the mean cover of a region of " + level / 255.0 + " with the scattered edge and with the ramp");
                    double midRamp = 0, midCells = 0;
                    for (int j = 40; j < 216; j++) for (int i = 0; i < W; i++) { double a = ramp[j * W + i] / 255.0, b = cells[j * W + i] / 255.0; if (a > 0.1 && a < 0.9) midRamp++; if (b > 0.1 && b < 0.9) midCells++; }
                    Assert.IsTrue(midCells < 0.5 * midRamp, "the thin edge is cells and gaps, not a half-covered ramp: " + midCells + " texels of in-between cover against " + midRamp);
                }
            }
            finally
            {
                cumulus.Meso.Scatter = shipped;
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

        // The cloud maps of a game day on the home planet (radius 600 km, troposphere 10.5 km) and on a second body of another size and atmosphere (radius 500 km, troposphere 7.5 km, a day twice
        // as long): the sizes of the features of each layer and the mix of its types are bounded in the units of the body, the troposphere's depth d (and, for the rain deck, the length of the
        // body's own weather systems), so one set of bounds holds for both. Measured over 2 seeds x 2 times (d = 10.5 / 7.5 km):
        //   layer          corr length zonal / meridional (d)   variance under 9.5 d   field (d)   largest field   types in 28.6 d   dominant   largest type component
        //   cumulus home   3.9-5.1 / 3.3-4.5                    0.28-0.32              16-36       0.19-0.29       4.0-4.1           0.39-0.40  0.03-0.05
        //   cumulus second 2.5-3.2 / 2.2-2.8                    0.37-0.40              (cover .54: one sheet)                         4.0        0.39-0.41  0.06-0.09
        //   cb home        12-20 / 9-11                         0.18-0.20              16-48       0.26-0.31       1.7-1.8           0.73-0.80  0.27-0.33
        //   cb second      9-13 / 7-10                          0.22-0.26              (cover .45)                                    1.8        0.64-0.75  0.54-0.74
        //   cirrus home    8-10 / 4-6                           0.06-0.07              26-37       0.25-0.30       1.8-1.9           0.67-0.73  0.09-0.20
        //   cirrus second  6.5-7.9 / 3.0-4.1                    0.10-0.11              (cover .52)                                    1.9-2.0    0.51-0.56  0.12-0.19
        //   rain deck home 14-17 / 11-16                       (the zonal belt: 0.75-0.80 of the variance over 28.6 d; one connected sheet of 160-170 d)
        //   rain deck second 12.5-15.6 / 9.6-13.1
        // With the scattered edge of the fields (E40 L5 maps: the edge is a density of small cells, so two fields joined by a thin fringe of cells are one connected component of the map) the
        // same four maps of the home planet give field diameters of cumulus 17-44 d, cb 16-50 d and cirrus 25-68 d (before: 17-36, 16-48, 26-37), the mean cover and the corr lengths as before
        // (cumulus 3.1 / 2.9 d). Cumulus and cirrus fields are bounded by 55 d and 75 d for that (before the fix the cumulus fields were 125 d).
        // The bounds are these with the spread of single maps. Field diameters and the largest field are bounded only where the layer's mean cover stays under 0.4: a sky of more cover is one
        // connected sheet whatever the pattern.
        private static void CheckTheMapsOfABody(Func<int, GeneratorField> makeField, CloudLayerDef[] layers, double radiusKm, double depthKm, double dayS, string name)
        {
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                CloudLayerDef deck = layers[0], cumulus = layers[1], cb = layers[2], cirrus = layers[3];
                foreach (int seed in new[] { 1, 2 })
                {
                    GeneratorField field = makeField(seed);
                    int body = FieldFixture.BodyIndex;
                    // the shortest wavelength of this body's weather systems: 3 r 0.7, r the system radius (BodyDerived), in km
                    double lambdaMinKm = field.SystemWavelengthMinKm(body);
                    foreach (double ut in new[] { 3.0e6, 3.0e6 + 0.5 * dayS })
                    {
                        TypeMixFixture.Shared shared = TypeMixFixture.Maps(field, body, ut);
                        string tag = " (" + name + ", seed " + seed + ", ut " + ut + ")";
                        using (TypeMixMetrics.Use(radiusKm, depthKm))
                        {
                            byte[] offCover, offType, onCover, onType;
                            TypeMixFixture.Layer(field, body, shared, cumulus, false, out offCover, out offType);
                            TypeMixFixture.Layer(field, body, shared, cumulus, true, out onCover, out onType);
                            TypeMixMetrics.Sizes on = TypeMixMetrics.Of(onCover, 0.5), off = TypeMixMetrics.Of(offCover, 0.5);
                            Assert.AreEqual(Mean(offCover, 0, H), Mean(onCover, 0, H), 0.03, "the mean cover of the layer stays" + tag);
                            // Measured over 3 seeds x 8 times by tools/e40_type_mix.py on the home planet: corr length 45 / 41 km, 31 % of the variance under 100 km, fields 267 km, largest 26 %;
                            // before 330 / 198, 2 %, 1318, 73 %. TVC's base maps on the same grid: 74 / 48 km, 32 %, 238 km, 14 %. 100 km is 9.5 d, 450 km 43 d.
                            Assert.IsTrue(on.CorrZonalKm < 9.5 * depthKm && on.CorrMeridionalKm < 8.6 * depthKm, "cumulus corr length " + on.CorrZonalKm + " / " + on.CorrMeridionalKm + " km (before " + off.CorrZonalKm + " / " + off.CorrMeridionalKm + ")" + tag);
                            Assert.IsTrue(on.VarianceUnder100 >= 0.2, "variance under 9.5 d " + on.VarianceUnder100 + " (before " + off.VarianceUnder100 + ")" + tag);
                            Assert.IsTrue(on.CorrZonalKm < 0.8 * off.CorrZonalKm, "the stage makes the cumulus pattern finer: " + on.CorrZonalKm + " against " + off.CorrZonalKm + " km" + tag);
                            if (Mean(onCover, 0, H) < 0.4)
                            {
                                Assert.IsTrue(on.FieldDiameterKm < 55.0 * depthKm, "field diameter " + on.FieldDiameterKm + " km (before " + off.FieldDiameterKm + ")" + tag);
                                Assert.IsTrue(on.LargestFieldShare <= 0.4, "largest field " + on.LargestFieldShare + " of the field area (before " + off.LargestFieldShare + ")" + tag);
                            }
                            TypeMixMetrics.Mix mix = TypeMixMetrics.MixOf(onCover, onType, cumulus.Types.Count, 0.2);
                            Assert.IsTrue(mix.TypesIn300kmWindow >= 3.2, "types in a window of 28.6 d " + mix.TypesIn300kmWindow + tag);
                            Assert.IsTrue(mix.DominantShare <= 0.55, "dominant type " + mix.DominantShare + tag);
                            Assert.IsTrue(mix.LargestTypeComponentShare <= 0.15, "largest single-type component " + mix.LargestTypeComponentShare + tag);
                            Assert.IsTrue(mix.TypeCorrZonalKm < 12.4 * depthKm && mix.TypeCorrMeridionalKm < 11.4 * depthKm, "type map corr length " + mix.TypeCorrZonalKm + " / " + mix.TypeCorrMeridionalKm + tag);
                            Assert.IsTrue(mix.ShareOfType[0] > 0.0 && mix.ShareOfType[3] > 0.0, "stratocumulus " + mix.ShareOfType[0] + " and congestus " + mix.ShareOfType[3] + " are reachable" + tag);

                            // cumulonimbus: storm cells and the systems that carry them
                            byte[] cbCover, cbType;
                            TypeMixFixture.Layer(field, body, shared, cb, true, out cbCover, out cbType);
                            TypeMixMetrics.Sizes cbs = TypeMixMetrics.Of(cbCover, 0.5);
                            Assert.IsTrue(cbs.CorrZonalKm < 26.0 * depthKm && cbs.CorrMeridionalKm < 20.0 * depthKm, "cumulonimbus corr length " + cbs.CorrZonalKm + " / " + cbs.CorrMeridionalKm + " km" + tag);
                            Assert.IsTrue(cbs.VarianceUnder100 >= 0.12, "cumulonimbus variance under 9.5 d " + cbs.VarianceUnder100 + tag);
                            if (Mean(cbCover, 0, H) < 0.3)
                            {
                                Assert.IsTrue(cbs.FieldDiameterKm < 60.0 * depthKm, "cumulonimbus field diameter " + cbs.FieldDiameterKm + " km" + tag);
                                Assert.IsTrue(cbs.LargestFieldShare <= 0.4, "cumulonimbus largest field " + cbs.LargestFieldShare + tag);
                            }
                            Assert.IsTrue(TypeMixMetrics.MixOf(cbCover, cbType, cb.Types.Count, 0.2).TypesIn300kmWindow >= 1.5, "cumulonimbus types in a window of 28.6 d" + tag);

                            // cirrus: fibres and veils
                            byte[] cirrusCover, cirrusType;
                            TypeMixFixture.Layer(field, body, shared, cirrus, true, out cirrusCover, out cirrusType);
                            TypeMixMetrics.Sizes cs = TypeMixMetrics.Of(cirrusCover, 0.5);
                            Assert.IsTrue(cs.CorrZonalKm < 13.0 * depthKm && cs.CorrMeridionalKm < 9.0 * depthKm, "cirrus corr length " + cs.CorrZonalKm + " / " + cs.CorrMeridionalKm + " km" + tag);
                            Assert.IsTrue(cs.CorrZonalKm > cs.CorrMeridionalKm, "cirrus fibres are longer along the parallel than across it" + tag);
                            if (Mean(cirrusCover, 0, H) < 0.4)
                            {
                                Assert.IsTrue(cs.FieldDiameterKm < 75.0 * depthKm, "cirrus field diameter " + cs.FieldDiameterKm + " km" + tag);
                                Assert.IsTrue(cs.LargestFieldShare <= 0.4, "cirrus largest field " + cs.LargestFieldShare + tag);
                            }
                            Assert.IsTrue(TypeMixMetrics.MixOf(cirrusCover, cirrusType, 2, 0.2).TypesIn300kmWindow >= 1.5, "cirrus types in a window of 28.6 d" + tag);

                            // the rain deck: the generator's own systems (no stage); its pattern is smaller than half the shortest wavelength of the body's weather systems
                            byte[] deckCover, deckType;
                            TypeMixFixture.Layer(field, body, shared, deck, true, out deckCover, out deckType);
                            TypeMixMetrics.Sizes ds = TypeMixMetrics.Of(deckCover, 0.5);
                            Assert.IsTrue(ds.CorrZonalKm < 0.5 * lambdaMinKm && ds.CorrMeridionalKm < 0.5 * lambdaMinKm, "rain deck corr length " + ds.CorrZonalKm + " / " + ds.CorrMeridionalKm + " km against the systems' shortest wavelength " + lambdaMinKm + tag);
                        }
                    }
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
        }

        [TestMethod, TestCategory("TypeMix")]
        public void TheCloudMapsOfAGameDayHaveTheSizesAndTheMixOfAPlanetOfKerbinsRadius()
        {
            CheckTheMapsOfABody(seed => FieldFixture.Field(seed), TypeMixFixture.ShippedLayers(), 600.0, 10.5, TypeMixFixture.Day, "home planet");
        }

        [TestMethod, TestCategory("TypeMix")]
        public void TheCloudMapsOfASecondBodyOfAnotherSizeHaveTheSameSizesInItsOwnUnits()
        {
            BodyFacts f = FieldFixture.SecondFacts();
            CheckTheMapsOfABody(seed => FieldFixture.SecondField(seed), TypeMixFixture.ShippedLayersScaled(7.5 / 10.5), f.RadiusM / 1000.0, 7.5, f.DaySeconds, "second body");
        }

        [TestMethod, TestCategory("TypeMix")]
        public void ThePatternsOfTheStageAreFinerOnABodyWithAShallowerTroposphere()
        {
            // the same cover through the same layer on two bodies: the pattern's correlation length in km follows the troposphere depth (10.5 / 7.5 km), within the spread of one noise
            CloudLayerDef cumulus = Cumulus();
            double[] lengthKm = new double[2];
            for (int b = 0; b < 2; b++)
            {
                double radius = b == 0 ? 600.0 : 500.0, depth = b == 0 ? 10.5 : 7.5;
                var inp = new CloudMesoInputs { Width = W, Height = H, Precip = Flat(0), Humidity = Flat(128), Surface = Flat(128), Ut = 3.0e6, Seed = 5UL, RadiusKm = radius, DepthKm = depth, Edge1Rad = Math.PI / 6.0, Edge2Rad = Math.PI / 3.0 };
                byte[] coverOut = null, typeOut = null;
                CloudMesoStage.Apply(cumulus, inp, Flat(128), Flat(64), ref coverOut, ref typeOut);
                using (TypeMixMetrics.Use(radius, depth))
                {
                    TypeMixMetrics.Sizes s = TypeMixMetrics.Of(coverOut, 0.5);
                    lengthKm[b] = 0.5 * (s.CorrZonalKm + s.CorrMeridionalKm);
                    Assert.IsTrue(lengthKm[b] > 0.0);
                }
            }
            // the second body's patterns are 7.5 / 10.5 = 0.71 of the home planet's, not the radii's 0.83 and not the same 1.0
            Assert.AreEqual(7.5 / 10.5, lengthKm[1] / lengthKm[0], 0.12, "pattern size in km: " + lengthKm[0] + " on the home planet, " + lengthKm[1] + " on the second body");
        }
    }
}
