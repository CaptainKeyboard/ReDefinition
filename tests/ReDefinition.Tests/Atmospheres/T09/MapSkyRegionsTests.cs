using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The sky drawn from the weather map (MapSkyRegions, master plan section 34) on single texels of Kerbin's shipped layers: clear where the map is clear, the cumuliform on the lattice,
    // capped cumuliform and unsaturated sheets as connected stratocumulus, saturated levels in their layers, deep cumuliform in the tall layer.
    [TestClass]
    public class MapSkyRegionsTests
    {
        private const double H = 10500.0;
        // A stratiform amount of 0.7 as the layers' byte.
        private static readonly byte Seventy = (byte)Math.Round(0.7f * 255.0, MidpointRounding.ToEven);

        private static MapSkyRegions.Inputs One(float cumuliform, float sheet, double baseM, double topM, double stableM, float rhLow = 0.5f, float rhMid = 0.5f, float rhHigh = 0.5f,
                                                float lowStratiform = 0f, float reach = 0f, float precip = 0f)
        {
            return new MapSkyRegions.Inputs
            {
                Width = 1, Height = 1, Cover = new[] { cumuliform + sheet }, Cumuliform = new[] { cumuliform }, Stratiform = new[] { sheet }, LowStratiform = new[] { lowStratiform }, BaseShare = new[] { (float)(baseM / H) },
                TopShare = new[] { (float)(topM / H) }, StableShare = new[] { (float)(stableM / H) }, RhLow = new[] { rhLow }, RhMid = new[] { rhMid }, RhHigh = new[] { rhHigh },
                Reach = new[] { reach }, PrecipLevel = new[] { precip }, TropopauseM = H, Ut = 1486800.0
            };
        }

        private static CloudLayerDef[] Layers()
        {
            return TypeMixFixture.ShippedLayers();
        }

        private static MapSkyRegions.Parts Plan(MapSkyRegions.Inputs m)
        {
            CloudLayerDef[] l = Layers();
            return MapSkyRegions.Plan(m, l[1], l[2]);
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
        public void UncappedCumuliformIsCumulusOnTheLatticeWithItsTypeFromTheDepth()
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
            Assert.IsTrue(tDeep > tThin, "deeper cumuliform, taller type: " + tThin + " -> " + tDeep);
            Assert.IsTrue(tDeep / 255f <= MapSkyRegions.CumuliformLast(cu) * step + 0.01f, "never the alto stratiform type");
        }

        [TestMethod]
        public void CappedCumuliformIsAConnectedStratocumulusSheet()
        {
            // stable layer below the base: capped
            MapSkyRegions.Parts a = Plan(One(0.6f, 0f, 1200, 1200, 900));
            Assert.AreEqual(0.6f, a.Sc[0], 1e-6f, "above the lattice's fair share all of it is sheet");
            Assert.AreEqual(0f, a.Lattice[0], 1e-6f);
            MapSkyRegions.Parts aLow = Plan(One(0.1f, 0f, 1200, 1200, 900));
            Assert.AreEqual(0.1f * 0.4f, aLow.Sc[0], 1e-6f, "a small capped cumuliform is mostly lattice");
            Assert.AreEqual(0.06f, aLow.Lattice[0], 1e-6f);
            byte c, t;
            Draw(a, 1, out c, out t);
            Assert.AreEqual(0, t, "the stratiform type (Stratocumulus, the first)");
            // a cell is never capped
            MapSkyRegions.Parts cell = Plan(One(0.6f, 0f, 1200, 9000, 900, reach: 0.8f));
            Assert.AreEqual(0f, cell.Sc[0]);
            Assert.AreEqual(9000f - 1200f, cell.DepthM[0], 1f);
        }

        [TestMethod]
        public void SheetsGoToTheirSaturatedLevelsAndOtherwiseByTheRule()
        {
            byte c, t;
            MapSkyRegions.Parts low = Plan(One(0f, 0.7f, 800, 800, 1500, 1f, 0.5f, 0.5f));
            Draw(low, 0, out c, out t);
            Assert.AreEqual(Seventy, c, "the low level's sheet is the low stratiform layer");
            Assert.AreEqual(0f, low.Sc[0]);
            MapSkyRegions.Parts mid = Plan(One(0f, 0.7f, 800, 800, 1500, 0.5f, 1f, 0.5f));
            Draw(mid, 1, out c, out t);
            Assert.AreEqual(Seventy, c);
            Assert.AreEqual(255, t, "the middle level's sheet is the alto type");
            MapSkyRegions.Parts high = Plan(One(0f, 0.7f, 800, 800, 1500, 0.5f, 0.5f, 1f));
            Draw(high, 3, out c, out t);
            Assert.AreEqual(Seventy, c);
            // no saturated level ("by source", owner 2026-11-23): the low stratiform layer's share as the middle level's Altocumulus, the rest a Stratocumulus layer at the condensation level
            MapSkyRegions.Parts rb = Plan(One(0f, 0.7f, 800, 800, 1500, 0.6f, 0.9f, 0.5f, lowStratiform: 0.3f));
            Assert.AreEqual(0.3f, rb.Ac[0], 1e-6f);
            Assert.AreEqual(0.4f, rb.Sc[0], 1e-6f);
            MapSkyRegions.Parts none = Plan(One(0f, 0.7f, 800, 800, 1500, 0.6f, 0.9f, 0.5f));
            Assert.AreEqual(0f, none.Ac[0], 1e-6f);
            Assert.AreEqual(0.7f, none.Sc[0], 1e-6f, "without a low stratiform share all of the sheet is Stratocumulus");
        }

        [TestMethod]
        public void DeepCumuliformGoesOverToTheTallLayerSmoothly()
        {
            CloudLayerDef[] l = Layers();
            float congM = (l[1].Types[MapSkyRegions.CumuliformLast(l[1])].TopShare - l[1].Types[MapSkyRegions.CumuliformLast(l[1])].BaseShare) * l[1].ThicknessM;
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

        // Owner 2026-11-23 "Ja, Feld-Basis": the low stratiform layer's types are lifted to the field's cloud base (never lowered, at most the low stratiform layer's depth); the fog type stays on the ground.
        [TestMethod]
        public void TheLowStratiformFollowsTheFieldsBaseAndItsFogStaysOnTheGround()
        {
            CloudLayerDef lowStratiform = Layers()[0];
            Assert.AreEqual(WeatherMapSky.Role.LowStratiform, WeatherMapSky.RoleOf(lowStratiform));
            float depth = lowStratiform.OuterAltitudeM - lowStratiform.InnerAltitudeM;
            Assert.AreEqual(depth, MapSkyRegions.LowStratiformRootRangeM(lowStratiform), 1e-3f);
            int count = lowStratiform.Types.Count;
            float fogValue = lowStratiform.LowStratiform != null ? lowStratiform.LowStratiform.FogType : 0f, precipValue = lowStratiform.LowStratiform != null ? lowStratiform.LowStratiform.PrecipType : 0.5f;
            MapSkyRegions.Parts p = Plan(One(0f, 0.7f, 900, 1900, 1900, 1f, 0.5f, 0.5f));
            float precipBase = lowStratiform.InnerAltitudeM + BaseShareAt(lowStratiform, precipValue) * depth;
            float[] r = MapSkyRegions.LowStratiformRoots(lowStratiform, p, new[] { (byte)Math.Round(precipValue * 255.0) });
            Assert.AreEqual(900f - precipBase, r[0], 2f, "the precipitating type's base is lifted to the field's 900 m");
            r = MapSkyRegions.LowStratiformRoots(lowStratiform, p, new[] { (byte)Math.Round(fogValue * 255.0) });
            Assert.AreEqual(0f, r[0], 1e-3f, "the fog type stays on the ground");
            MapSkyRegions.Parts low = Plan(One(0f, 0.7f, 50, 400, 400, 1f, 0.5f, 0.5f));
            r = MapSkyRegions.LowStratiformRoots(lowStratiform, low, new[] { (byte)Math.Round(precipValue * 255.0) });
            Assert.AreEqual(0f, r[0], 1e-3f, "a field base below the cfg base never lowers the low stratiform layer");
            MapSkyRegions.Parts high = Plan(One(0f, 0.7f, 9000, 9500, 9500, 1f, 0.5f, 0.5f));
            r = MapSkyRegions.LowStratiformRoots(lowStratiform, high, null);
            Assert.AreEqual(depth, r[0], 1e-3f, "at most the low stratiform layer's depth");
        }

        private static float BaseShareAt(CloudLayerDef l, float typeValue)
        {
            int count = l.Types.Count;
            float x = typeValue * (count - 1);
            int ta = Math.Min((int)Math.Floor(x), count - 1), tb = Math.Min(ta + 1, count - 1);
            return l.Types[ta].BaseShare + (l.Types[tb].BaseShare - l.Types[ta].BaseShare) * (x - ta);
        }

        [TestMethod]
        public void OnlyTheCumuliformLayerHasStratiformTypesAndTheRootsFollowTheBase()
        {
            CloudLayerDef[] l = Layers();
            Assert.AreEqual(0f, MapSkyRegions.StratiformTypes(l[0]).x);
            Assert.AreEqual(1f, MapSkyRegions.StratiformTypes(l[1]).x);
            Assert.AreEqual(0f, MapSkyRegions.StratiformTypes(l[2]).x);
            MapSkyRegions.Parts p = Plan(One(0.4f, 0f, 600, 1600, 1800));
            Assert.AreEqual(600f - l[1].InnerAltitudeM, MapSkyRegions.Roots(l[1], p)[0], 1e-3f);
            MapSkyRegions.Parts high = Plan(One(0.4f, 0f, 9000, 9100, 9500));
            Assert.AreEqual(MapSkyRegions.RootRangeM(l[1]), MapSkyRegions.Roots(l[1], high)[0], 1e-3f, "clamped to the range");
        }
    }
}
