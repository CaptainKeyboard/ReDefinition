using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // K-W3a row B12.vessel_altitude_error_m read 600075 m, one body radius plus the altitude. The site carries the altitude above sea level
    // unchanged, and the probe's reference must be one that cannot be a radius off.
    [TestClass]
    public class VesselSiteAltitudeTests
    {
        [TestMethod]
        public void ASiteCarriesTheAltitudeAboveSeaLevelItIsGiven()
        {
            ForecastSite site = ForecastModule.SiteAt(1, 28.6, -80.0, 74.83, "Vessel");
            Assert.AreEqual(74.83, site.AltAgl, 0.0);
            Assert.AreEqual(1, site.BodyIndex);
        }

        [TestMethod]
        public void TheProbeReferenceIsTheTerrainPlusTheHeightAndNeverARadiusOff()
        {
            // The pad of K-W3a: terrain 64.78 m, the vessel's centre 10 m above it, the site 74.83 m: an error of 0.05 m.
            double reference = B12Probe.VesselAltitudeReference(64.78, 10.0);
            Assert.AreEqual(74.78, reference, 1e-9);
            Assert.IsTrue(System.Math.Abs(74.83 - reference) < 50.0);
            Assert.IsTrue(System.Math.Abs(74.83 - B12Probe.VesselAltitudeReference(64.78, -1.0)) < 50.0, "an unmeasured height counts as zero");
            Assert.IsTrue(System.Math.Abs(74.83 - B12Probe.VesselAltitudeReference(64.78, double.NaN)) < 50.0);
        }
    }
}
