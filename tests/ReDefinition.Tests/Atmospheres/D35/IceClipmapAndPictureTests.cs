using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D35
{
    // Package D35, the sheet's geometry and the orbit picture made from a scripted field.
    [TestClass]
    public class IceClipmapAndPictureTests
    {
        // ------------------------------------------------------------------------------------------------- the clipmap

        [TestMethod]
        public void TheLargestCellKeepsTheChordWithinSixCentimetres()
        {
            foreach (float radius in new[] { 200000f, 500000f, 600000f, 6371000f })
            {
                float cell = IceClipmap.LargestCell(radius);
                double sagitta = (double)cell * cell / (8.0 * radius);
                Assert.IsTrue(sagitta <= IceClipmap.MaxSagittaM + 1e-9 || cell <= IceClipmap.LeastLargestCellM, "radius " + radius + " cell " + cell + " sagitta " + sagitta);
            }
            Assert.AreEqual(512f, IceClipmap.LargestCell(600000f), "Kerbin");
        }

        [TestMethod]
        public void TheRingsTileTheSquareWithoutGapsOrOverlapAndReachTheFarDistance()
        {
            List<IceClipmap.Ring> rings = IceClipmap.Rings(600000f, 80000f);
            float previousOuter = 0f;
            double area = 0;
            foreach (IceClipmap.Ring ring in rings)
            {
                Assert.AreEqual(previousOuter, ring.InnerHalf, 1e-3, "a ring starts where the one inside ends");
                Assert.IsTrue(ring.OuterHalf > ring.InnerHalf);
                Assert.AreEqual(0.0, (2.0 * ring.OuterHalf / ring.Cell) % 1.0, 1e-6, "a whole number of cells along the side");
                Assert.AreEqual(0.0, (2.0 * ring.InnerHalf / ring.Cell) % 1.0, 1e-6, "the hole is a whole number of cells");
                area += 4.0 * ring.OuterHalf * ring.OuterHalf - 4.0 * ring.InnerHalf * ring.InnerHalf;
                previousOuter = ring.OuterHalf;
            }
            Assert.IsTrue(previousOuter >= 80000f, "the last ring reaches the far distance: " + previousOuter);
            Assert.AreEqual(4.0 * previousOuter * previousOuter, area, 1.0, "the rings cover the square once");
            // The cells grow outward.
            for (int i = 1; i < rings.Count; i++) Assert.IsTrue(rings[i].Cell >= rings[i - 1].Cell);
            Assert.AreEqual(IceClipmap.SmallestCellM, rings[0].Cell);
        }

        [TestMethod]
        public void TheMeshHasTwoTrianglesPerCellSharedVerticesAndValidIndices()
        {
            List<IceClipmap.Ring> rings = IceClipmap.Rings(600000f, 20000f);
            float[] xz;
            int[] indices;
            IceClipmap.Build(rings, out xz, out indices);
            long cells = IceClipmap.CellCount(rings);
            Assert.AreEqual(cells * 6, indices.Length, "two triangles a cell");
            int vertices = xz.Length / 2;
            foreach (int i in indices) Assert.IsTrue(i >= 0 && i < vertices);
            // A ring of 64 x 64 cells has 65 x 65 vertices: shared, not 4 a cell.
            Assert.IsTrue(vertices < cells * 2, "vertices " + vertices + " cells " + cells);
            // No triangle is degenerate and all are counter-clockwise seen from above (the sheet is lit from either side but culled by none).
            for (int t = 0; t < indices.Length; t += 3)
            {
                float ax = xz[2 * indices[t]], az = xz[2 * indices[t] + 1];
                float bx = xz[2 * indices[t + 1]] - ax, bz = xz[2 * indices[t + 1] + 1] - az;
                float cx = xz[2 * indices[t + 2]] - ax, cz = xz[2 * indices[t + 2] + 1] - az;
                Assert.AreNotEqual(0f, bx * cz - bz * cx, "a degenerate triangle");
            }
        }

        [TestMethod]
        public void TheKerbinSheetIsTensOfThousandsOfCellsNotMillions()
        {
            long cells = IceClipmap.CellCount(IceClipmap.Rings(600000f, 80000f));
            Assert.IsTrue(cells > 50000 && cells < 200000, "cells " + cells);
        }

        [TestMethod]
        public void ANearFarDistanceStillGivesOneRingAndASmallBodyGivesMoreCells()
        {
            List<IceClipmap.Ring> near = IceClipmap.Rings(600000f, 100f);
            Assert.AreEqual(1, near.Count);
            Assert.IsTrue(near[0].OuterHalf >= 100f);
            long big = IceClipmap.CellCount(IceClipmap.Rings(6371000f, 80000f));
            long small = IceClipmap.CellCount(IceClipmap.Rings(60000f, 80000f));
            Assert.IsTrue(small > big, "a small body needs smaller cells");
        }

        // ------------------------------------------------------------------------------------------------- the picture

        // A coast map for a planet whose land is a band of longitudes (sphere frame) 100 to 140 degrees.
        private static byte[] Coast()
        {
            var heights = new float[ShoreGrid.Width * ShoreGrid.Height];
            for (int row = 0; row < ShoreGrid.Height; row++)
                for (int col = 0; col < ShoreGrid.Width; col++)
                {
                    double lon = (col + 0.5) / ShoreGrid.Width * 360.0;
                    double lat = ((row + 0.5) / ShoreGrid.Height - 0.5) * 180.0;
                    heights[row * ShoreGrid.Width + col] = lon >= 100.0 && lon < 140.0 && Math.Abs(lat) < 70.0 ? 400f : -3000f;
                }
            return ShoreGrid.Build(heights, 0f, 600000f);
        }

        private static IceMapCopy Constant(float value)
        {
            var map = new WeatherMap();
            map.Resize(256, 128);
            for (int i = 0; i < map.Values.Length; i++) map.Values[i] = value;
            var copy = new IceMapCopy();
            copy.CopyFrom(map);
            return copy;
        }

        private static IceInputs Inputs(float fraction, float thicknessM, byte[] shore)
        {
            var input = new IceInputs { Shore = shore };
            input.Params = new IceParams { RadiusM = 600000f, FloeSizeKm = 3f, LeadFraction = 0.08f, Seed = 999u };
            input.Fraction.CopyFrom(null);
            input.Fraction.Width = 256;
            input.Fraction.Height = 128;
            input.Fraction.Values = Constant(fraction).Values;
            input.ThicknessM.Width = 256;
            input.ThicknessM.Height = 128;
            input.ThicknessM.Values = Constant(thicknessM).Values;
            return input;
        }

        [TestMethod]
        public void ThePictureHasNoIceOnLandAndTheIceHidesTheGlintByItsAlpha()
        {
            var input = Inputs(0.8f, 0.6f, Coast());
            var rgba = new byte[IceOrbitPicture.Width * IceOrbitPicture.Height * 4];
            IceOrbitPicture.Stats stats = IceOrbitPicture.Fill(input, rgba, IceOrbitPicture.Width, IceOrbitPicture.Height);
            Assert.IsTrue(stats.SeaTexels > 0 && stats.IceTexels > 0);
            Assert.AreEqual(0.8, stats.IceTexels / (double)stats.SeaTexels, 0.04, "the share of ice texels is the field's");
            long landAlpha = 0, solid = 0;
            for (int row = 0; row < IceOrbitPicture.Height; row++)
            {
                double lat = 90.0 - (row + 0.5) / IceOrbitPicture.Height * 180.0;
                for (int col = 0; col < IceOrbitPicture.Width; col++)
                {
                    double lon = (col + 0.5) / IceOrbitPicture.Width * 360.0 - 180.0;
                    bool sea;
                    IceOrbitPicture.StateAt(input, lat, lon, out sea);
                    byte a = rgba[(row * IceOrbitPicture.Width + col) * 4 + 3];
                    if (!sea && a != 0) landAlpha++;
                    if (a == 255) solid++;
                }
            }
            Assert.AreEqual(0, landAlpha, "ice on land");
            Assert.IsTrue(solid > 0, "thick ice is opaque (the planet's glint is hidden under it)");
        }

        [TestMethod]
        public void ThinIceIsLessOpaqueAndDarkerThanThickIce()
        {
            byte[] coast = Coast();
            var thin = Inputs(1f, 0.02f, coast);
            var thick = Inputs(1f, 1.0f, coast);
            var a = new byte[IceOrbitPicture.Width * IceOrbitPicture.Height * 4];
            var b = new byte[a.Length];
            IceOrbitPicture.Fill(thin, a, IceOrbitPicture.Width, IceOrbitPicture.Height);
            IceOrbitPicture.Fill(thick, b, IceOrbitPicture.Width, IceOrbitPicture.Height);
            double alphaThin = 0, alphaThick = 0, redThin = 0, redThick = 0;
            int n = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i + 3] == 0 || b[i + 3] == 0) continue;
                alphaThin += a[i + 3];
                alphaThick += b[i + 3];
                redThin += a[i];
                redThick += b[i];
                n++;
            }
            Assert.IsTrue(n > 1000);
            Assert.IsTrue(alphaThin / n < alphaThick / n - 40, "thin " + alphaThin / n + " thick " + alphaThick / n);
            Assert.IsTrue(redThin / n < redThick / n - 20, "thin ice is darker");
        }

        [TestMethod]
        public void TheWindowCoverageEqualsTheFieldsMeanOnTheSea()
        {
            byte[] coast = Coast();
            foreach (float f in new[] { 0.1f, 0.35f, 0.6f, 0.85f })
            {
                var input = Inputs(f, 0.5f, coast);
                double share, mean;
                int samples;
                IceOrbitPicture.WindowCoverage(input, -80, 80, -180, 180, 0.5, out share, out mean, out samples);
                Assert.IsTrue(samples > 10000);
                Assert.AreEqual(f, mean, 1e-5);
                Assert.AreEqual(f, share, 0.05, "rendered coverage at field fraction " + f);
            }
        }

        [TestMethod]
        public void AForcedFractionReplacesTheFieldAndAPermanentSeaIsAllIce()
        {
            byte[] coast = Coast();
            var input = Inputs(0.0f, 0.5f, coast);
            double share, mean;
            int samples;
            IceOrbitPicture.WindowCoverage(input, -80, 80, -180, 180, 1.0, out share, out mean, out samples);
            Assert.AreEqual(0.0, share, 1e-9);
            input.ForcedFraction = 1f;
            IceOrbitPicture.WindowCoverage(input, -80, 80, -180, 180, 1.0, out share, out mean, out samples);
            Assert.AreEqual(1.0, share, 0.002, "forced fraction 1: all sea is ice (no leads at 1)");
            var permanent = Inputs(0.0f, 0.0f, coast);
            permanent.Params = new IceParams { RadiusM = 600000f, FloeSizeKm = 3f, LeadFraction = 0.3f, Seed = 999u, Permanent = true };
            IceOrbitPicture.WindowCoverage(permanent, -80, 80, -180, 180, 1.0, out share, out mean, out samples);
            Assert.AreEqual(1.0, share, 0.002, "a permanent sea is frozen without a field");
        }

        [TestMethod]
        public void AMissingFieldMapAndAMissingCoastMapGiveNoIceAndNoException()
        {
            var noMaps = new IceInputs { Shore = Coast() };
            double share, mean;
            int samples;
            IceOrbitPicture.WindowCoverage(noMaps, -80, 80, -180, 180, 2.0, out share, out mean, out samples);
            Assert.AreEqual(0.0, share, 1e-9);
            var noCoast = Inputs(0.5f, 0.5f, null);
            IceOrbitPicture.WindowCoverage(noCoast, -80, 80, -180, 180, 2.0, out share, out mean, out samples);
            Assert.IsTrue(samples > 0 && share > 0.3, "without a coast map every texel counts as sea");
        }

        [TestMethod]
        public void FlippingTheRowsPutsTheSouthFirst()
        {
            var north = new byte[4 * 2 * 3];
            for (int row = 0; row < 3; row++)
                for (int i = 0; i < 8; i++) north[row * 8 + i] = (byte)(row + 1);
            var south = new byte[north.Length];
            IceOrbitLayer.BodyShell.FlipRows(north, south, 2, 3);
            Assert.AreEqual(3, south[0]);
            Assert.AreEqual(2, south[8]);
            Assert.AreEqual(1, south[16]);
        }

        [TestMethod]
        public void TheMapCopyReadsLikeTheFieldsMap()
        {
            var map = new WeatherMap();
            map.Resize(16, 8);
            var rng = new Random(3);
            for (int i = 0; i < map.Values.Length; i++) map.Values[i] = (float)rng.NextDouble();
            var copy = new IceMapCopy();
            copy.CopyFrom(map);
            foreach (double lat in new[] { -89.0, -33.3, 0.0, 41.7, 89.9 })
                foreach (double lon in new[] { -180.0, -123.4, -0.1, 77.7, 179.9 })
                    Assert.AreEqual(map.Sample(lat, lon), copy.Sample(lat, lon), 1e-6, lat + "," + lon);
            copy.CopyFrom(null);
            Assert.IsFalse(copy.Valid);
            Assert.AreEqual(0f, copy.Sample(10, 10));
        }
    }
}
