using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The colour a far cloud fades into, worked out from the body's air (E40 H1 haze: CloudHorizonSky, CloudHazePlan.SkyInscatter). Nothing in these rows is fitted to a sky: the inputs are
    // the Scatterer nodes of StockScattererConfigs (Kerbin, Duna), the camera's height, the sun's elevation and the view's azimuth; the one measurement is the frame of K-T2 start 61.
    [TestClass]
    public class CloudHorizonSkyTests
    {
        private const string Nodes = @"
Scatterer_atmosphere
{
    Atmo
    {
        name = Kerbin
        HR = 3.83999991
        HM = 1
        m_betaR = 0.0153407799,0.0357074998,0.0875488967
        BETA_MSca = 0.0121406196,0.0121406196,0.0121406196
        m_mieG = 0.850000024
        averageGroundReflectance = 0.100000001
        configPoints
        {
            Item
            {
                altitude = 0
                skyExposure = 0.200000003
            }
        }
    }
    Atmo
    {
        name = Duna
        HR = 5
        HM = 3
        m_betaR = 0.0448499992,0.0225000009,0.00779999979
        BETA_MSca = 0.00449999981,0.00899999961,0.00899999961
        m_mieG = 0.699999988
        averageGroundReflectance = 0.100000001
        configPoints
        {
            Item
            {
                altitude = 0
                skyExposure = 0.200000003
            }
        }
    }
}";
        private const double KerbinRadius = 600000.0, DunaRadius = 320000.0, Deg = Math.PI / 180.0;

        private static CloudBridgeAir.Air Node(string body)
        {
            string why;
            CloudBridgeAir.Air a = ScattererAirReader.Read(new[] { ConfigNode.Parse(Nodes).GetNode("Scatterer_atmosphere") }, body, out why);
            Assert.IsTrue(a.Valid, why);
            return a;
        }

        private static CloudHorizonSky.Horizon Ray(CloudBridgeAir.Air a, double radius, double heightM, double sunDeg, double azimuthDeg)
        {
            return CloudHorizonSky.Evaluate(CloudHorizonSky.MediumOf(a, radius), heightM, 0.0, sunDeg * Deg, azimuthDeg * Deg);
        }

        private static CloudHorizonSky.Rgb Shown(CloudBridgeAir.Air a, CloudHorizonSky.Rgb radiance, double heightM)
        {
            return CloudHorizonSky.Display(radiance, a.SkyExposureAt(heightM));
        }

        // The Python twin (tools/weather_metrics/horizon_sky.py, which the player's jobs take their far colours from) gives the same numbers: its selftest pins these rows too.
        [TestMethod]
        public void ThePythonTwinGivesTheSameNumbers()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin"), duna = Node("Duna");
            CloudHorizonSky.Horizon h = Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0);
            AssertRgb(0.6549, 0.7786, 0.8060, Shown(kerbin, h.Far, 92.0), "Kerbin far, sun 44 behind");
            AssertRgb(0.7176, 0.7879, 0.7973, Shown(kerbin, h.Near, 92.0), "Kerbin near");
            AssertRgb(0.6302, 0.6637, 0.5218, Shown(kerbin, Ray(kerbin, KerbinRadius, 92.0, 10.0, 180.0).Far, 92.0), "Kerbin far, sun 10 behind");
            AssertRgb(0.7986, 0.7170, 0.5315, Shown(duna, Ray(duna, DunaRadius, 92.0, 44.0, 180.0).Far, 92.0), "Duna far, sun 44 behind");
        }

        private static void AssertRgb(double r, double g, double b, CloudHorizonSky.Rgb actual, string what)
        {
            Assert.AreEqual(r, actual.R, 0.002, what + " red");
            Assert.AreEqual(g, actual.G, 0.002, what + " green");
            Assert.AreEqual(b, actual.B, 0.002, what + " blue");
        }

        [TestMethod]
        public void TheNodeGivesTheMiePhaseTheGroundAndTheSkyExposure()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin"), duna = Node("Duna");
            Assert.IsTrue(kerbin.HasSky);
            Assert.AreEqual(0.85f, kerbin.MieG, 1e-6f);
            Assert.AreEqual(0.7f, duna.MieG, 1e-6f);
            Assert.AreEqual(0.1f, kerbin.GroundAlbedo, 1e-6f);
            Assert.AreEqual(0.2f, kerbin.SkyExposureAt(0.0), 1e-6f);
            Assert.AreEqual(0.2f, kerbin.SkyExposureAt(50000.0), 1e-6f, "one config point: the same everywhere");
            string text = Nodes.Replace("skyExposure = 0.200000003", "skyExposure = 0.2").Replace("altitude = 0\n                skyExposure = 0.2",
                "altitude = 0\n                skyExposure = 0.2\n            }\n            Item\n            {\n                altitude = 10000\n                skyExposure = 0.4");
            string why;
            CloudBridgeAir.Air two = ScattererAirReader.Read(new[] { ConfigNode.Parse(text).GetNode("Scatterer_atmosphere") }, "Kerbin", out why);
            Assert.AreEqual(0.2f, two.SkyExposureAt(0.0), 1e-6f);
            Assert.AreEqual(0.3f, two.SkyExposureAt(5000.0), 1e-5f, "linear between the points");
            Assert.AreEqual(0.4f, two.SkyExposureAt(20000.0), 1e-6f, "the last point holds above it");
            string bare = Nodes.Replace("m_mieG = 0.850000024", "").Replace("averageGroundReflectance = 0.100000001", "");
            CloudBridgeAir.Air none = ScattererAirReader.Read(new[] { ConfigNode.Parse(bare).GetNode("Scatterer_atmosphere") }, "Kerbin", out why);
            Assert.IsTrue(none.Valid, why);
            Assert.AreEqual(CloudHorizonSky.DefaultMieG, none.MieG, 1e-6);
            Assert.AreEqual(CloudHorizonSky.DefaultGroundAlbedo, none.GroundAlbedo, 1e-6);
        }

        [TestMethod]
        public void TheTwoPhaseFunctionsIntegrateToOneOverTheSphere()
        {
            foreach (double g in new[] { 0.0, 0.7, 0.85 })
            {
                double rayleigh = 0.0, mie = 0.0;
                const int n = 200000;
                for (int i = 0; i < n; i++)
                {
                    double mu = -1.0 + 2.0 * (i + 0.5) / n;
                    rayleigh += CloudHorizonSky.Rayleigh(mu) * 2.0 * Math.PI * 2.0 / n;
                    mie += CloudHorizonSky.MiePhase(mu, g) * 2.0 * Math.PI * 2.0 / n;
                }
                Assert.AreEqual(1.0, rayleigh, 1e-4);
                Assert.AreEqual(1.0, mie, 0.01, "g " + g);
            }
        }

        // The measurement: the frame of K-T2 start 61 without our clouds (ground view, sun 44 degrees up), the sky at row 740 of 960, a little above the horizon, in display values: 0.663 / 0.784 /
        // 0.820. The azimuth of that view from the sun is not logged; the frame shows no sun disc or glare, the sun is behind the camera. The model, with the node of Kerbin and the camera at
        // 92 m (the horizontal ray, which the far clouds of a layer above the camera stand on), gives 0.655 / 0.779 / 0.806 there, and 0.69 / 0.80 / 0.81 at 20 degrees from the sun: the
        // measurement lies in between; 90 degrees from the sun is darker (0.57 / 0.69 / 0.73) and is not the frame. No gain, no exponent: the older law's gain (1.35 / 1.48 / 1.17 on a power law)
        // was fitted to these three numbers.
        [TestMethod]
        public void TheHorizonOfStart61IsReproducedWithoutAFittedNumber()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            double[] sky = { 0.663, 0.784, 0.820 };
            foreach (double az in new[] { 180.0, 30.0 })
            {
                CloudHorizonSky.Rgb d = Shown(kerbin, Ray(kerbin, KerbinRadius, 92.0, 44.0, az).Far, 92.0);
                Assert.AreEqual(sky[0], d.R, 0.03, "red at " + az);
                Assert.AreEqual(sky[1], d.G, 0.03, "green at " + az);
                Assert.AreEqual(sky[2], d.B, 0.03, "blue at " + az);
                Assert.AreEqual((sky[1] - sky[0]) / (sky[2] - sky[0]), (d.G - d.R) / (d.B - d.R), 0.12, "the position of green between red and blue at " + az);
            }
            CloudHorizonSky.Rgb side = Shown(kerbin, Ray(kerbin, KerbinRadius, 92.0, 44.0, 90.0).Far, 92.0);
            Assert.IsTrue(sky[0] - side.R > 0.07, "at 90 degrees from the sun the sky is darker than the frame");
            // the camera's height hardly matters: 300 m gives the same colour to 0.03
            CloudHorizonSky.Rgb high = Shown(kerbin, Ray(kerbin, KerbinRadius, 300.0, 44.0, 180.0).Far, 300.0);
            CloudHorizonSky.Rgb low = Shown(kerbin, Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0).Far, 92.0);
            Assert.AreEqual(low.G, high.G, 0.03);
        }

        // The chain of the FL package: a cloud of the congestus's lit-face colour (display 0.48, neutral) 50 km away, 2 km up, from a camera at 264 m, mixed with the air the way the layer shader does
        // (CloudHazePlan.AerialMix with the air's transmittance), against the sky's colour at the horizon: the older law needed a body gain to come near it.
        [TestMethod]
        public void AFarCongestusFadesIntoTheSkyWithinAFewPercent()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            const float white = 0.7244f;
            double elevation = Math.Atan((2000.0 - 264.0 + 50000.0 * 50000.0 / (2.0 * KerbinRadius)) / 50000.0);
            double tr, tg, tb;
            FarViewMath.AirTau(KerbinRadius, 0.0, KerbinRadius + 264.0, 0.0, Math.Cos(elevation), Math.Sin(elevation), 0.0, 50000.0, kerbin.RayleighPerM.x, kerbin.RayleighPerM.y, kerbin.RayleighPerM.z,
                               kerbin.MiePerM, kerbin.RayleighHeightM, kerbin.MieHeightM, 1.0, out tr, out tg, out tb);
            var t = new Vector3((float)Math.Exp(-tr), (float)Math.Exp(-tg), (float)Math.Exp(-tb));
            var sun = new CloudHorizonSky.Evaluator();
            CloudHazePlan.SkyView view = new CloudHazePlan.SkyView { HeightM = 264.0, LayerAltitudeM = 1700.0, SunElevation = 44.0 * Deg, SunAzimuth = Math.PI };
            Color far, near;
            Assert.IsTrue(CloudHazePlan.SkyInscatter(sun, new CloudHorizonSky.Evaluator(), kerbin, KerbinRadius, view, new Color(0.0f, 0.0f, 0.0f, 1f), white, 1f, 2.2f, out far, out near));
            // the lit flank of the congestus (display 0.62, the FL package's closed part), neutral; the sky behind the cloud is the model's own on the cloud's ray
            float cloud = CloudOutputStage.Decode(0.62f, white, 1f, 2.2f);
            Color mixed = CloudHazePlan.AerialMix(new Color(cloud, cloud, cloud, 1f), 1f, t, near, far);
            CloudHorizonSky.Rgb behind = Shown(kerbin, CloudHorizonSky.Evaluate(CloudHorizonSky.MediumOf(kerbin, KerbinRadius), 264.0, elevation, 44.0 * Deg, Math.PI).Far, 264.0);
            float[] shownMix = { CloudOutputStage.Encode(mixed.r, white, 1f, 2.2f), CloudOutputStage.Encode(mixed.g, white, 1f, 2.2f), CloudOutputStage.Encode(mixed.b, white, 1f, 2.2f) };
            float[] shownSky = { (float)behind.R, (float)behind.G, (float)behind.B };
            float[] shownFar = { CloudOutputStage.Encode(far.r, white, 1f, 2.2f), CloudOutputStage.Encode(far.g, white, 1f, 2.2f), CloudOutputStage.Encode(far.b, white, 1f, 2.2f) };
            string said = " (T " + t.x.ToString("0.00") + " " + t.y.ToString("0.00") + " " + t.z.ToString("0.00") + ", cloud " + shownMix[0].ToString("0.000") + " " + shownMix[1].ToString("0.000") + " " + shownMix[2].ToString("0.000")
                          + ", sky behind " + shownSky[0].ToString("0.000") + " " + shownSky[1].ToString("0.000") + " " + shownSky[2].ToString("0.000") + ", far colour " + shownFar[0].ToString("0.000") + " "
                          + shownFar[1].ToString("0.000") + " " + shownFar[2].ToString("0.000") + ")";
            // green and blue (the channels the 50 km of air has taken T 0.35 and 0.09 of) land on the sky behind the cloud within 4 percent; red, which still has 60 percent of the cloud, is the
            // channel in which the sky falls off with the ray's elevation (0.65 at the horizon, 0.56 4.4 degrees up) and the one fade colour of the layer shader cannot follow
            Assert.AreEqual(shownSky[1], shownMix[1], 0.04 * shownSky[1], said);
            Assert.AreEqual(shownSky[2], shownMix[2], 0.04 * shownSky[2], said);
            Assert.AreEqual(0.2126 * shownSky[0] + 0.7152 * shownSky[1] + 0.0722 * shownSky[2], 0.2126 * shownMix[0] + 0.7152 * shownMix[1] + 0.0722 * shownMix[2], 0.04, "luma" + said);
            Assert.IsTrue(t.z < 0.1f, "almost no blue of the cloud itself reaches the camera" + said);
            // the colour the cloud fades into is the sky's at the horizon: the start-61 frame's, 0.663 / 0.784 / 0.820, to 0.03
            Assert.AreEqual(0.663, shownFar[0], 0.03, said);
            Assert.AreEqual(0.784, shownFar[1], 0.03, said);
            Assert.AreEqual(0.820, shownFar[2], 0.03, said);
        }

        [TestMethod]
        public void TheSkyFollowsTheSunsHeightAndTheViewsAzimuth()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            CloudHorizonSky.Rgb toward = Ray(kerbin, KerbinRadius, 92.0, 44.0, 0.0).Far, side = Ray(kerbin, KerbinRadius, 92.0, 44.0, 90.0).Far, away = Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0).Far;
            Assert.IsTrue(toward.G > away.G && away.G > side.G, "forward scattering of the aerosol, then the Rayleigh's back lobe, the side darkest");
            // a low sun with it behind the camera reddens and yellows the horizon (the sun's air path takes the blue), a high sun leaves it bluer
            CloudHorizonSky.Rgb low = Ray(kerbin, KerbinRadius, 92.0, 10.0, 180.0).Far, mid = Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0).Far, high = Ray(kerbin, KerbinRadius, 92.0, 80.0, 180.0).Far;
            Assert.IsTrue(low.B / low.R < mid.B / mid.R && mid.B / mid.R < high.B / high.R, "blue over red rises with the sun: " + low.B / low.R + " " + mid.B / mid.R + " " + high.B / high.R);
            Assert.AreEqual(0.630, Shown(kerbin, low, 92.0).R, 0.01, "sun 10 degrees, behind: 0.630 / 0.664 / 0.522");
            Assert.AreEqual(0.664, Shown(kerbin, low, 92.0).G, 0.01);
            Assert.AreEqual(0.522, Shown(kerbin, low, 92.0).B, 0.01);
            Assert.AreEqual(0.601, Shown(kerbin, high, 92.0).R, 0.01, "sun 80 degrees: 0.601 / 0.726 / 0.792");
            Assert.AreEqual(0.726, Shown(kerbin, high, 92.0).G, 0.01);
            Assert.AreEqual(0.792, Shown(kerbin, high, 92.0).B, 0.01);
            // a sun far below the horizon lights nothing
            CloudHorizonSky.Rgb night = Ray(kerbin, KerbinRadius, 92.0, -30.0, 180.0).Far;
            Assert.IsTrue(night.G < 1e-3, "night: " + night.G);
            // and the short-path colour is the sun's own more than the far one is
            CloudHorizonSky.Horizon h = Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0);
            Assert.IsTrue(h.Near.B / h.Near.R < h.Far.B / h.Far.R, "the air's own blue is in the extinction: the short path is the greyer colour");
        }

        [TestMethod]
        public void AnotherBodysAirGivesItsOwnSkyAndTheOwnAirOfTheBodyDataIsUsableToo()
        {
            CloudBridgeAir.Air duna = Node("Duna");
            CloudHorizonSky.Rgb d = Shown(duna, Ray(duna, DunaRadius, 92.0, 44.0, 180.0).Far, 92.0);
            Assert.IsTrue(d.R > d.G && d.G > d.B, "Duna's coefficients are strongest in red: a butterscotch horizon, not a blue one: " + d.R + " " + d.G + " " + d.B);
            Assert.AreEqual(0.794, d.R, 0.01);
            Assert.AreEqual(0.712, d.G, 0.01);
            Assert.AreEqual(0.528, d.B, 0.01);
            CloudHorizonSky.Rgb dusk = Shown(duna, Ray(duna, DunaRadius, 92.0, 10.0, 180.0).Far, 92.0);
            Assert.AreEqual(0.617, dusk.R, 0.01, "sun 10 degrees: 0.617 / 0.635 / 0.516");
            Assert.AreEqual(0.635, dusk.G, 0.01);
            Assert.AreEqual(0.516, dusk.B, 0.01);
            // Earth-scaled air from the body's data (no Scatterer): the same model, a blue horizon
            CloudBridgeAir.Air own = CloudBridgeAir.From(101325.0, 288.0, 0.0289644, 101325.0 * Math.Exp(-1000.0 / 8000.0), 1000.0);
            Assert.IsFalse(own.HasSky, "the body's data has no Mie phase, ground or exposure: the model's defaults apply");
            CloudHorizonSky.Rgb o = CloudHorizonSky.Display(Ray(own, 6371000.0, 92.0, 44.0, 180.0).Far, own.SkyExposureAt(92.0));
            Assert.IsTrue(o.B > o.R, "Earth's air: " + o.R + " " + o.G + " " + o.B);
            Assert.AreEqual(0.721, o.R, 0.015);
            Assert.AreEqual(0.803, o.G, 0.015);
            Assert.AreEqual(0.815, o.B, 0.015);
        }

        [TestMethod]
        public void TheRayOfAFarCloudIsTheLayersTangentFromTheCamera()
        {
            Assert.AreEqual(0.0, CloudHorizonSky.LayerElevation(92.0, 1500.0, KerbinRadius), 0.0, "a camera under the layer: its far end is on the horizontal");
            Assert.AreEqual(0.0, CloudHorizonSky.LayerElevation(1500.0, 1500.0, KerbinRadius), 0.0);
            double above = CloudHorizonSky.LayerElevation(10000.0, 1500.0, KerbinRadius);
            Assert.AreEqual(-Math.Acos((KerbinRadius + 1500.0) / (KerbinRadius + 10000.0)), above, 1e-12);
            Assert.AreEqual(-9.58, above / Deg, 0.01, "from 10 km the 1.5 km layer's tangent is 9.58 degrees below the horizontal");
        }

        [TestMethod]
        public void TheViewAnglesAreTheLightsElevationAndTheAzimuthOfTheViewFromIt()
        {
            double el, az;
            CloudHorizonSky.ViewAngles(Vector3.up, new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), out el, out az);
            Assert.AreEqual(45.0, el / Deg, 1e-4);
            Assert.AreEqual(0.0, az / Deg, 1e-3, "looking at the sun's azimuth");
            CloudHorizonSky.ViewAngles(Vector3.up, new Vector3(-1f, 0f, 0f), new Vector3(1f, 1f, 0f), out el, out az);
            Assert.AreEqual(180.0, az / Deg, 1e-3, "the sun behind the camera");
            CloudHorizonSky.ViewAngles(Vector3.up, new Vector3(0f, 0.2f, 1f), new Vector3(1f, 1f, 0f), out el, out az);
            Assert.AreEqual(90.0, az / Deg, 0.5, "a pitched view counts by its horizontal part");
            CloudHorizonSky.ViewAngles(Vector3.up, Vector3.up, new Vector3(1f, 1f, 0f), out el, out az);
            Assert.AreEqual(90.0, az / Deg, 1e-6, "straight up has no azimuth: the side view");
            CloudHorizonSky.ViewAngles(Vector3.up, new Vector3(1f, 0f, 0f), Vector3.up, out el, out az);
            Assert.AreEqual(90.0, el / Deg, 1e-4);
            Assert.AreEqual(0.0, az, 0.0, "a sun at the zenith has no azimuth");
        }

        [TestMethod]
        public void ATurningCameraDoesNotWorkTheHigherOrdersOutAgain()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            CloudHorizonSky.Medium m = CloudHorizonSky.MediumOf(kerbin, KerbinRadius);
            var ev = new CloudHorizonSky.Evaluator();
            CloudHorizonSky.Horizon first = ev.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 1.0);
            Assert.AreEqual(1, ev.Recomputed);
            CloudHorizonSky.Horizon plain = CloudHorizonSky.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 1.0);
            Assert.AreEqual(plain.Far.G, first.Far.G, 1e-12, "the first call is the uncached evaluation");
            for (int i = 0; i < 30; i++) ev.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 0.2 * i);
            Assert.AreEqual(1, ev.Recomputed, "azimuth, a few metres of height and a tenth of a degree of sun do not");
            ev.Evaluate(m, 100.0, 0.0, 44.1 * Deg, 0.5);
            Assert.AreEqual(1, ev.Recomputed);
            CloudHorizonSky.Horizon reused = ev.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 2.0);
            CloudHorizonSky.Horizon again = CloudHorizonSky.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 2.0);
            Assert.AreEqual(again.Far.G, reused.Far.G, 1e-12, "the cached orders are those of the same height and sun");
            ev.Evaluate(m, 92.0, 0.0, 46.0 * Deg, 0.5);
            Assert.AreEqual(2, ev.Recomputed, "two degrees of sun do");
            ev.Evaluate(m, 92.0 + 0.04 * m.RayleighHeightM + 10.0, 0.0, 46.0 * Deg, 0.5);
            Assert.AreEqual(3, ev.Recomputed, "so does a rise of 4 percent of the scale height");
        }

        [TestMethod]
        public void TheCostIsAFractionOfAMillisecondPerFrame()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            CloudHorizonSky.Medium m = CloudHorizonSky.MediumOf(kerbin, KerbinRadius);
            var ev = new CloudHorizonSky.Evaluator();
            ev.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 0.0);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 500; i++) ev.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 0.01 * i);
            double cachedMs = watch.Elapsed.TotalMilliseconds / 500.0;
            watch.Restart();
            for (int i = 0; i < 20; i++) CloudHorizonSky.Evaluate(m, 92.0, 0.0, 44.0 * Deg, 0.1 * i);
            double fullMs = watch.Elapsed.TotalMilliseconds / 20.0;
            Console.WriteLine("horizon sky: cached " + cachedMs.ToString("0.000") + " ms, with the higher orders " + fullMs.ToString("0.00") + " ms");
            Assert.IsTrue(cachedMs < 0.25, "cached " + cachedMs);
            Assert.IsTrue(fullMs < 10.0, "full " + fullMs);
        }

        [TestMethod]
        public void TheStageShowsTheSkysColourAndTheAmbientIsTheFloor()
        {
            CloudBridgeAir.Air kerbin = Node("Kerbin");
            const float white = 0.7244f;
            var view = new CloudHazePlan.SkyView { HeightM = 92.0, LayerAltitudeM = 1700.0, SunElevation = 44.0 * Deg, SunAzimuth = Math.PI };
            Color far, near;
            Assert.IsTrue(CloudHazePlan.SkyInscatter(new CloudHorizonSky.Evaluator(), new CloudHorizonSky.Evaluator(), kerbin, KerbinRadius, view, new Color(0.0f, 0.0f, 0.0f, 1f), white, 1f, 2.2f, out far, out near));
            CloudHorizonSky.Rgb sky = Shown(kerbin, Ray(kerbin, KerbinRadius, 92.0, 44.0, 180.0).Far, 92.0);
            Assert.AreEqual(sky.R, CloudOutputStage.Encode(far.r, white, 1f, 2.2f), 2e-3);
            Assert.AreEqual(sky.G, CloudOutputStage.Encode(far.g, white, 1f, 2.2f), 2e-3);
            Assert.AreEqual(sky.B, CloudOutputStage.Encode(far.b, white, 1f, 2.2f), 2e-3);
            // night with no moon: the ambient is all there is; with a moon (5 percent of the sun, from 40 degrees up behind the view) the air shows its light
            view.SunElevation = -30.0 * Deg;
            var ambient = new Color(0.01f, 0.01f, 0.012f, 1f);
            Assert.IsTrue(CloudHazePlan.SkyInscatter(new CloudHorizonSky.Evaluator(), new CloudHorizonSky.Evaluator(), kerbin, KerbinRadius, view, ambient, white, 1f, 2.2f, out far, out near));
            Assert.AreEqual(ambient.g, far.g, 1e-6f);
            view.MoonElevation = 40.0 * Deg; view.MoonAzimuth = Math.PI; view.MoonOverSun = new Color(0.05f, 0.05f, 0.055f, 1f);
            Color lit, litNear;
            Assert.IsTrue(CloudHazePlan.SkyInscatter(new CloudHorizonSky.Evaluator(), new CloudHorizonSky.Evaluator(), kerbin, KerbinRadius, view, ambient, white, 1f, 2.2f, out lit, out litNear));
            Assert.IsTrue(lit.g > far.g, "the moon's light in the air: " + lit.g + " against the ambient " + far.g);
            // no medium: the caller keeps the law of before
            Assert.IsFalse(CloudHazePlan.SkyInscatter(new CloudHorizonSky.Evaluator(), new CloudHorizonSky.Evaluator(), new CloudBridgeAir.Air(), KerbinRadius, view, ambient, white, 1f, 2.2f, out lit, out litNear));
        }
    }
}
