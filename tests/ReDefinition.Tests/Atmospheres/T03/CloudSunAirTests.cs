using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // E40 sun colour: the clouds are lit by the sun through the air above the CLOUD, not above the camera. Scatterer filters Unity's sun light by the air above the active
    // vessel; the pass divides that air out (CloudSunAir) and the shader's SunAir puts the air above each sample back (CloudLightMath.SunAir is its CPU twin).
    // The numbers are Kerbin's atmosphere node of StockScattererConfigs (m_betaR 0.01534 / 0.03571 / 0.08755 per km, BETA_MSca 0.01214, HR 3.84 km, HM 1 km) and the sun
    // 45.64 degrees up as in K-T2d start 55 (.local/plans/atmospheres/E40-sun-colour-design.md).
    [TestClass]
    public class CloudSunAirTests
    {
        private static readonly double SinSun = Math.Sin(45.64 * Math.PI / 180.0);

        private static CloudLightSettings KerbinAir()
        {
            var s = new CloudLightSettings { SunAirWhiteBalance = false };   // what the game runs: OwnCloudOptions.Apply writes the setting, which is off by default
            CloudBridgeAir.Air air = CloudBridgeAir.FromScatterer(new Vector3(0.01534f, 0.03571f, 0.08755f), new Vector3(0.01214f, 0.01214f, 0.01214f), 3.84f, 1f);
            Assert.IsTrue(air.Valid && air.FromScatterer);
            s.UseAir(air.RayleighPerM, air.RayleighHeightM, air.MiePerM, air.MieHeightM);
            return s;
        }

        // The sun colour of the shader at a sample (per channel), for a pass colour.
        private static double[] Shader(CloudLightSettings s, double sin, double altitude)
        {
            return new[]
            {
                CloudLightMath.SunAir(sin, s.SunAirTau.x, s.SunAirHeightM, s.SunAirMieTau, s.SunAirMieHeightM, altitude),
                CloudLightMath.SunAir(sin, s.SunAirTau.y, s.SunAirHeightM, s.SunAirMieTau, s.SunAirMieHeightM, altitude),
                CloudLightMath.SunAir(sin, s.SunAirTau.z, s.SunAirHeightM, s.SunAirMieTau, s.SunAirMieHeightM, altitude)
            };
        }

        // What the cloud sees at a sample for a camera at the given altitude: Scatterer's colour at the camera (the white sun times the air above the camera),
        // through the pass's division, times the shader's air above the sample.
        private static double[] CloudSun(CloudLightSettings s, double cameraAltitude, double sampleAltitude)
        {
            double[] atCamera = Shader(s, SinSun, cameraAltitude);
            Color pass = CloudSunAir.OutsideAir(new Color((float)atCamera[0], (float)atCamera[1], (float)atCamera[2]), new Color(1f, 1f, 1f), SinSun, cameraAltitude, s);
            double[] air = Shader(s, SinSun, sampleAltitude);
            return new[] { pass.r * air[0], pass.g * air[1], pass.b * air[2] };
        }

        [TestMethod]
        public void TheSunColourAtASampleDoesNotDependOnTheCamerasAltitude()
        {
            CloudLightSettings s = KerbinAir();
            double[] low = CloudSun(s, 92.0, 2000.0), high = CloudSun(s, 8400.0, 2000.0), ground = CloudSun(s, 0.0, 2000.0);
            for (int c = 0; c < 3; c++)
            {
                Assert.AreEqual(low[c], high[c], 1e-3, "channel " + c + ": camera at 92 m against 8.4 km");
                Assert.AreEqual(low[c], ground[c], 1e-3, "channel " + c + ": camera at the ground");
            }
        }

        [TestMethod]
        public void ASampleAt2KmGetsTheSunThroughTheAirAboveIt()
        {
            // The design's table (E40-sun-colour-design.md) is 0.945 / 0.882 / 0.738 of the white sun from the slant path on the sphere; the shader's Kasten-Young air mass gives
            // 0.950 / 0.890 / 0.755 for the same air, within 0.02 of it in every channel.
            double[] sun = CloudSun(KerbinAir(), 92.0, 2000.0);
            Assert.AreEqual(0.945, sun[0], 0.02);
            Assert.AreEqual(0.882, sun[1], 0.02);
            Assert.AreEqual(0.738, sun[2], 0.02);
            Assert.AreEqual(0.950, sun[0], 2e-3);
            Assert.AreEqual(0.890, sun[1], 2e-3);
            Assert.AreEqual(0.755, sun[2], 2e-3);
        }

        [TestMethod]
        public void AHigherCloudGetsAWhiterSunAndALowOneAWarmerOne()
        {
            CloudLightSettings s = KerbinAir();
            double[] low = CloudSun(s, 92.0, 1000.0), mid = CloudSun(s, 92.0, 4000.0), high = CloudSun(s, 92.0, 8000.0);
            Assert.IsTrue(low[2] / low[0] < mid[2] / mid[0] && mid[2] / mid[0] < high[2] / high[0], "blue over red rises with the sample's altitude");
            Assert.IsTrue(high[2] / high[0] > 0.95, "a cloud at 8 km is almost in the white sun: " + high[2] / high[0]);
        }

        [TestMethod]
        public void TheModelReproducesTheMeasuredSunColoursOfTheCamera()
        {
            // The measured blue over red of the pass at 92 m and 8.4 km (start 55: 0.69 and 0.96 to 0.97) against the model with Scatterer's air (0.686 and 0.958 in the design).
            CloudLightSettings s = KerbinAir();
            double[] a = Shader(s, SinSun, 92.0), b = Shader(s, SinSun, 8400.0);
            Assert.AreEqual(0.69, a[2] / a[0], 0.01);
            Assert.AreEqual(0.96, b[2] / b[0], 0.01);
        }

        [TestMethod]
        public void TheSunNeverGainsLight()
        {
            CloudLightSettings s = KerbinAir();
            // A colour that is brighter than the camera's air allows (the air model overestimates what Scatterer took out) is capped at the unextinguished sun.
            Color pass = CloudSunAir.OutsideAir(new Color(0.95f, 0.9f, 0.8f), new Color(1f, 1f, 1f), SinSun, 92.0, s);
            Assert.IsTrue(pass.r <= 1f && pass.g <= 1f && pass.b <= 1f, pass.ToString());
            // A camera in the dark (the sun below the horizon at the camera): the division is floored, not infinite.
            Color night = CloudSunAir.OutsideAir(new Color(0.01f, 0.01f, 0.01f), null, -0.3, 92.0, s);
            Assert.IsTrue(night.r <= 0.01f / CloudLightMath.CameraAirFloor + 1e-6f && !float.IsInfinity(night.r));
            // Without a cap the colour is the quotient alone.
            Color noCap = CloudSunAir.OutsideAir(new Color(0.5f, 0.5f, 0.5f), null, SinSun, 0.0, s);
            Assert.IsTrue(noCap.r > 0.5f && noCap.b > noCap.r, "the blue is the more divided: " + noCap);
        }

        [TestMethod]
        public void TheCameraInsideALayerReadsContinuously()
        {
            // The sample at the camera's own altitude gets exactly the sun outside the air back that the colour had at the camera: the product is Scatterer's colour at the camera.
            CloudLightSettings s = KerbinAir();
            foreach (double altitude in new[] { 92.0, 1500.0, 3200.0, 8400.0 })
            {
                double[] atCamera = Shader(s, SinSun, altitude), cloud = CloudSun(s, altitude, altitude);
                for (int c = 0; c < 3; c++) Assert.AreEqual(atCamera[c], cloud[c], 1e-3, "altitude " + altitude + " channel " + c);
            }
        }

        [TestMethod]
        public void TheTwoComponentAirEqualsTheOldFormWithoutTheAerosol()
        {
            double[] tau = { 0.0764, 0.138, 0.295 };
            foreach (double sin in new[] { 0.9, 0.5, 0.05 })
                for (int c = 0; c < 3; c++)
                    Assert.AreEqual(CloudLightMath.SunAir(sin, tau[c], 2000.0, 8000.0), CloudLightMath.SunAir(sin, tau[c], 8000.0, 0.0, 1000.0, 2000.0), 1e-12);
        }

        [TestMethod]
        public void TheSettingsUploadTheAirOfTheBodyInTwoComponents()
        {
            CloudLightSettings s = KerbinAir();
            // The coefficients are per km in the node and per m in the air: tau is the coefficient per m times the scale height in m.
            Assert.AreEqual(0.01534f / 1000f * 3840f, s.SunAirTau.x, 1e-4f);
            Assert.AreEqual(0.08755f / 1000f * 3840f, s.SunAirTau.z, 1e-4f);
            Assert.AreEqual(3840f, s.SunAirHeightM, 1e-3f);
            Assert.AreEqual(0.01214f / 0.9f / 1000f * 1000f, s.SunAirMieTau, 1e-4f);
            Assert.AreEqual(1000f, s.SunAirMieHeightM, 1e-3f);
        }
    }
}
