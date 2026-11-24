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

        // The leap's vertical cap over a sample the map made empty (layers.leapVerticalShare -> _SpacingP.y; the stripe under the horizon from above, PRUEFPROTOKOLL 167): off by
        // default, so the picture is the one before until the owner says yes; the key sets it, and a share above 1 is clamped (1 = the whole layer's depth, no tighter than no cap).
        [TestMethod]
        public void TheLeapVerticalShareIsOffByDefaultAndTheTuningKeySetsIt()
        {
            var frame = new LayerFrame();
            Assert.AreEqual(0f, frame.LeapVerticalShare, "the frame as compiled has no cap");
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9200011);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.AreEqual(0f, frame.LeapVerticalShare, "off by default");
            Assert.AreEqual(OwnCloudOptions.LeapVerticalShareDefault, frame.LeapVerticalShare);
            try
            {
                Tuning.SetOverride(OwnCloudOptions.TuneLeapVerticalShare, 0.1f);
                Tuning.Instance.BeginFrame(9200012);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.AreEqual(0.1f, frame.LeapVerticalShare, 1e-6f);
                Tuning.SetOverride(OwnCloudOptions.TuneLeapVerticalShare, 3f);
                Tuning.Instance.BeginFrame(9200013);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.AreEqual(1f, frame.LeapVerticalShare, "clamped to the layer's whole depth");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9200014);
            }
        }
    }
}
