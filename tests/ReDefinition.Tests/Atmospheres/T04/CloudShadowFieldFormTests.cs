using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // Master plan 47.U stage 1: the CPU twin of the cloud shadow (CloudShadowShapeNoise) places the clouds of a type with a form by the cloud field, as the shaders do
    // (CloudCells.cginc FieldCloud, the cloud shadow shader's LayerExtinction): the field's read, then the body on the bulge noise's rank. The shader side is judged by the weather
    // player's scene cloud-shadow -shadownoise 3 (the series u1-field).
    [TestClass]
    public class CloudShadowFieldFormTests
    {
        private const int FieldSize = 256;
        private const double Rm = 602000.0;
        private static readonly NoiseVolumeSet Volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 5u);
        private static readonly float[] FieldData = CloudField.Build(FieldSize);

        private static NoiseLayerParams Params()
        {
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            prm.OffsetX = 12345.0;
            prm.OffsetY = 6789.0;
            prm.OffsetZ = 4321.0;
            prm.ReferenceRadiusM = Rm;
            return prm;
        }

        private static CloudShadowShapeNoise Noise(bool field)
        {
            var n = new CloudShadowShapeNoise();
            n.Set(Volumes, new[] { Params() }, null);
            n.SetField(field ? FieldData : null, FieldSize, 0.0);
            return n;
        }

        private static CloudLayerDef Layer(float noiseAmount, int form, out LayerTypeTable table)
        {
            CloudTypeDef type = CloudTypeDef.Slab("s", 0.1f, 0.9f);
            type.Form = form;
            var layer = new CloudLayerDef("l", 1, 1000f, 3000f, new[] { type }, 0.001f);
            layer.Coverage = 0.35f;
            layer.EdgeSoftness = 0.15f;
            layer.NoiseAmount = noiseAmount;
            layer.NoiseScaleM = 500f;
            layer.CellFine = 0f;   // the finer bulge octaves need the cumuliform volume; the first octave carries the test
            table = new LayerTypeTable(layer);
            return layer;
        }

        // A point of the layer's middle sphere near the equator.
        private static void Point(int k, out double x, out double y, out double z)
        {
            double lon = 0.3 + k * 0.00037, lat = 0.01 * Math.Sin(k * 1.7);
            x = Rm * Math.Cos(lat) * Math.Cos(lon);
            y = Rm * Math.Cos(lat) * Math.Sin(lon);
            z = Rm * Math.Sin(lat);
        }

        [TestMethod]
        public void ATypeWithAFormIsTheFieldsBodyOnTheBulgeNoisesRankWhateverTheLayersNoiseAmount()
        {
            int form = CloudField.ClassOf("mediocris");
            foreach (float noiseAmount in new[] { 1f, 0.4f })
            {
                LayerTypeTable table;
                CloudLayerDef layer = Layer(noiseAmount, form, out table);
                CloudShadowShapeNoise noise = Noise(true);
                double b, tp;
                table.InterpolatedHeights(0.0, out b, out tp);
                double coverage = layer.EffectiveCoverage, edge = Math.Max(layer.EffectiveEdgeSoftness, 1e-4f);
                double typeHeightM = (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM);
                double bulge = CloudLookTuning.CellBulgeFor(layer), band = CloudLookTuning.CellBandFor(layer);
                const double cov = 0.62, layerShare = 0.3;
                int bodies = 0, empty = 0, partly = 0;
                for (int k = 0; k < 600; k++)
                {
                    double x, y, z;
                    Point(k, out x, out y, out z);
                    double got = noise.Share(0, layer, x, y, z, cov, table, 0.0, layerShare);
                    // By hand: the field's read at the map place, then the body on the noise's rank (threshold at the mean, a unit edge, the full swing).
                    double r = Math.Sqrt(x * x + y * y + z * z);
                    double u = (Math.Atan2(y, x) * 180.0 / Math.PI - layer.MapShiftDeg + 180.0) / 360.0, v = (90.0 - Math.Asin(z / r) * 180.0 / Math.PI) / 180.0;
                    CloudCells.CellRead read;
                    int cloud = CloudField.Cloud(FieldData, FieldSize, CloudField.Classes[form], 1.0, 0.0, layer.ClearSkyMap(cov), coverage, edge, layer.EffectiveNoiseAmount, u, v, Rm,
                                                 (layerShare - b) / (tp - b), typeHeightM, bulge, band, 0.0, out read);
                    double want = 0.0;
                    if (cloud > 0)
                    {
                        NoiseLayerParams prm = Params();
                        prm.CumuliformPreset = table.CumuliformAtTypeValue(0.0);
                        prm.SmoothingScales = table.SmoothingOf(0.0);
                        prm.ShapeLod = CloudShapeModel.SmoothingLod(prm.SmoothingScales, Volumes.ShapeN);
                        double rank = CloudShapeModel.Share(Volumes, ref prm, x, y, z, 1.0 - coverage, coverage, 1.0, 1.0, 0.0, false);
                        double s = CloudCells.CellBody(read, (layerShare - b) / (tp - b), rank, 0.0, 0.0, typeHeightM, bulge, band, 0.0);
                        want = s <= 0.0 ? 0.0 : CloudWater.Pow(s, layer.EffectiveWaterPowerAt(table, 0.0));
                    }
                    Assert.AreEqual(want, got, 1e-9, "noise amount " + noiseAmount + ", point " + k);
                    if (got <= 0.0) empty++; else if (got >= 1.0) bodies++; else partly++;
                }
                // The field draws separate bodies: points inside, points outside (the map's threshold alone would give one share everywhere at a constant map value and no noise).
                Assert.IsTrue(bodies > 30 && empty > 30, "noise amount " + noiseAmount + ": inside " + bodies + ", outside " + empty + ", at a surface " + partly);
            }
        }

        [TestMethod]
        public void WithoutTheFieldsDataOrWithoutAHeightTheMapsThresholdIsRead()
        {
            // The same layer and table in every call; the type has no LIFT and no FORM amount, so the map's path gives the same share with or without a height. That path is taken
            // (a) in the frames before the field is built and (b) by a caller that gives no height (it cannot place a body); the field's path differs from it at most points.
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, CloudField.ClassOf("mediocris"), out table);
            CloudShadowShapeNoise noField = Noise(false), field = Noise(true);
            int differs = 0;
            for (int k = 0; k < 300; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                double mapPath = noField.Share(0, layer, x, y, z, 0.62, table, 0.0);
                Assert.AreEqual(mapPath, noField.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.3), 1e-12, "no field data, point " + k);
                Assert.AreEqual(mapPath, field.Share(0, layer, x, y, z, 0.62, table, 0.0), 1e-12, "no height, point " + k);
                if (Math.Abs(field.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.3) - mapPath) > 0.05) differs++;
            }
            Assert.IsTrue(differs > 100, "points where the field's body is not the map's threshold: " + differs);
        }

        [TestMethod]
        public void TheBodysHeightIsTheLocalOneWhereTheCallerGivesIt()
        {
            // The shader's TypeSlab moves the type's base and top with the height maps; the body's height and the point's height in it follow them (HeightDensity hands them in).
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, CloudField.ClassOf("mediocris"), out table);
            CloudShadowShapeNoise noise = Noise(true);
            int raised = 0;
            for (int k = 0; k < 600; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                // Above the type's own top (0.9) nothing is drawn with the table's heights ...
                Assert.AreEqual(0.0, noise.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.93), 0.0, "point " + k);
                // ... and inside the body's base when the local top is raised so that the point lies low in the type.
                if (noise.Share(0, layer, x, y, z, 0.62, table, 0.0, 0.93, 0.9, 1.6) > 0.0) raised++;
            }
            Assert.IsTrue(raised > 30, "points in a body whose local base is 0.9: " + raised);
        }

        [TestMethod]
        public void TheFieldPlacesTheCloudsWithoutTheShapeNoiseToo()
        {
            // The shaders read the field whether or not the shape noise is bound (their noise amount is then 0 and the bulges' rank 0.5): the frames before the volumes are ready
            // and the switch off draw the bare bodies, not the map's threshold.
            int form = CloudField.ClassOf("mediocris");
            LayerTypeTable table, plainTable;
            CloudLayerDef layer = Layer(1f, form, out table), plain = Layer(1f, 0, out plainTable);
            var none = new CloudShadowShapeNoise();
            none.Set(null, new[] { Params() }, null);
            Assert.IsFalse(none.Active, "neither the noise nor the field");
            Assert.AreEqual(-1.0, none.Share(0, layer, Rm, 0.0, 0.0, 0.62, table, 0.0, 0.3));
            var noise = new CloudShadowShapeNoise();
            noise.Set(null, new[] { Params() }, null);
            noise.SetField(FieldData, FieldSize, 0.0);
            Assert.IsTrue(noise.Active, "the field alone");
            double b, tp;
            table.InterpolatedHeights(0.0, out b, out tp);
            double coverage = layer.EffectiveCoverage, edge = Math.Max(layer.EffectiveEdgeSoftness, 1e-4f), typeHeightM = (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM);
            double bulge = CloudLookTuning.CellBulgeFor(layer), band = CloudLookTuning.CellBandFor(layer);
            // Without the noise's swing the share of the region that is cloud is the map's place on the edge ramp alone: a map value a little below the threshold is a
            // share of about 0.37 (0.62, the other tests' value, is a closed layer here).
            const double layerShare = 0.3;
            double cov = 1.0 - coverage - 0.02;
            int bodies = 0, empty = 0;
            for (int k = 0; k < 600; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                Assert.AreEqual(-1.0, noise.Share(0, plain, x, y, z, cov, plainTable, 0.0, layerShare), "a type without a form reads the map's plain threshold, point " + k);
                double got = noise.Share(0, layer, x, y, z, cov, table, 0.0, layerShare);
                double r = Math.Sqrt(x * x + y * y + z * z);
                double u = (Math.Atan2(y, x) * 180.0 / Math.PI - layer.MapShiftDeg + 180.0) / 360.0, v = (90.0 - Math.Asin(z / r) * 180.0 / Math.PI) / 180.0;
                CloudCells.CellRead read;
                // The noise amount 0 in the field's read (the share of the region that is cloud is the map's alone), the rank 0.5 in the body.
                int cloud = CloudField.Cloud(FieldData, FieldSize, CloudField.Classes[form], 1.0, 0.0, layer.ClearSkyMap(cov), coverage, edge, 0.0, u, v, Rm, (layerShare - b) / (tp - b),
                                             typeHeightM, bulge, band, 0.0, out read);
                double want = 0.0;
                if (cloud > 0)
                {
                    double s = CloudCells.CellBody(read, (layerShare - b) / (tp - b), 0.5, 0.0, 0.0, typeHeightM, bulge, band, 0.0);
                    want = s <= 0.0 ? 0.0 : CloudWater.Pow(s, layer.EffectiveWaterPowerAt(table, 0.0));
                }
                Assert.AreEqual(want, got, 1e-9, "point " + k);
                if (got <= 0.0) empty++; else if (got >= 1.0) bodies++;
            }
            Assert.IsTrue(bodies > 30 && empty > 30, "inside " + bodies + ", outside " + empty);
            // The closed layer (a map well above the full share) is the field's merged body too: low in the type every place is cloud.
            double closed = 1.0 - coverage + 0.5 * edge + 0.3;
            for (int k = 0; k < 200; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                Assert.IsTrue(noise.Share(0, layer, x, y, z, closed, table, 0.0, b + 0.05 * (tp - b)) >= 0.99, "the closed layer's base, point " + k);
            }
        }

        // One layer with a constant map value and a constant raise of the cloud base (the height source of the test below).
        private sealed class RaisedSource : ICloudLayerSource, ICloudHeightSource
        {
            private readonly CloudLayerDef layer;
            internal readonly HeightTuning Tuning;

            internal RaisedSource(CloudLayerDef layer, HeightTuning tuning)
            {
                Tuning = tuning;
                this.layer = layer;
                layer.HeightDownM = layer.HeightUpM = 600f;
            }

            public int BodyIndex { get { return 1; } }
            public double BodyRadiusM { get { return Rm - 2000.0; } }
            public bool HasSurface { get { return true; } }
            public int LayerCount { get { return 1; } }
            public long Version { get { return 1; } }
            public CloudLayerDef LayerAt(int index) { return layer; }

            public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type)
            {
                coverage = 0.62f;
                type = 0f;
            }

            public HeightTuning HeightOf(int layerIndex) { return Tuning; }
            public LocalHeights SampleHeights(int layerIndex, double latDeg, double lonDeg) { return new LocalHeights { RootM = 400f }; }
        }

        [TestMethod]
        public void TheCloudFieldsReaderHandsTheLocalHeightsToTheBody()
        {
            // HeightDensity reads the type's local base and top once and gives them to the body AND to the height profile: the extinction is the one with the local heights in
            // both, and differs from the one whose body stands on the type table's heights (the cloud shadow before master plan 47.U stage 1's review).
            LayerTypeTable table;
            CloudLayerDef layer = Layer(1f, CloudField.ClassOf("mediocris"), out table);
            var source = new RaisedSource(layer, new HeightTuning { BaseRagged = false });
            Assert.IsTrue(source.Tuning.Any, "the test's height tuning moves the base");
            CloudShadowShapeNoise noise = Noise(true);
            LocalHeights heights = source.SampleHeights(0, 0.0, 0.0);
            double lb, lt, b, tp;
            HeightDensity.LocalBaseTop(layer, table, source.Tuning, 0.0, heights, 0.0, out lb, out lt);
            table.InterpolatedHeights(0.0, out b, out tp);
            Assert.IsTrue(lb > b + 0.05, "the local base " + lb + " lies above the table's " + b);
            const double altitudeM = 1000.0 + 0.45 * 2000.0;
            double layerShare = (altitudeM - layer.InnerAltitudeM) / layer.ThicknessM;
            int differs = 0, drawn = 0;
            for (int k = 0; k < 600; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                double r = Math.Sqrt(x * x + y * y + z * z), latDeg = Math.Asin(z / r) * 180.0 / Math.PI, lonDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
                double got = HeightDensity.Extinction(source, table, 0, altitudeM, latDeg, lonDeg, noise, x, y, z);
                double local = HeightDensity.Extinction(layer, table, source.Tuning, altitudeM, 0.62, 0.0, heights, 0.0, noise.Share(0, layer, x, y, z, 0.62f, table, 0.0, layerShare, lb, lt));
                double nominal = HeightDensity.Extinction(layer, table, source.Tuning, altitudeM, 0.62, 0.0, heights, 0.0, noise.Share(0, layer, x, y, z, 0.62f, table, 0.0, layerShare));
                Assert.AreEqual(local, got, 1e-12, "point " + k);
                if (got > 0.0) drawn++;
                if (Math.Abs(local - nominal) > 1e-6 * layer.EffectiveSigmaPerM) differs++;
            }
            Assert.IsTrue(drawn > 30, "points in a cloud: " + drawn);
            Assert.IsTrue(differs > 30, "points where the table's heights would draw another body: " + differs);

            // A layer whose height variation is switched off altogether: the type table's heights, the plain extinction with the field's share.
            var still = new RaisedSource(layer, new HeightTuning { BaseVariation = false, BasePhysics = false, TopFromUpdraft = false, BaseRagged = false });
            Assert.IsFalse(still.Tuning.Any);
            int stillDrawn = 0;
            for (int k = 0; k < 300; k++)
            {
                double x, y, z;
                Point(k, out x, out y, out z);
                double r = Math.Sqrt(x * x + y * y + z * z), latDeg = Math.Asin(z / r) * 180.0 / Math.PI, lonDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
                double got = HeightDensity.Extinction(still, table, 0, altitudeM, latDeg, lonDeg, noise, x, y, z);
                Assert.AreEqual(LayerDensity.ExtinctionWithShare(layer, table, altitudeM, 0.62f, 0f, noise.Share(0, layer, x, y, z, 0.62f, table, 0.0, layerShare)), got, 1e-12, "point " + k);
                if (got > 0.0) stillDrawn++;
            }
            Assert.IsTrue(stillDrawn > 30, "points in a cloud without the height variation: " + stillDrawn);
        }

        [TestMethod]
        public void TheScatterersBaseIsItsFreshValueOrTheStoredOneWhereOurLastWriteStillStands()
        {
            // Master plan 47.2S: each of the two exposures keeps its own base. Scatterer interpolated (the value is no longer the one written): its fresh value is the base.
            Assert.AreEqual(0.8f, ScattererAirExposure.Base(0.8f, 1.0f, 0.3f, true));
            // Scatterer did not interpolate this frame (our last write still stands): the stored base, so the factor never compounds.
            Assert.AreEqual(1.0f, ScattererAirExposure.Base(0.3f, 1.0f, 0.3f, true));
            // Nothing stored yet: the value as it is.
            Assert.AreEqual(0.3f, ScattererAirExposure.Base(0.3f, 1.0f, 0.3f, false));
        }
    }
}
