using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The pure parts of the cover on the ground: the noise, the colour blend, the repaint of a quad's colours from the field's map and the
    // decisions about the scatters.
    [TestClass]
    public class SnowGroundTests
    {
        private static CoverDef Snow(double minLat = -90, double maxLat = 90, double maxSlope = 60)
        {
            return new CoverDef("snow", CoverKind.Snow, 0.93f, 0.95f, 0.98f, 0.8f, 0.5f, minLat, maxLat, double.NegativeInfinity, double.PositiveInfinity, maxSlope, true, true, 10800, 0.3f, 4f);
        }

        [TestMethod]
        public void TheNoiseIsDeterministicAndInRange()
        {
            for (double lat = -85; lat <= 85; lat += 5)
                for (double lon = 0; lon < 360; lon += 17)
                {
                    double y = Math.Sin(lat * Math.PI / 180);
                    double r = Math.Cos(lat * Math.PI / 180);
                    double x = r * Math.Cos(lon * Math.PI / 180), z = r * Math.Sin(lon * Math.PI / 180);
                    double a = SnowNoise.Value(x, y, z);
                    Assert.AreEqual(a, SnowNoise.Value(x, y, z));
                    Assert.AreEqual(a, SnowNoise.Value(x * 3, y * 3, z * 3), 1e-12, "the length of the direction does not matter");
                    Assert.IsTrue(a >= -1 && a <= 1, lat + "," + lon + ": " + a);
                }
        }

        [TestMethod]
        public void TheNoiseVariesAndIsSmooth()
        {
            double min = 1, max = -1, step = 0;
            double previous = SnowNoise.Value(1, 0.3, 0);
            for (int i = 1; i <= 2000; i++)
            {
                double angle = i * 0.0004;
                double value = SnowNoise.Value(Math.Cos(angle), 0.3, Math.Sin(angle));
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                step = Math.Max(step, Math.Abs(value - previous));
                previous = value;
            }
            Assert.IsTrue(max - min > 0.5, "range " + min + " to " + max);
            Assert.IsTrue(step < 0.2, "largest step between neighbours " + step);
        }

        [TestMethod]
        public void TheNoiseOfADegenerateDirectionIsZero()
        {
            Assert.AreEqual(0, SnowNoise.Value(0, 0, 0));
            Assert.AreEqual(0, SnowNoise.Value(double.NaN, 1, 0));
            Assert.AreEqual(0, SnowNoise.Value(double.PositiveInfinity, 1, 0));
            Assert.AreEqual(0, SnowNoise.Lattice(double.NaN, 1, 0));
        }

        [TestMethod]
        public void TheBlendKeepsTheColourAtZeroAndTheLayersColourAtOneAndAlphaAlways()
        {
            CoverDef def = Snow();
            var ground = new Color(0.2f, 0.4f, 0.1f, 0.37f);
            Color none = CoverPaint.Blend(ground, def, 0f);
            Assert.AreEqual(ground, none);
            Color full = CoverPaint.Blend(ground, def, 1f);
            Assert.AreEqual(def.Red, full.r, 1e-6);
            Assert.AreEqual(def.Green, full.g, 1e-6);
            Assert.AreEqual(def.Blue, full.b, 1e-6);
            Assert.AreEqual(0.37f, full.a, 1e-6, "alpha is left as it was");
            Color half = CoverPaint.Blend(ground, def, 0.5f);
            Assert.AreEqual((0.2f + def.Red) / 2, half.r, 1e-6);
            Assert.AreEqual(full, CoverPaint.Blend(ground, def, 7f), "an amount above 1 counts as 1");
            Assert.AreEqual(ground, CoverPaint.Blend(ground, def, float.NaN), "not a number leaves the colour");
            var dust = new CoverDef("dust", CoverKind.Dust, 0.5f, 0.42f, 0.33f, 0.9f, 0.05f, -90, 90, double.NegativeInfinity, double.PositiveInfinity, 60, true, true, 86400, 0.6f, 4f);
            Assert.AreEqual(0.5f, CoverPaint.Blend(ground, dust, 1f).r, 1e-6, "dust has its own colour");
        }

        [TestMethod]
        public void TheByteBlendAgreesWithTheFloatBlendWithinARoundingStep()
        {
            CoverDef def = Snow();
            for (int r = 0; r < 256; r += 51)
                for (float amount = 0f; amount <= 1.0001f; amount += 0.25f)
                {
                    var b = new Color32((byte)r, (byte)(255 - r), (byte)(r / 2), 200);
                    Color32 viaBytes = CoverPaint.Blend(b, def, amount);
                    Color viaFloat = CoverPaint.Blend((Color)b, def, amount);
                    Assert.AreEqual(viaFloat.r * 255f, viaBytes.r, 1.01f);
                    Assert.AreEqual(viaFloat.g * 255f, viaBytes.g, 1.01f);
                    Assert.AreEqual(viaFloat.b * 255f, viaBytes.b, 1.01f);
                    Assert.AreEqual(200, viaBytes.a);
                }
        }

        // A frame with 0.25 m of a layer whose full depth is 0.5 m north of 50 degrees.
        private static CoverMap.Frame NorthFrame(CoverMap map, float depthM = 0.25f, double fromLat = 50)
        {
            const int w = 256, h = 128;
            var values = new float[w * h];
            for (int j = 0; j < h; j++)
                if (90.0 - (j + 0.5) * 180.0 / h > fromLat)
                    for (int i = 0; i < w; i++) values[j * w + i] = depthM;
            CoverMap.Frame frame = map.Resample(values, w, h, 0.5f, 1, 10.0);
            map.Publish(frame);
            return frame;
        }

        private static QuadCover Quad(double lat, int n = 16)
        {
            var q = new QuadCover(null, n);
            for (int i = 0; i < n; i++)
                q.Set(i, new Color32(60, 90, 40, 255), lat + i * 0.01, 20 + i * 0.01, 100, SnowNoise.Value(Math.Cos(i), 0.3, Math.Sin(i)));
            return q;
        }

        private static CoverLook Look(CoverDef def, double strength = 1.0, double edge = 0.0)
        {
            return new CoverLook { Def = def, Strength = strength, EdgeNoise = edge, Drift = 0.0, Ut = 0.0, RadiusM = 600000.0 };
        }

        [TestMethod]
        public void AQuadIsPaintedFromTheFieldsMapAndStaysBareWhereTheFieldHasNone()
        {
            CoverMap.Frame frame = NorthFrame(new CoverMap());
            CoverDef def = Snow();
            QuadCover north = Quad(70);
            float mean = north.Paint(frame, Look(def), false);
            Assert.AreEqual(0.5f, mean, 0.01f, "0.25 m of 0.5 m");
            Assert.AreEqual(north.LastMean, mean);
            for (int i = 0; i < north.Length; i++)
            {
                Assert.AreEqual(north.Original[i].a, north.Painted[i].a);
                Assert.IsTrue(north.Painted[i].r > north.Original[i].r, "the vertex turned toward the layer's colour");
            }
            QuadCover equator = Quad(0);
            Assert.AreEqual(0f, equator.Paint(frame, Look(def), false));
            for (int i = 0; i < equator.Length; i++) Assert.AreEqual(equator.Original[i], equator.Painted[i]);
        }

        [TestMethod]
        public void TheStrengthSettingScalesTheCoverAndMeanAmountMatchesPaint()
        {
            CoverMap.Frame frame = NorthFrame(new CoverMap());
            CoverDef def = Snow();
            QuadCover q = Quad(70);
            float full = q.MeanAmount(frame, Look(def), false);
            float half = q.MeanAmount(frame, Look(def, 0.5), false);
            Assert.AreEqual(full / 2f, half, 1e-4);
            Assert.AreEqual(full, q.Paint(frame, Look(def), false), 1e-6);
            Assert.AreEqual(0f, q.MeanAmount(frame, Look(def, 0.0), false));
        }

        [TestMethod]
        public void ASteepVertexAndASeaVertexKeepTheirColour()
        {
            CoverMap.Frame frame = NorthFrame(new CoverMap());
            CoverDef def = Snow(maxSlope: 40);
            QuadCover q = Quad(70, 4);
            for (int i = 0; i < 4; i++) q.SetSlope(i, 70, double.NaN, double.NaN);
            Assert.AreEqual(0f, q.MeanAmount(frame, Look(def), false), "over the slope limit");
            QuadCover sea = new QuadCover(null, 4);
            for (int i = 0; i < 4; i++) sea.Set(i, new Color32(10, 20, 80, 255), 70, 20 + i, -5, 0);
            Assert.AreEqual(0f, sea.MeanAmount(frame, Look(def), true), "no cover on water");
            Assert.IsTrue(sea.MeanAmount(frame, Look(def), false) > 0.3f, "on a body without an ocean the same height is land");
        }

        [TestMethod]
        public void AVertexOutsideTheQuadIsIgnoredAndTheLengthIsTheVertexCount()
        {
            var q = new QuadCover(null, 4);
            q.Set(-1, new Color32(1, 2, 3, 4), 1, 2, 3, 0);
            q.Set(4, new Color32(1, 2, 3, 4), 1, 2, 3, 0);
            q.Set(3, new Color32(1, 2, 3, 4), 1, 2, 3, 0);
            q.SetSlope(9, 10, 0, 0);
            Assert.AreEqual(4, q.Length);
            Assert.AreEqual(new Color32(1, 2, 3, 4), q.Original[3]);
            Assert.AreEqual(default(Color32), q.Original[0]);
        }

        [TestMethod]
        public void AQuadIsPaintedAgainOnlyWhenTheMapUnderItChanged()
        {
            var map = new CoverMap();
            CoverDef def = Snow();
            CoverMap.Frame first = NorthFrame(map);
            QuadCover north = Quad(70);
            QuadCover south = Quad(-40);
            Assert.IsTrue(north.NeedsPaint(first, 1, 0, 0), "never painted");
            north.Paint(first, Look(def), false);
            north.MarkPainted(first, 1, 0);
            south.Paint(first, Look(def), false);
            south.MarkPainted(first, 1, 0);
            Assert.IsFalse(north.NeedsPaint(first, 1, 0, 0));
            Assert.IsTrue(north.NeedsPaint(first, 2, 0, 0), "the settings changed");
            // A storm in the south changes the south and not the north.
            const int w = 256, h = 128;
            var values = new float[w * h];
            for (int j = 0; j < h; j++)
            {
                double lat = 90.0 - (j + 0.5) * 180.0 / h;
                for (int i = 0; i < w; i++) values[j * w + i] = lat > 50 ? 0.25f : lat < -30 ? 0.4f : 0f;
            }
            CoverMap.Frame second = map.Resample(values, w, h, 0.5f, 2, 20.0);
            map.Publish(second);
            Assert.IsFalse(north.NeedsPaint(second, 1, 0, 0), "the tiles under the north did not change");
            Assert.IsTrue(south.NeedsPaint(second, 1, 0, 0), "the tiles under the south did");
            Assert.IsTrue(north.NeedsPaint(second, 1, 1000, 900), "the wind pattern moved far enough");
        }

        [TestMethod]
        public void TheScattersAreMadeAgainOnlyWhenTheMeanCrossesFourTenthsOrEightTenths()
        {
            Assert.AreEqual(0, QuadCover.Band(0.1f));
            Assert.AreEqual(1, QuadCover.Band(0.4f));
            Assert.AreEqual(1, QuadCover.Band(0.79f));
            Assert.AreEqual(2, QuadCover.Band(0.8f));
            Assert.IsTrue(QuadCover.RegenerateScatters(CoverKind.Snow, -1f, 0.2f), "never known");
            Assert.IsFalse(QuadCover.RegenerateScatters(CoverKind.Snow, 0.1f, 0.35f), "moved inside a band");
            Assert.IsTrue(QuadCover.RegenerateScatters(CoverKind.Snow, 0.35f, 0.45f), "crossed 0.4");
            Assert.IsFalse(QuadCover.RegenerateScatters(CoverKind.Snow, 0.45f, 0.75f));
            Assert.IsTrue(QuadCover.RegenerateScatters(CoverKind.Snow, 0.75f, 0.85f), "crossed 0.8");
            Assert.IsFalse(QuadCover.RegenerateScatters(CoverKind.Dust, -1f, 0.9f), "dust makes no respawn");
            Assert.IsFalse(QuadCover.RegenerateScatters(CoverKind.Dust, 0.1f, 0.9f));
        }

        [TestMethod]
        public void TheRecordKeepsTheNormalsOfTheDriftOnlyWhenAskedFor()
        {
            var q = new QuadCover(null, 4);
            Assert.IsFalse(q.HasNormals);
            q.SetSlope(0, 10, double.NaN, double.NaN);
            Assert.IsFalse(q.HasNormals, "no wind drift, no extra data");
            q.SetSlope(1, 10, 0.5, -0.25);
            Assert.IsTrue(q.HasNormals);
            Assert.AreEqual(0.5, q.NormalEastOf(1), 0.01);
            Assert.AreEqual(-0.25, q.NormalNorthOf(1), 0.01);
        }
    }
}
