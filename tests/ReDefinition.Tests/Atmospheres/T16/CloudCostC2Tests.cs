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
    }
}
