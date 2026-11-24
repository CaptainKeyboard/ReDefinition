using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T16
{
    // E40 C2: the leap over empty grid points that follows a layer's noise scale (layers.leapScale). The shader does the leap; the weather player measures the cost and the picture
    // against the converged one (tools/weather_metrics/e40c1.py, cases lp<NN>).
    [TestClass]
    public class CloudCostC2Tests
    {
        [TestMethod]
        public void TheLeapScaleIsOnByDefaultAndTheTuningKeySetsIt()
        {
            var frame = new LayerFrame();
            Assert.AreEqual(0f, frame.LeapScales, "the frame as compiled is the march as it was");
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9200001);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.AreEqual(OwnCloudOptions.LeapScaleDefault, frame.LeapScales);
            Assert.IsTrue(OwnCloudOptions.LeapScaleDefault > 0f, "the leap is on in the product");
            Tuning.SetOverride(OwnCloudOptions.TuneLeapScale, 0f);
            try
            {
                Tuning.Instance.BeginFrame(9200002);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.AreEqual(0f, frame.LeapScales, "layers.leapScale 0 is the march as it was");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9200003);
            }
        }

        // The layer's thinnest cloud slab (_CullL.x), which bounds the vertical part of a leap over empty samples (the stripe under the horizon from above, PRUEFPROTOKOLL 166-167):
        // the thinnest type's base to top in m, the height variation's thinnest cloud where it is thinner, types without thickness skipped, 0 for a layer without a type.
        [TestMethod]
        public void TheThinnestSlabIsTheThinnestTypeOrTheHeightVariationsFloor()
        {
            float[] shares = { 0.5f, 0.11f, 0f, 0.3f };
            Assert.AreEqual(0.11f * 2000f, CullMath.ThinnestSlabM(4, t => shares[t], 2000f, 0f), 1e-3f, "the thinnest type; a type without thickness skipped");
            Assert.AreEqual(120f, CullMath.ThinnestSlabM(4, t => shares[t], 2000f, 120f), 1e-3f, "the height variation's floor where thinner");
            Assert.AreEqual(220f, CullMath.ThinnestSlabM(4, t => shares[t], 2000f, 500f), 1e-3f, "the type where the floor is thicker");
            Assert.AreEqual(0f, CullMath.ThinnestSlabM(0, t => 1f, 2000f, 0f), "no type: no slab");
        }
    }
}
