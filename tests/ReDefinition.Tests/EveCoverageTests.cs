using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;

namespace ReDefinition.Tests
{
    // The pure part of the EVE cell layer: the equirectangular map of the coverage and the painting of the cells into it.
    [TestClass]
    public class EveCoverageTests
    {
        private const double Kerbin = 600000;

        // EVE's TextureWrapper.GetEquirectangularUV, written out here from its source: y is the polar axis.
        private static void EveUv(double x, double y, double z, out double u, out double v)
        {
            v = Math.Acos(Math.Max(-1.0, Math.Min(1.0, y))) / Math.PI;
            u = 0.5 + 0.5 / Math.PI * Math.Atan2(x, z);
        }

        private static EveCoverageMath.Blob BlobAt(double latDegrees, double lonDegrees, double radiusM, double peak, ulong seed = 7)
        {
            // Any frame will do for the maths: y is the pole, longitude 0 is +z, east is +x.
            double la = latDegrees * Math.PI / 180, lo = lonDegrees * Math.PI / 180;
            return new EveCoverageMath.Blob
            {
                X = Math.Cos(la) * Math.Sin(lo),
                Y = Math.Sin(la),
                Z = Math.Cos(la) * Math.Cos(lo),
                RadiusM = radiusM,
                Peak = peak,
                Seed = seed
            };
        }

        [TestMethod]
        public void TheMapFollowsEvesFormulaAndTheRoundTripReturnsTheDirection()
        {
            var rng = new Random(11);
            for (int i = 0; i < 2000; i++)
            {
                double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1;
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length < 0.1) continue;
                x /= length; y /= length; z /= length;
                double u, v, eu, ev;
                EveCoverageMath.Uv(x, y, z, out u, out v);
                EveUv(x, y, z, out eu, out ev);
                Assert.AreEqual(eu, u, 1e-12, "u");
                Assert.AreEqual(ev, v, 1e-12, "v");
                double bx, by, bz;
                EveCoverageMath.Direction(u, v, out bx, out by, out bz);
                Assert.AreEqual(x, bx, 1e-9, "x back");
                Assert.AreEqual(y, by, 1e-9, "y back");
                Assert.AreEqual(z, bz, 1e-9, "z back");
            }
            double pu, pv;
            EveCoverageMath.Uv(0, 0, 1, out pu, out pv);
            Assert.AreEqual(0.5, pu, 1e-12, "+z is the middle of the map");
            Assert.AreEqual(0.5, pv, 1e-12, "the equator is the middle row");
            EveCoverageMath.Uv(1, 0, 0, out pu, out pv);
            Assert.AreEqual(0.75, pu, 1e-12, "+x is a quarter turn to the right");
            EveCoverageMath.Uv(0, 1, 0, out pu, out pv);
            Assert.AreEqual(0.0, pv, 1e-12, "y = 1 is the first row");
        }

        [TestMethod]
        public void TheTexelOfACoordinateWrapsInColumnsAndClampsInRows()
        {
            int col, row;
            EveCoverageMath.Texel(0.0, 0.0, out col, out row);
            Assert.AreEqual(0, col);
            Assert.AreEqual(0, row);
            EveCoverageMath.Texel(1.0, 1.0, out col, out row);
            Assert.AreEqual(0, col, "u = 1 is the same column as u = 0");
            Assert.AreEqual(EveCoverageMath.Height - 1, row, "v = 1 is the last row");
            EveCoverageMath.Texel(-0.25, 0.5, out col, out row);
            Assert.AreEqual(EveCoverageMath.Width * 3 / 4, col, "a negative u wraps");
        }

        [TestMethod]
        public void TheFalloffHasItsPeakInTheCoreFallsSmoothlyAndEndsAtTheRadius()
        {
            Assert.AreEqual(1.0, Falloff(0), 1e-12);
            Assert.AreEqual(1.0, Falloff(0.4), 1e-12, "full inside the core");
            Assert.AreEqual(0.0, Falloff(1.0), 1e-12, "none at the radius");
            Assert.AreEqual(0.0, Falloff(1.6), 1e-12);
            double last = 1.0;
            for (double s = 0.4; s <= 1.0; s += 0.02)
            {
                double f = Falloff(s);
                Assert.IsTrue(f <= last + 1e-12, "never rises with the distance: " + s);
                Assert.IsTrue(f >= 0 && f <= 1);
                last = f;
            }
            Assert.IsTrue(EveCoverageMath.Falloff(0.95 * 10000, 10000, -1) > EveCoverageMath.Falloff(0.95 * 10000, 10000, 1), "a positive raggedness moves the rim in");
            Assert.AreEqual(0.0, EveCoverageMath.Falloff(100, 0, 0), "a cell without a radius covers nothing");
        }

        private static double Falloff(double share)
        {
            return EveCoverageMath.Falloff(share * 10000, 10000, 0);
        }

        [TestMethod]
        public void TheRaggedEdgeIsDeterministicAndInRange()
        {
            for (int row = 0; row < 40; row++)
            {
                for (int col = 0; col < 40; col++)
                {
                    double a = EveCoverageMath.Ragged(col, row, 99), b = EveCoverageMath.Ragged(col, row, 99);
                    Assert.AreEqual(a, b, "the same texel gives the same value");
                    Assert.IsTrue(a >= -1.0 && a <= 1.0, "in range: " + a);
                }
            }
            double differ = 0;
            for (int i = 0; i < 100; i++) differ += Math.Abs(EveCoverageMath.Ragged(i, 5, 1) - EveCoverageMath.Ragged(i, 5, 2));
            Assert.IsTrue(differ > 5, "another seed gives another rim");
        }

        [TestMethod]
        public void ACellIsPaintedWithItsPeakAtTheCentreAndNothingBeyondItsReach()
        {
            var data = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            EveCoverageMath.Blob blob = BlobAt(60, 20, 15000, 0.8);
            EveCoverageMath.Rect rect = EveCoverageMath.Paint(data, blob, Kerbin, 0);
            Assert.IsFalse(rect.IsEmpty);
            double u, v;
            EveCoverageMath.Uv(blob.X, blob.Y, blob.Z, out u, out v);
            double centre = EveCoverageMath.Sample(data, u, v);
            Assert.AreEqual(0.8, centre, 0.02, "the peak at the centre");
            // Walk north: the value never rises and ends at zero before 1.25 radii.
            double step = 500.0 / Kerbin * 180 / Math.PI;
            double last = 1.0;
            for (int i = 0; i < 40; i++)
            {
                EveCoverageMath.Blob p = BlobAt(60 + i * step, 20, 1, 1);
                double pu, pv;
                EveCoverageMath.Uv(p.X, p.Y, p.Z, out pu, out pv);
                double value = EveCoverageMath.Sample(data, pu, pv);
                if (i * 500.0 < 0.2 * 15000) Assert.IsTrue(value > 0.7, "the core at " + i * 500 + " m: " + value);
                if (i * 500.0 > 1.25 * 15000) Assert.AreEqual(0.0, value, 1e-9, "nothing at " + i * 500 + " m");
                last = value;
            }
            Assert.AreEqual(0.0, last, 1e-9);
            long outside = 0;
            for (int row = 0; row < EveCoverageMath.Height; row++)
                for (int col = 0; col < EveCoverageMath.Width; col++)
                    if (data[row * EveCoverageMath.Width + col] != 0 && !rect.Contains(col, row)) outside++;
            Assert.AreEqual(0, outside, "nothing is painted outside the rectangle that was returned");
        }

        [TestMethod]
        public void PaintingTwiceGivesTheSameMapAndClearingTheRectangleEmptiesIt()
        {
            var a = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            var b = new byte[a.Length];
            EveCoverageMath.Blob blob = BlobAt(-45, 100, 9000, 1.0, 1234);
            EveCoverageMath.Rect ra = EveCoverageMath.Paint(a, blob, Kerbin, 0);
            EveCoverageMath.Paint(b, blob, Kerbin, 0);
            CollectionAssert.AreEqual(a, b, "deterministic");
            long set = 0;
            foreach (byte x in a) if (x != 0) set++;
            Assert.IsTrue(set > 50, "a 9 km cell covers some texels: " + set);
            EveCoverageMath.Clear(a, ra);
            foreach (byte x in a) Assert.AreEqual(0, x);
        }

        [TestMethod]
        public void ACellOnTheDateLineWrapsAroundTheEdgeOfTheMap()
        {
            var data = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            // u = 0 and u = 1 are the same meridian (longitude 180 degrees): z = -1.
            EveCoverageMath.Blob blob = BlobAt(10, 180, 20000, 1.0);
            EveCoverageMath.Rect rect = EveCoverageMath.Paint(data, blob, Kerbin, 0);
            Assert.IsTrue(rect.Cols < EveCoverageMath.Width, "a cell at 10 degrees of latitude is not the whole width");
            Assert.IsTrue(rect.Col0 + rect.Cols > EveCoverageMath.Width, "the rectangle wraps");
            bool left = false, right = false;
            for (int row = rect.Row0; row < rect.Row0 + rect.Rows; row++)
            {
                if (data[row * EveCoverageMath.Width + 2] != 0) left = true;
                if (data[row * EveCoverageMath.Width + EveCoverageMath.Width - 3] != 0) right = true;
            }
            Assert.IsTrue(left && right, "both sides of the seam hold the cloud");
            double u, v;
            EveCoverageMath.Uv(blob.X, blob.Y, blob.Z, out u, out v);
            Assert.AreEqual(1.0, EveCoverageMath.Sample(data, u, v), 0.02, "the bilinear read across the seam");
        }

        [TestMethod]
        public void ACellNearAPoleTakesTheWholeWidthAndTheSizeIsRightAtTheEquator()
        {
            EveCoverageMath.Rect pole = EveCoverageMath.RectAround(0.02, 0.9998, 0.0, 25000 / Kerbin);
            Assert.AreEqual(EveCoverageMath.Width, pole.Cols, "a circle round the pole spans every column");
            Assert.AreEqual(0, pole.Row0);
            EveCoverageMath.Blob equator = BlobAt(0, 0, 10000, 1);
            EveCoverageMath.Rect rect = EveCoverageMath.RectAround(equator.X, equator.Y, equator.Z, 10000 / Kerbin);
            double texelM = 2 * Math.PI * Kerbin / EveCoverageMath.Width;
            double widthM = rect.Cols * texelM;
            Assert.IsTrue(widthM >= 20000 && widthM < 20000 + 12 * texelM, "the rectangle holds the circle with a small margin: " + widthM);
            Assert.IsTrue(rect.Contains(EveCoverageMath.Width / 2, EveCoverageMath.Height / 2));
            Assert.IsFalse(rect.Contains(0, 0));
            Assert.IsTrue(rect.Row0 >= 0 && rect.Row0 + rect.Rows <= EveCoverageMath.Height, "the rows stay inside the map");
        }

        [TestMethod]
        public void TheLongitudeShiftMovesTheCloudAlongTheMap()
        {
            var data = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            EveCoverageMath.Blob blob = BlobAt(0, 0, 12000, 1.0);
            EveCoverageMath.Paint(data, blob, Kerbin, 0.05);
            double u, v;
            EveCoverageMath.Uv(blob.X, blob.Y, blob.Z, out u, out v);
            Assert.AreEqual(0.0, EveCoverageMath.Sample(data, u, v), 0.01, "the old place is empty");
            Assert.AreEqual(1.0, EveCoverageMath.Sample(data, u + 0.05, v), 0.02, "the cloud is 0.05 of the width along");
        }

        [TestMethod]
        public void OverlappingCellsKeepTheLargerValue()
        {
            var data = new byte[EveCoverageMath.Width * EveCoverageMath.Height];
            EveCoverageMath.Blob strong = BlobAt(30, 30, 15000, 0.9, 1), weak = BlobAt(30, 30, 15000, 0.3, 2);
            EveCoverageMath.Paint(data, strong, Kerbin, 0);
            EveCoverageMath.Paint(data, weak, Kerbin, 0);
            double u, v;
            EveCoverageMath.Uv(strong.X, strong.Y, strong.Z, out u, out v);
            Assert.AreEqual(0.9, EveCoverageMath.Sample(data, u, v), 0.02);
        }

        [TestMethod]
        public void TheSettingIsOnByDefaultAndComesBackFromTheFile()
        {
            string folder = Path.Combine(Path.GetTempPath(), "eve-clouds-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            var saved = SeasonsSettings.Snapshot();
            try
            {
                Assert.IsTrue(SeasonsSettings.StormEveClouds, "on by default");
                SeasonsSettings.StormEveClouds = false;
                SeasonsSettings.Write(file);
                SeasonsSettings.StormEveClouds = true;
                SeasonsSettings.Read(file);
                Assert.IsFalse(SeasonsSettings.StormEveClouds);
                File.WriteAllText(file, "stormEveClouds = maybe\n");
                SeasonsSettings.Read(file);
                Assert.IsFalse(SeasonsSettings.StormEveClouds, "a value that is not a switch is ignored");
                File.WriteAllText(file, "stormEveClouds = True\n");
                SeasonsSettings.Read(file);
                Assert.IsTrue(SeasonsSettings.StormEveClouds);
            }
            finally
            {
                SeasonsSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void TheConfigOfTheLayerNamesTheLayerAndTheCoverageFileExists()
        {
            string root = Repo();
            string folder = Path.Combine(root, "GameData", "ReDefinition", "Seasons", "Clouds");
            string cfg = File.ReadAllText(Path.Combine(folder, "SeasonsStormLayer.cfg"));
            StringAssert.Contains(cfg, "name = " + EveCellLayer.LayerName);
            StringAssert.Contains(cfg, "body = Kerbin");
            StringAssert.Contains(cfg, "minAltitude = " + EveCellLayer.BaseAslM.ToString(System.Globalization.CultureInfo.InvariantCulture));
            StringAssert.Contains(cfg, "speed = 0,0,0");
            Assert.IsFalse(cfg.Contains("particleFieldConfig"), "the layer has no particle field of its own");
            Assert.IsFalse(cfg.Contains("lightningConfig") || cfg.Contains("soundName") || cfg.Contains("dropletsConfig"), "no lightning, sound or droplets");
            Assert.IsFalse(cfg.ToLowerInvariant().Contains("thunder") || cfg.ToLowerInvariant().Contains("cumulonimbus"), "a snow cloud, not a thunderstorm");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "StormCoverage.png")), "the placeholder coverage map");
            StringAssert.Contains(cfg, "ReDefinition/Seasons/Clouds/StormCoverage");
        }

        private static string Repo()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "ReDefinition.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "the repository root");
            return dir;
        }
    }
}
