using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The sky drawn from the weather map (MapSkyRegions, master plan section 34) on single texels of Kerbin's shipped layers: clear where the map is clear, the heap on the lattice,
    // capped heap and unsaturated sheets as connected stratocumulus, saturated levels in their layers, deep heap in the tall layer, and the calibrated rule's noise as the proposal's script.
    [TestClass]
    public class MapSkyRegionsTests
    {
        private const double H = 10500.0;
        // A sheet amount of 0.7 as the layers' byte.
        private static readonly byte Seventy = (byte)Math.Round(0.7f * 255.0, MidpointRounding.ToEven);

        private static MapSkyRegions.Inputs One(float heap, float sheet, double baseM, double topM, double stableM, float rhLow = 0.5f, float rhMid = 0.5f, float rhHigh = 0.5f,
                                                float deck = 0f, float reach = 0f, float precip = 0f)
        {
            return new MapSkyRegions.Inputs
            {
                Width = 1, Height = 1, Cover = new[] { heap + sheet }, Heap = new[] { heap }, Sheet = new[] { sheet }, Deck = new[] { deck }, BaseShare = new[] { (float)(baseM / H) },
                TopShare = new[] { (float)(topM / H) }, StableShare = new[] { (float)(stableM / H) }, RhLow = new[] { rhLow }, RhMid = new[] { rhMid }, RhHigh = new[] { rhHigh },
                Descent = new[] { 0.2f }, Instability = new[] { 0.5f }, Uplift = new[] { 0.1f }, Reach = new[] { reach }, PrecipLevel = new[] { precip }, SurfaceTempK = new[] { 290f },
                TropopauseM = H, LapseKPerM = 0.00711, DayS = 21549.425, Ut = 1486800.0
            };
        }

        private static CloudLayerDef[] Layers()
        {
            return TypeMixFixture.ShippedLayers();
        }

        // Owner 2026-11-23: the sheet rule is B (2) and the depth rule a (1, the field's depth: the more realistic) by default (the other tests here name their rule).
        [TestMethod]
        public void TheOwnersRulesAreTheDefaults()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9200101);
            Assert.AreEqual(2, MapSkyRegions.SheetRule);
            Assert.AreEqual(1, MapSkyRegions.DepthRule);
        }

        private static MapSkyRegions.Parts Plan(MapSkyRegions.Inputs m, int sheetRule = 1, int depthRule = 1)
        {
            CloudLayerDef[] l = Layers();
            return MapSkyRegions.Plan(m, l[1], l[2], sheetRule, depthRule);
        }

        private static void Draw(MapSkyRegions.Parts p, int layer, out byte cover, out byte type)
        {
            CloudLayerDef[] l = Layers();
            var c = new byte[1];
            var t = new byte[1];
            Assert.IsTrue(MapSkyRegions.Layer(l[layer], WeatherMapSky.RoleOf(l[layer]), p, new byte[1], new byte[1], new byte[1], c, t));
            cover = c[0];
            type = t[0];
        }

        [TestMethod]
        public void AClearMapDrawsNothing()
        {
            MapSkyRegions.Parts p = Plan(One(0f, 0f, 800, 800, 1500, 1f, 1f, 1f));
            for (int l = 0; l < 4; l++)
            {
                byte c, t;
                Draw(p, l, out c, out t);
                Assert.AreEqual(0, c, "layer " + l);
            }
        }

        [TestMethod]
        public void UncappedHeapIsCumulusOnTheLatticeWithItsTypeFromTheDepth()
        {
            CloudLayerDef cu = Layers()[1];
            MapSkyRegions.Parts thin = Plan(One(0.4f, 0f, 800, 1000, 1800)), deep = Plan(One(0.4f, 0f, 800, 4800, 6000));
            Assert.AreEqual(0.4f, thin.Lattice[0], 1e-6f);
            Assert.AreEqual(0f, thin.Sc[0]);
            byte c, tThin, tDeep;
            Draw(thin, 1, out c, out tThin);
            Assert.AreEqual(102, c);
            Draw(deep, 1, out c, out tDeep);
            float step = 1f / (cu.Types.Count - 1);
            Assert.IsTrue(tThin / 255f >= step - 0.01f, "the thinnest lattice type is humilis, not the sheet: " + tThin);
            Assert.IsTrue(tDeep > tThin, "deeper heap, taller type: " + tThin + " -> " + tDeep);
            Assert.IsTrue(tDeep / 255f <= MapSkyRegions.HeapLast(cu) * step + 0.01f, "never the alto sheet type");
        }

        [TestMethod]
        public void CappedHeapIsAConnectedStratocumulusSheetByTheDepthRule()
        {
            // stable layer below the base: capped
            MapSkyRegions.Parts a = Plan(One(0.6f, 0f, 1200, 1200, 900), 1, 1), b = Plan(One(0.6f, 0f, 1200, 1200, 900), 1, 2);
            Assert.AreEqual(0.6f, a.Sc[0], 1e-6f, "rule a: above the lattice's fair share all of it is sheet");
            Assert.AreEqual(0f, a.Lattice[0], 1e-6f);
            MapSkyRegions.Parts aLow = Plan(One(0.1f, 0f, 1200, 1200, 900), 1, 1);
            Assert.AreEqual(0.1f * 0.4f, aLow.Sc[0], 1e-6f, "rule a: a small capped heap is mostly lattice");
            Assert.AreEqual(0.06f, aLow.Lattice[0], 1e-6f);
            Assert.AreEqual(0.6f, b.Sc[0], 1e-6f, "rule b: capped means sheet");
            byte c, t;
            Draw(a, 1, out c, out t);
            Assert.AreEqual(0, t, "the sheet type (Stratocumulus, the first)");
            // a cell is never capped
            MapSkyRegions.Parts cell = Plan(One(0.6f, 0f, 1200, 9000, 900, reach: 0.8f), 1, 2);
            Assert.AreEqual(0f, cell.Sc[0]);
            Assert.AreEqual(9000f - 1200f, cell.DepthM[0], 1f);
        }

        [TestMethod]
        public void SheetsGoToTheirSaturatedLevelsAndOtherwiseByTheRule()
        {
            byte c, t;
            MapSkyRegions.Parts low = Plan(One(0f, 0.7f, 800, 800, 1500, 1f, 0.5f, 0.5f));
            Draw(low, 0, out c, out t);
            Assert.AreEqual(Seventy, c, "the low level's sheet is the deck");
            Assert.AreEqual(0f, low.Sc[0]);
            MapSkyRegions.Parts mid = Plan(One(0f, 0.7f, 800, 800, 1500, 0.5f, 1f, 0.5f));
            Draw(mid, 1, out c, out t);
            Assert.AreEqual(Seventy, c);
            Assert.AreEqual(255, t, "the middle level's sheet is the alto type");
            MapSkyRegions.Parts high = Plan(One(0f, 0.7f, 800, 800, 1500, 0.5f, 0.5f, 1f));
            Draw(high, 3, out c, out t);
            Assert.AreEqual(Seventy, c);
            // no saturated level: rule A at the condensation level, rule B the deck's share in the middle, rule 0 the most humid level
            MapSkyRegions.Parts ra = Plan(One(0f, 0.7f, 800, 800, 1500, 0.6f, 0.9f, 0.5f, deck: 0.3f), 1);
            Assert.AreEqual(0.7f, ra.Sc[0], 1e-6f);
            Assert.AreEqual(0f, ra.Ac[0]);
            MapSkyRegions.Parts rb = Plan(One(0f, 0.7f, 800, 800, 1500, 0.6f, 0.9f, 0.5f, deck: 0.3f), 2);
            Assert.AreEqual(0.3f, rb.Ac[0], 1e-6f);
            Assert.AreEqual(0.4f, rb.Sc[0], 1e-6f);
            MapSkyRegions.Parts r0 = Plan(One(0f, 0.7f, 800, 800, 1500, 0.6f, 0.9f, 0.5f, deck: 0.3f), 0);
            Assert.AreEqual(0.7f, r0.Ac[0], 1e-6f);
        }

        [TestMethod]
        public void DeepHeapGoesOverToTheTallLayerSmoothly()
        {
            CloudLayerDef[] l = Layers();
            float congM = (l[1].Types[MapSkyRegions.HeapLast(l[1])].TopShare - l[1].Types[MapSkyRegions.HeapLast(l[1])].BaseShare) * l[1].ThicknessM;
            float calvusM = (l[2].Types[0].TopShare - l[2].Types[0].BaseShare) * l[2].ThicknessM;
            float last = -1f;
            for (double d = congM - 500; d <= calvusM + 500; d += 250)
            {
                MapSkyRegions.Parts p = Plan(One(0.5f, 0f, 1000, 1000 + d, 1000 + d + 10, reach: 0.5f));
                Assert.AreEqual(0.5f, p.Tall[0] + p.Lattice[0], 1e-5f);
                Assert.IsTrue(p.Tall[0] >= last - 1e-6f, "the tall share rises with the depth");
                Assert.IsTrue(p.Tall[0] - last < 0.5f * 250f / (calvusM - congM) + 1e-3f || last < 0f, "no jump");
                last = p.Tall[0];
            }
            Assert.AreEqual(0.5f, last, 1e-6f);
        }

        [TestMethod]
        public void TheCalibratedNoiseIsTheProposalsScript()
        {
            // calibrated_depth.py smooth_noise at these points (period one Kerbin day, 21549.425 s)
            double[] lat = { 28.6, -10.0, 55.3, 0.1 }, lon = { -74.6, 120.0, -3.3, 179.9 }, t = { 1486800.0, 2000000.0, 12345.0, 1e7 };
            double[] want = { 0.50264451, 0.31817152, 0.8909271, 0.99148839 };
            for (int k = 0; k < 4; k++) Assert.AreEqual(want[k], MapSkyRegions.SmoothNoise(lat[k], lon[k], t[k], 21549.425), 1e-6, "point " + k);
        }

        [TestMethod]
        public void TheCalibratedDepthStaysBelowTheIceLevelAndTheTropopause()
        {
            MapSkyRegions.Inputs m = One(0.4f, 0f, 1000, 1200, 3000, rhMid: 1f);
            for (int k = 0; k < 50; k++)
            {
                m.Ut = 1486800.0 + k * 3000.0;
                double d = MapSkyRegions.CalibratedDepthM(m, 0, 1000.0);
                double zIce = (290.0 - 263.15) / 0.00711;
                Assert.IsTrue(d >= 0.0 && 1000.0 + d <= Math.Max(zIce, 1000.0 + 0.25 * 1000.0) + 1e-6 && 1000.0 + d <= H, "depth " + d);
            }
        }

        [TestMethod]
        public void OnlyTheHeapLayerHasSheetTypesAndTheRootsFollowTheBase()
        {
            CloudLayerDef[] l = Layers();
            Assert.AreEqual(0f, MapSkyRegions.SheetTypes(l[0]).x);
            Assert.AreEqual(1f, MapSkyRegions.SheetTypes(l[1]).x);
            Assert.AreEqual(0f, MapSkyRegions.SheetTypes(l[2]).x);
            MapSkyRegions.Parts p = Plan(One(0.4f, 0f, 600, 1600, 1800));
            Assert.AreEqual(600f - l[1].InnerAltitudeM, MapSkyRegions.Roots(l[1], p)[0], 1e-3f);
            MapSkyRegions.Parts high = Plan(One(0.4f, 0f, 9000, 9100, 9500));
            Assert.AreEqual(MapSkyRegions.RootRangeM(l[1]), MapSkyRegions.Roots(l[1], high)[0], 1e-3f, "clamped to the range");
        }
    }
}
