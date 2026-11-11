using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The pure part of the storm cell inside EVE's own layers: the lobed shape, the composite into the coverage and type maps, the cloud
    // type channel, the swirl of the 2D layer and the vertices that draw it.
    [TestClass]
    public class EveStormMathTests
    {
        private const double Kerbin = 600000;

        private static void Direction(double latDegrees, double lonDegrees, out double x, out double y, out double z)
        {
            Geo.DirectionOf(Geo.ToRad(latDegrees), Geo.ToRad(lonDegrees), out x, out y, out z);
        }

        private static int Lobes(ulong seed, double radiusM, double heading, double ut, EveStormMath.Lobe[] output)
        {
            return EveStormMath.Lobes(seed, radiusM, heading, ut, output);
        }

        [TestMethod]
        public void TheLobesAreTheSameEveryTimeForTheSameInputsAndChangeWithTheSeedAndTheTime()
        {
            var a = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            var b = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            int na = Lobes(42, 12000, 0.7, 1234567.0, a), nb = Lobes(42, 12000, 0.7, 1234567.0, b);
            Assert.AreEqual(na, nb);
            for (int i = 0; i < na; i++) Assert.AreEqual(a[i], b[i], "lobe " + i);
            Lobes(43, 12000, 0.7, 1234567.0, b);
            bool differs = false;
            for (int i = 0; i < na; i++) if (!a[i].Equals(b[i])) differs = true;
            Assert.IsTrue(differs, "another seed gives another cell");
            Lobes(42, 12000, 0.7, 1234567.0 + 3600, b);
            differs = false;
            for (int i = 0; i < na; i++) if (!a[i].Equals(b[i])) differs = true;
            Assert.IsTrue(differs, "the lobes breathe with the time");
        }

        [TestMethod]
        public void ACellHasFiveToSevenLobesAndAMiddleOneAtTheCentre()
        {
            var lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            var seen = new System.Collections.Generic.HashSet<int>();
            for (ulong seed = 1; seed < 400; seed++)
            {
                int n = Lobes(seed, 10000, 0, 0, lobes);
                Assert.IsTrue(n >= 5 && n <= 7, "lobes: " + n);
                seen.Add(n);
                Assert.AreEqual(0.0, lobes[0].EastM);
                Assert.AreEqual(0.0, lobes[0].NorthM);
                Assert.AreEqual(1.0, lobes[0].Weight);
            }
            Assert.IsTrue(seen.Count >= 2, "the number varies with the seed");
        }

        [TestMethod]
        public void ALobeArrayThatIsTooSmallOrARadiusThatIsNotPositiveGivesWhatFits()
        {
            Assert.AreEqual(0, Lobes(1, 10000, 0, 0, null));
            Assert.AreEqual(0, Lobes(1, 0, 0, 0, new EveStormMath.Lobe[8]));
            Assert.AreEqual(0, Lobes(1, double.NaN, 0, 0, new EveStormMath.Lobe[8]));
            Assert.AreEqual(3, Lobes(1, 10000, 0, 0, new EveStormMath.Lobe[3]));
            var lobes = new EveStormMath.Lobe[8];
            Assert.IsTrue(Lobes(1, 10000, 0, double.NaN, lobes) > 0, "a time that is not a number does not break it");
        }

        [TestMethod]
        public void TheLobesAreStretchedAlongTheWindByOneAndAHalfTimesOrMore()
        {
            var lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            double along = 0, across = 0;
            int cells = 0;
            for (ulong seed = 1; seed < 200; seed++)
            {
                double stretch = EveStormMath.Stretch(seed);
                Assert.IsTrue(stretch >= EveStormMath.StretchMin && stretch <= EveStormMath.StretchMax, "stretch " + stretch);
                // The wind blows toward the east: heading 90 degrees.
                int n = Lobes(seed, 10000, Math.PI / 2, 0, lobes);
                double maxEast = 0, maxNorth = 0;
                for (int i = 0; i < n; i++)
                {
                    maxEast = Math.Max(maxEast, Math.Abs(lobes[i].EastM));
                    maxNorth = Math.Max(maxNorth, Math.Abs(lobes[i].NorthM));
                }
                along += maxEast;
                across += maxNorth;
                cells++;
            }
            Assert.IsTrue(along / across > 1.5, "the cells are longer along the wind than across it: " + along / cells + " against " + across / cells);
        }

        [TestMethod]
        public void NoLobeReachesFartherThanTheBoundForAnySeedHeadingAndTime()
        {
            var lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            for (ulong seed = 1; seed < 300; seed++)
            {
                for (int h = 0; h < 8; h++)
                {
                    double heading = h * Math.PI / 4;
                    double ut = seed * 9973.0 + h * 1000;
                    int n = Lobes(seed, 10000, heading, ut, lobes);
                    for (int i = 0; i < n; i++)
                    {
                        double reach = Math.Sqrt(lobes[i].EastM * lobes[i].EastM + lobes[i].NorthM * lobes[i].NorthM) + lobes[i].RadiusM * EveCoverageMath.ReachShare;
                        Assert.IsTrue(reach <= EveStormMath.MaxReachFactor * 10000, "reach " + reach + " (seed " + seed + ", lobe " + i + ")");
                        Assert.IsTrue(lobes[i].Weight > 0 && lobes[i].Weight <= 1.0);
                        Assert.IsTrue(lobes[i].RadiusM > 0);
                    }
                }
            }
        }

        [TestMethod]
        public void ACellGrowsFromAFractionOfItsSizeToItsFullSize()
        {
            Assert.AreEqual(EveStormMath.GrowthStart, EveStormMath.GrowthScale(0.0), 1e-9);
            Assert.AreEqual(1.0, EveStormMath.GrowthScale(0.6), 1e-9);
            Assert.AreEqual(1.0, EveStormMath.GrowthScale(1.0), 1e-9);
            double last = 0;
            for (double s = 0; s <= 0.6; s += 0.05)
            {
                Assert.IsTrue(EveStormMath.GrowthScale(s) >= last);
                last = EveStormMath.GrowthScale(s);
            }
        }

        private static byte[] PaintCell(int width, int height, double lat, double lon, double radiusM, double peak, ulong seed, out EveCoverageMath.Rect rect)
        {
            double cx, cy, cz;
            Direction(lat, lon, out cx, out cy, out cz);
            var lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            int n = Lobes(seed, radiusM, 1.0, 0, lobes);
            rect = EveStormMath.CellRect(cx, cy, cz, radiusM, Kerbin, width, height);
            var mask = new byte[EveStormMath.MaskSize(rect)];
            EveStormMath.PaintMask(mask, rect, width, height, cx, cy, cz, Kerbin, lobes, n, peak, seed);
            return mask;
        }

        [TestMethod]
        public void TheMaskHasAPlateauInTheMiddleAndNothingAtTheEdgeOfItsRectangle()
        {
            EveCoverageMath.Rect rect;
            byte[] mask = PaintCell(4096, 4096, 55, 20, 12000, 0.9, 5, out rect);
            Assert.IsFalse(rect.IsEmpty);
            int centreRow = rect.Rows / 2, centreCol = rect.Cols / 2;
            Assert.IsTrue(mask[centreRow * rect.Cols + centreCol] >= 0.9 * 255 * 0.72, "the plateau at the centre: " + mask[centreRow * rect.Cols + centreCol]);
            for (int k = 0; k < rect.Cols; k++)
            {
                Assert.AreEqual(0, mask[k], "the first row is empty");
                Assert.AreEqual(0, mask[(rect.Rows - 1) * rect.Cols + k], "the last row is empty");
            }
            for (int r = 0; r < rect.Rows; r++)
            {
                Assert.AreEqual(0, mask[r * rect.Cols], "the first column is empty");
                Assert.AreEqual(0, mask[r * rect.Cols + rect.Cols - 1], "the last column is empty");
            }
        }

        [TestMethod]
        public void TheMaskIsTheSameEveryTimeItIsPaintedAndFollowsTheWindDirection()
        {
            EveCoverageMath.Rect r1, r2;
            byte[] a = PaintCell(4096, 2048, 40, 100, 10000, 0.8, 9, out r1);
            byte[] b = PaintCell(4096, 2048, 40, 100, 10000, 0.8, 9, out r2);
            CollectionAssert.AreEqual(a, b);
            Assert.AreEqual(r1.Cols, r2.Cols);
            int painted = 0;
            foreach (byte value in a) if (value > 0) painted++;
            Assert.IsTrue(painted > 100, "painted " + painted);
        }

        [TestMethod]
        public void ACellAcrossTheDateLineIsPaintedOnBothSidesOfTheMap()
        {
            const int width = 1024, height = 1024;
            EveCoverageMath.Rect rect;
            byte[] mask = PaintCell(width, height, 20, 179.9, 20000, 1.0, 3, out rect);
            Assert.IsFalse(rect.IsEmpty);
            Assert.IsTrue(rect.Col0 + rect.Cols > width, "the rectangle wraps: " + rect.Col0 + " + " + rect.Cols);
            var map = new byte[width * height];
            var type = new byte[width * height];
            EveStormMath.Composite(map, type, 255, mask, rect, width);
            int left = 0, right = 0;
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < 64; c++) if (map[r * width + c] > 0) left++;
                for (int c = width - 64; c < width; c++) if (map[r * width + c] > 0) right++;
            }
            Assert.IsTrue(left > 0 && right > 0, "both sides: " + left + " and " + right);
        }

        [TestMethod]
        public void AMaskThatIsTooSmallIsRefused()
        {
            EveCoverageMath.Rect rect;
            PaintCell(1024, 1024, 30, 30, 10000, 1.0, 1, out rect);
            double cx, cy, cz;
            Direction(30, 30, out cx, out cy, out cz);
            var lobes = new EveStormMath.Lobe[EveStormMath.MaxLobes];
            Assert.ThrowsException<ArgumentException>(() => EveStormMath.PaintMask(new byte[3], rect, 1024, 1024, cx, cy, cz, Kerbin, lobes, 3, 1.0, 1));
        }

        [TestMethod]
        public void TheScreenBlendNeverLowersAndKeepsTheLayersOwnTextureWhereTheMaskIsThin()
        {
            for (int layer = 0; layer <= 255; layer += 5)
            {
                Assert.AreEqual(layer, EveStormMath.Screen((byte)layer, 0));
                Assert.AreEqual(255, EveStormMath.Screen((byte)layer, 255));
                for (int mask = 0; mask <= 255; mask += 15)
                {
                    int v = EveStormMath.Screen((byte)layer, (byte)mask);
                    Assert.IsTrue(v >= layer && v >= mask, "screen(" + layer + ", " + mask + ") = " + v);
                }
            }
            Assert.AreEqual(200, EveStormMath.Screen(0, 200));
            Assert.IsTrue(EveStormMath.Screen(100, 200) > EveStormMath.Screen(150, 200) - 60 && EveStormMath.Screen(100, 200) < EveStormMath.Screen(150, 200),
                "a denser layer stays denser");
        }

        [TestMethod]
        public void TheCloudTypeTurnsToTheTargetAsTheMaskGrows()
        {
            Assert.AreEqual(50, EveStormMath.BlendType(50, 0, 255), "no cell, no change");
            Assert.AreEqual(50, EveStormMath.BlendType(50, 30, 255), "below the start it is the layer's own");
            Assert.AreEqual(255, EveStormMath.BlendType(50, 255, 255));
            Assert.AreEqual(0, EveStormMath.BlendType(200, 255, 0));
            int last = 0;
            for (int m = 0; m <= 255; m += 5)
            {
                int t = EveStormMath.BlendType(0, (byte)m, 255);
                Assert.IsTrue(t >= last, "monotone");
                last = t;
            }
        }

        [TestMethod]
        public void TheCloudTypeValueIsTheIndexOverCountMinusOneWithTheEndsExact()
        {
            Assert.AreEqual(0.0, EveStormMath.CloudTypeValue(0, 6));
            Assert.AreEqual(1.0, EveStormMath.CloudTypeValue(5, 6));
            Assert.AreEqual(1.0, EveStormMath.CloudTypeValue(4, 5), "Polar-snow is the last of Kerbin-Weather-1's five types");
            Assert.AreEqual(0.5, EveStormMath.CloudTypeValue(2, 5), 1e-12);
            Assert.AreEqual(0.0, EveStormMath.CloudTypeValue(0, 1));
            Assert.AreEqual((byte)255, EveStormMath.CloudTypeByte(4, 5));
            Assert.AreEqual((byte)0, EveStormMath.CloudTypeByte(0, 6));
            Assert.AreEqual((byte)128, EveStormMath.CloudTypeByte(2, 5));
        }

        [TestMethod]
        public void TheTypesOfTheStockLayersAreFoundByName()
        {
            string[] weather = { "Fog", "Rain", "Thunder", "Polar-fog", "Polar-snow" };
            string[] baseLayer = { "Stratus", "Cumulus", "Congestus0", "Congestus 1", "Congestus 2", "Congestus 3" };
            string[] snow = { "Polar-snow", "Snow" };
            string[] stratus = { "Stratus" };
            Assert.AreEqual(4, EveStormMath.FindType(weather, snow));
            Assert.AreEqual(-1, EveStormMath.FindType(baseLayer, snow));
            Assert.AreEqual(0, EveStormMath.FindType(baseLayer, stratus));
            Assert.AreEqual(4, EveStormMath.FindType(new[] { "x", null, "y", "z", "POLAR-SNOW" }, snow), "case does not matter");
            Assert.AreEqual(1, EveStormMath.FindType(new[] { "Snow", "Polar-snow" }, snow), "the first wanted name wins");
            Assert.AreEqual(-1, EveStormMath.FindType(new[] { "SnowParticles" }, snow), "a name is matched whole");
            Assert.AreEqual(-1, EveStormMath.FindType(null, snow));
        }

        [TestMethod]
        public void TheStockConfigsTypesAreWhatTheLayerSelectionAssumes()
        {
            string clouds = Path.Combine(Environment.GetEnvironmentVariable("KSP_ROOT") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
                "GameData", "StockVolumetricClouds", "Clouds", "clouds.cfg");
            if (!File.Exists(clouds)) Assert.Inconclusive("the StockVolumetricClouds config is not installed here");
            string text = File.ReadAllText(clouds);
            int weather = text.IndexOf("name = Kerbin-Weather-1", StringComparison.Ordinal);
            int snow = text.IndexOf("typeName = Polar-snow", weather, StringComparison.Ordinal);
            Assert.IsTrue(weather > 0 && snow > weather, "Kerbin-Weather-1 has a Polar-snow type");
            int next = text.IndexOf("name = Kerbin-weather-2", weather, StringComparison.Ordinal);
            Assert.IsTrue(snow < next, "and it is in that layer");
            int baseLayer = text.IndexOf("name = Kerbin-base-layer", StringComparison.Ordinal);
            int stratus = text.IndexOf("typeName = Stratus", baseLayer, StringComparison.Ordinal);
            Assert.IsTrue(baseLayer >= 0 && stratus > baseLayer && stratus < weather, "Kerbin-base-layer has the Stratus type");
        }

        [TestMethod]
        public void CompositingAndRestoringLeavesTheOriginalExactlyWhereNoCellIs()
        {
            const int width = 1024, height = 1024;
            EveCoverageMath.Rect rect;
            byte[] mask = PaintCell(width, height, 60, -40, 15000, 0.9, 17, out rect);
            var original = new byte[width * height];
            var type = new byte[width * height];
            for (int i = 0; i < original.Length; i++) { original[i] = (byte)(i * 31 % 200); type[i] = (byte)(i * 17 % 255); }
            byte[] work = (byte[])original.Clone(), workType = (byte[])type.Clone();
            EveStormMath.Composite(work, workType, 255, mask, rect, width);
            bool raised = false, turned = false;
            for (int i = 0; i < work.Length; i++)
            {
                Assert.IsTrue(work[i] >= original[i], "never lowered");
                if (work[i] > original[i]) raised = true;
                if (workType[i] != type[i]) turned = true;
            }
            Assert.IsTrue(raised && turned);
            EveStormMath.Restore(work, original, rect, width);
            EveStormMath.Restore(workType, type, rect, width);
            CollectionAssert.AreEqual(original, work);
            CollectionAssert.AreEqual(type, workType);
        }

        [TestMethod]
        public void TheTypeMapOnlyTurnsInsideTheCellAndTheCoverageOnlyRisesThere()
        {
            const int width = 1024, height = 1024;
            EveCoverageMath.Rect rect;
            byte[] mask = PaintCell(width, height, 50, 0, 12000, 1.0, 4, out rect);
            var coverage = new byte[width * height];
            var type = new byte[width * height];
            EveStormMath.Composite(coverage, type, 200, mask, rect, width);
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < width; c++)
                {
                    bool inside = false;
                    if (r >= rect.Row0 && r < rect.Row0 + rect.Rows)
                    {
                        int d = (c - rect.Col0 + width) % width;
                        inside = d < rect.Cols;
                    }
                    if (!inside) Assert.AreEqual(0, coverage[r * width + c] + type[r * width + c], "outside the rectangle at " + c + ", " + r);
                }
            }
        }

        [TestMethod]
        public void OffsetMovesAPointByTheDistanceOnTheSphereAlongEastAndNorth()
        {
            double x, y, z;
            double cx, cy, cz;
            Direction(0, 0, out cx, out cy, out cz);
            EveStormMath.Offset(cx, cy, cz, Math.PI / 4 * Kerbin, 0, Kerbin, out x, out y, out z);
            Assert.AreEqual(45.0, Geo.LongitudeOf(x, z), 1e-6, "east raises the longitude");
            Assert.AreEqual(0.0, Geo.LatitudeOf(y), 1e-6);
            EveStormMath.Offset(cx, cy, cz, 0, Math.PI / 6 * Kerbin, Kerbin, out x, out y, out z);
            Assert.AreEqual(30.0, Geo.LatitudeOf(y), 1e-6, "north raises the latitude");
            EveStormMath.Offset(cx, cy, cz, 0, 0, Kerbin, out x, out y, out z);
            Assert.AreEqual(1.0, x * cx + y * cy + z * cz, 1e-12);
            Direction(70, -120, out cx, out cy, out cz);
            EveStormMath.Offset(cx, cy, cz, 5000, 3000, Kerbin, out x, out y, out z);
            Assert.AreEqual(1.0, Math.Sqrt(x * x + y * y + z * z), 1e-9);
            double dot = x * cx + y * cy + z * cz;
            Assert.AreEqual(Math.Sqrt(5000.0 * 5000 + 3000.0 * 3000) / Kerbin, Math.Acos(dot), 1e-6);
        }

        [TestMethod]
        public void TheSwirlHasACoreTwoArmsAndAtLeastTheMinimumSize()
        {
            var puffs = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            int n = EveStormMath.Swirl(7, 3000, 0.9, true, 0.5, puffs);
            Assert.AreEqual(EveStormMath.FootprintDiscs + 2 + EveStormMath.Arms * EveStormMath.PuffsPerArm, n, "an erasing footprint, a body, white arms, a dark centre");
            for (int i = 0; i < EveStormMath.FootprintDiscs; i++)
            {
                Assert.IsTrue(puffs[i].Erase, "the footprint is drawn first and erases");
                Assert.IsTrue(puffs[i].RadiusM > 0.7 * EveStormMath.SwirlRadiusM(3000), "it covers the arms");
            }
            for (int i = EveStormMath.FootprintDiscs; i < n; i++) Assert.IsFalse(puffs[i].Erase, "everything after it paints");
            Assert.AreEqual(EveStormMath.MinSwirlRadiusM, EveStormMath.SwirlRadiusM(3000), "a small cell is drawn at the minimum size");
            Assert.AreEqual(40000 * 1.1, EveStormMath.SwirlRadiusM(40000), 1e-9, "a large one at its own");
            double s = EveStormMath.SwirlRadiusM(3000);
            double reach = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.IsTrue(puffs[i].RadiusM > 0 && puffs[i].Alpha >= 0 && puffs[i].Alpha <= 1.0 && puffs[i].Grey >= 0 && puffs[i].Grey <= 1.0);
                reach = Math.Max(reach, Math.Sqrt(puffs[i].EastM * puffs[i].EastM + puffs[i].NorthM * puffs[i].NorthM) + puffs[i].RadiusM * EveCoverageMath.ReachShare);
            }
            Assert.IsTrue(reach <= EveStormMath.SwirlReachFactor * s, "reach " + reach + " against " + EveStormMath.SwirlReachFactor * s);
            Assert.IsTrue(reach > 1.0 * s, "the arms reach beyond the display radius");
            Assert.IsTrue(puffs[n - 1].Grey < puffs[EveStormMath.FootprintDiscs].Grey, "the last puff is the darker core");
            Assert.IsTrue(puffs[n - 1].Cool > 0, "and it is blue-grey");
            Assert.IsTrue(EveStormMath.MinSwirlRadiusM * 2 >= 40000, "the swirl is at least 40 km across: 10 pixels from 1000 km");
            Assert.IsTrue(reach <= EveStormMath.SwirlReachFactor * s);
        }

        [TestMethod]
        public void TheSwirlIsTheSameEveryTimeAndWindsTheOtherWayInTheSouth()
        {
            var a = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            var b = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            int na = EveStormMath.Swirl(11, 12000, 0.8, true, 0.3, a), nb = EveStormMath.Swirl(11, 12000, 0.8, true, 0.3, b);
            Assert.AreEqual(na, nb);
            for (int i = 0; i < na; i++) Assert.AreEqual(a[i], b[i]);
            EveStormMath.Swirl(11, 12000, 0.8, false, 0.3, b);
            // The turn of the first arm: the cross product of the first and last puff of the arm about the centre.
            int first = EveStormMath.FootprintDiscs + 1, last = first + 4;
            double crossNorth = a[first].EastM * a[last].NorthM - a[first].NorthM * a[last].EastM;
            double crossSouth = b[first].EastM * b[last].NorthM - b[first].NorthM * b[last].EastM;
            Assert.IsTrue(crossNorth * crossSouth < 0, "the arms turn the other way: " + crossNorth + " and " + crossSouth);
            Assert.AreEqual(0, EveStormMath.Swirl(1, 0, 0.5, true, 0, a));
            Assert.AreEqual(0, EveStormMath.Swirl(1, 5000, 0.5, true, 0, new EveStormMath.Puff[3]));
        }

        [TestMethod]
        public void AWeakCellsSwirlIsFainterThanAStrongOnes()
        {
            var weak = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            var strong = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            EveStormMath.Swirl(3, 12000, 0.03, true, 0, weak);
            EveStormMath.Swirl(3, 12000, 1.0, true, 0, strong);
            Assert.IsTrue(weak[EveStormMath.FootprintDiscs].Alpha < strong[EveStormMath.FootprintDiscs].Alpha);
            Assert.IsTrue(weak[0].Alpha < strong[0].Alpha);
        }

        [TestMethod]
        public void ThePatternHasSixDiscsOfEveryKindAndTheColourFollowsGreyAndCool()
        {
            var puffs = new EveStormMath.Puff[EveStormMath.MaxPuffs];
            Assert.AreEqual(6, EveStormMath.Pattern(25000, puffs));
            Assert.AreEqual(0, EveStormMath.Pattern(0, puffs));
            int erasing = 0;
            for (int i = 0; i < 6; i++) if (puffs[i].Erase) erasing++;
            Assert.AreEqual(1, erasing);
            double r, g, b;
            EveStormMath.Colour(new EveStormMath.Puff { Grey = 0.5 }, out r, out g, out b);
            Assert.AreEqual(0.5, r, 1e-9);
            Assert.AreEqual(0.5, b, 1e-9);
            EveStormMath.Colour(new EveStormMath.Puff { Grey = 0.5, Cool = 1 }, out r, out g, out b);
            Assert.IsTrue(b > r && b > g, "cool is blue: " + r + "," + g + "," + b);
            EveStormMath.Colour(new EveStormMath.Puff { Grey = 0.5, Cool = -1 }, out r, out g, out b);
            Assert.IsTrue(r > g && r > b && g < 0.1, "warm is red: " + r + "," + g + "," + b);
        }

        [TestMethod]
        public void TouchingRectanglesAreUnitedAcrossTheDateLineAndApartOnesAreNot()
        {
            const int width = 1000;
            var a = new EveCoverageMath.Rect { Col0 = 100, Cols = 50, Row0 = 10, Rows = 20 };
            var b = new EveCoverageMath.Rect { Col0 = 140, Cols = 50, Row0 = 20, Rows = 30 };
            EveCoverageMath.Rect u;
            Assert.IsTrue(EveStormMath.TryUnion(a, b, width, out u));
            Assert.AreEqual(100, u.Col0);
            Assert.AreEqual(90, u.Cols);
            Assert.AreEqual(10, u.Row0);
            Assert.AreEqual(40, u.Rows);
            Assert.IsTrue(EveStormMath.TryUnion(b, a, width, out u), "the order does not matter");
            Assert.AreEqual(100, u.Col0);
            Assert.AreEqual(90, u.Cols);
            Assert.IsFalse(EveStormMath.TryUnion(a, new EveCoverageMath.Rect { Col0 = 400, Cols = 50, Row0 = 10, Rows = 20 }, width, out u), "apart in columns");
            Assert.IsFalse(EveStormMath.TryUnion(a, new EveCoverageMath.Rect { Col0 = 120, Cols = 10, Row0 = 200, Rows = 20 }, width, out u), "apart in rows");
            var west = new EveCoverageMath.Rect { Col0 = 970, Cols = 40, Row0 = 0, Rows = 10 };
            var east = new EveCoverageMath.Rect { Col0 = 5, Cols = 30, Row0 = 0, Rows = 10 };
            Assert.IsTrue(EveStormMath.TryUnion(west, east, width, out u), "across the date line");
            Assert.AreEqual(970, u.Col0);
            Assert.AreEqual(65, u.Cols);
        }

        [TestMethod]
        public void MergedMasksKeepTheLargerValueAndTheRectangleCompositeEqualsTheMapComposite()
        {
            const int width = 64, height = 32;
            var first = new EveCoverageMath.Rect { Col0 = 10, Cols = 6, Row0 = 4, Rows = 4 };
            var second = new EveCoverageMath.Rect { Col0 = 13, Cols = 6, Row0 = 6, Rows = 4 };
            EveCoverageMath.Rect union;
            Assert.IsTrue(EveStormMath.TryUnion(first, second, width, out union));
            var maskA = new byte[24];
            var maskB = new byte[24];
            for (int i = 0; i < 24; i++) { maskA[i] = 100; maskB[i] = (byte)(i % 2 == 0 ? 50 : 200); }
            var merged = new byte[EveStormMath.MaskSize(union)];
            EveStormMath.MergeMax(merged, union, maskA, first, width);
            EveStormMath.MergeMax(merged, union, maskB, second, width);
            Assert.AreEqual(100, merged[0], "only the first covers its corner");
            Assert.AreEqual(200, merged[(7 - union.Row0) * union.Cols + (14 - union.Col0)], "the larger wins in the overlap");
            // The rectangle form gives what the whole-map form gives inside the rectangle.
            var map = new byte[width * height];
            var type = new byte[width * height];
            for (int i = 0; i < map.Length; i++) { map[i] = (byte)(i * 7); type[i] = (byte)(i * 3); }
            var rectMap = new byte[merged.Length];
            var rectType = new byte[merged.Length];
            for (int r = 0; r < union.Rows; r++)
            {
                for (int k = 0; k < union.Cols; k++)
                {
                    rectMap[r * union.Cols + k] = map[(union.Row0 + r) * width + union.Col0 + k];
                    rectType[r * union.Cols + k] = type[(union.Row0 + r) * width + union.Col0 + k];
                }
            }
            EveStormMath.Composite(map, type, 77, merged, union, width);
            EveStormMath.CompositeRect(rectMap, rectType, 77, merged, union);
            for (int r = 0; r < union.Rows; r++)
            {
                for (int k = 0; k < union.Cols; k++)
                {
                    Assert.AreEqual(map[(union.Row0 + r) * width + union.Col0 + k], rectMap[r * union.Cols + k]);
                    Assert.AreEqual(type[(union.Row0 + r) * width + union.Col0 + k], rectType[r * union.Cols + k]);
                }
            }
        }

        [TestMethod]
        public void ColumnsCutAStripOutOfARectangleAndTheSignatureNoticesATexelMove()
        {
            var data = new byte[3 * 4];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
            var strip = new byte[2 * 3];
            EveStormMath.Columns(data, 4, 3, 1, 2, strip);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 5, 6, 9, 10 }, strip);
            ulong s = EveStormMath.Signature(0, 5, 0.25, 0.5, 6000, 0.8, 4096, 2048);
            Assert.AreEqual(s, EveStormMath.Signature(0, 5, 0.25, 0.5, 6000, 0.8, 4096, 2048));
            Assert.AreEqual(s, EveStormMath.Signature(0, 5, 0.25 + 0.2 / 4096, 0.5, 6000, 0.8, 4096, 2048), "a move inside the texel");
            Assert.AreNotEqual(s, EveStormMath.Signature(0, 5, 0.25 + 1.5 / 4096, 0.5, 6000, 0.8, 4096, 2048), "a texel on");
            Assert.AreNotEqual(s, EveStormMath.Signature(0, 5, 0.25, 0.5, 6000, 0.6, 4096, 2048), "the strength changed");
            Assert.AreNotEqual(s, EveStormMath.Signature(0, 6, 0.25, 0.5, 6000, 0.8, 4096, 2048), "another cell");
        }

        [TestMethod]
        public void TheMaskPlateauIsNearlyFullAndItsRimIsNarrow()
        {
            EveCoverageMath.Rect rect;
            byte[] mask = PaintCell(4096, 4096, 55, 20, 12000, 1.0, 5, out rect);
            int full = 0, partial = 0;
            foreach (byte value in mask)
            {
                if (value >= 230) full++;
                else if (value > 0) partial++;
            }
            Assert.IsTrue(full > 2.5 * partial, "full " + full + " against rim " + partial);
            Assert.AreEqual(1.0, EveCoverageMath.Falloff(0, 1000, 0, EveStormMath.CoreShare), 1e-12);
            Assert.AreEqual(0.0, EveCoverageMath.Falloff(1000, 1000, 0, EveStormMath.CoreShare), 1e-12);
            Assert.AreEqual(EveCoverageMath.Falloff(300, 1000, 0.3), EveCoverageMath.Falloff(300, 1000, 0.3, EveCoverageMath.CoreShare));
        }

        [TestMethod]
        public void TheFanHasACentreOfFullOpacityAndARimOfNone()
        {
            const int segments = 20, rings = 4;
            var u = new double[1 + segments * rings];
            var v = new double[u.Length];
            var alpha = new double[u.Length];
            double cx, cy, cz;
            Direction(50, 10, out cx, out cy, out cz);
            int n = EveStormMath.FanVertices(cx, cy, cz, 0.03, 5, segments, rings, u, v, alpha);
            Assert.AreEqual(1 + segments * rings, n);
            Assert.AreEqual(1.0, alpha[0]);
            for (int i = 1 + (rings - 1) * segments; i < n; i++) Assert.AreEqual(0.0, alpha[i], 1e-9, "the outer ring is clear");
            for (int i = 0; i < n; i++)
            {
                Assert.IsTrue(Math.Abs(u[i] - u[0]) < 0.25 && Math.Abs(v[i] - v[0]) < 0.25, "near the centre");
                Assert.IsTrue(v[i] >= 0 && v[i] <= 1);
                Assert.IsTrue(alpha[i] >= 0 && alpha[i] <= 1);
            }
            int[] indices = new int[(segments + (rings - 1) * segments * 2) * 3];
            int m = EveStormMath.FanIndices(segments, rings, indices);
            Assert.AreEqual(indices.Length, m);
            foreach (int index in indices) Assert.IsTrue(index >= 0 && index < n);
            Assert.AreEqual(0, EveStormMath.FanVertices(cx, cy, cz, 0.03, 5, 2, rings, u, v, alpha), "a fan needs three segments");
            Assert.AreEqual(0, EveStormMath.FanVertices(cx, cy, cz, 0.03, 5, segments, rings, new double[3], v, alpha), "and room");
        }

        [TestMethod]
        public void AFanAcrossTheDateLineIsUnwrappedAroundItsCentre()
        {
            const int segments = 20, rings = 3;
            var u = new double[1 + segments * rings];
            var v = new double[u.Length];
            var alpha = new double[u.Length];
            double cx, cy, cz;
            Direction(10, 179.8, out cx, out cy, out cz);
            EveStormMath.FanVertices(cx, cy, cz, 0.02, 1, segments, rings, u, v, alpha);
            double min = double.MaxValue, max = double.MinValue;
            foreach (double value in u) { min = Math.Min(min, value); max = Math.Max(max, value); }
            Assert.IsTrue(max > 1.0, "a vertex lies past the edge of the map: " + max);
            Assert.IsTrue(max - min < 0.1, "and the fan is not torn across the map: " + (max - min));
        }

        [TestMethod]
        public void TheStormCloudSettingIsExistingByDefaultAndComesBackFromTheFile()
        {
            string folder = Path.Combine(Path.GetTempPath(), "storm-clouds-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            var saved = AtmospheresSettings.Snapshot();
            try
            {
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(AtmospheresSettings.StormCloudModes.Existing, AtmospheresSettings.StormClouds);
                AtmospheresSettings.StormClouds = AtmospheresSettings.StormCloudModes.Own;
                AtmospheresSettings.Write(file);
                AtmospheresSettings.StormClouds = AtmospheresSettings.StormCloudModes.Existing;
                AtmospheresSettings.Read(file);
                Assert.AreEqual(AtmospheresSettings.StormCloudModes.Own, AtmospheresSettings.StormClouds);
                File.WriteAllText(file, "stormClouds = nonsense\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(AtmospheresSettings.StormCloudModes.Own, AtmospheresSettings.StormClouds, "a name that is none is ignored");
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
