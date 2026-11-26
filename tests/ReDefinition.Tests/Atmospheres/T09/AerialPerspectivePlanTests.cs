using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The horizon fade (E40): the pass's aerial perspective behind Scatterer's scattering with Scatterer's own coefficients (AerialPerspectivePlan, ScattererAirReader,
    // CloudBridgeAir.FromScatterer). The numbers are the Kerbin node of StockScattererConfigs (m_betaR 0.01534 / 0.03571 / 0.08755 per km, BETA_MSca 0.01214, HR 3.84 km,
    // HM 1 km) and the camera of K-T2d start 55's above view (8.3 km over the sea, the low stratiform layer 300 m up, 600 km radius).
    [TestClass]
    public class AerialPerspectivePlanTests
    {
        private const string KerbinNode = @"
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
    }
}";
        private const double Radius = 600000.0;

        private static ConfigNode[] Roots(string text)
        {
            return new[] { ConfigNode.Parse(text).GetNode("Scatterer_atmosphere") };
        }

        [TestMethod]
        public void TheNodeIsReadIntoPerMetreCoefficientsAndTheMieExtinctionIsTheScatteringOver0Point9()
        {
            string why;
            CloudBridgeAir.Air a = ScattererAirReader.Read(Roots(KerbinNode), "Kerbin", out why);
            Assert.IsTrue(a.Valid, why);
            Assert.AreEqual(1.53407799e-5f, a.RayleighPerM.x, 1e-9f);
            Assert.AreEqual(3.57074998e-5f, a.RayleighPerM.y, 1e-9f);
            Assert.AreEqual(8.75488967e-5f, a.RayleighPerM.z, 1e-9f);
            Assert.AreEqual(0.0121406196e-3f / 0.9f, a.MiePerM, 1e-10f, "SkyNode: betaMEx = BETA_MSca / 1000 / 0.9");
            Assert.AreEqual(3839.99991f, a.RayleighHeightM, 0.01f);
            Assert.AreEqual(1000f, a.MieHeightM, 1e-3f);
        }

        [TestMethod]
        public void TheProbeLightUnderADeckIsTheDiffuseShareOfTheDepthOverhead()
        {
            // PRUEFPROTOKOLL 295: a clear sky keeps the probe, a deck of vertical tau 10 passes about half, a nimbostratus of tau 66 about an eighth
            Assert.AreEqual(1.0, OwnCloudProbeLight.DiffuseShare(0f), 1e-9);
            Assert.AreEqual(1.0 / (1.0 + 0.11 * 10.0), OwnCloudProbeLight.DiffuseShare(10f), 1e-6);
            Assert.IsTrue(OwnCloudProbeLight.DiffuseShare(66f) < 0.13);
            Assert.AreEqual(1.0, OwnCloudProbeLight.DiffuseShare(-1f), 1e-9, "a negative depth is none");
        }

        [TestMethod]
        public void ANodeThatIsMissingOrBrokenGivesNoAirAndAReason()
        {
            string why;
            Assert.IsFalse(ScattererAirReader.Read(Roots(KerbinNode), "Duna", out why).Valid);
            StringAssert.Contains(why, "Duna");
            Assert.IsFalse(ScattererAirReader.Read((ConfigNode[])null, "Kerbin", out why).Valid);
            Assert.IsFalse(ScattererAirReader.Read(new ConfigNode[0], "Kerbin", out why).Valid);
            Assert.IsFalse(ScattererAirReader.Read(Roots(KerbinNode.Replace("m_betaR = 0.0153407799,0.0357074998,0.0875488967", "m_betaR = 0.01,0.02")), "Kerbin", out why).Valid);
            StringAssert.Contains(why, "m_betaR");
            Assert.IsFalse(ScattererAirReader.Read(Roots(KerbinNode.Replace("HM = 1", "HM = 0")), "Kerbin", out why).Valid);
            Assert.IsFalse(ScattererAirReader.Read(Roots(KerbinNode.Replace("HR = 3.83999991", "")), "Kerbin", out why).Valid);
            StringAssert.Contains(why, "HR");
            Assert.IsFalse(ScattererAirReader.Read(Roots(KerbinNode.Replace("0.0875488967", "nan")), "Kerbin", out why).Valid);
        }

        [TestMethod]
        public void TheFirstNodeThatNamesTheBodyWinsAndAnotherBodysNodeIsSkipped()
        {
            string two = KerbinNode.Replace("name = Kerbin", "name = Eve").Replace("0.0153407799", "0.5");
            string why;
            CloudBridgeAir.Air kerbin = ScattererAirReader.Read(new[] { Roots(two)[0], Roots(KerbinNode)[0] }, "Kerbin", out why);
            Assert.IsTrue(kerbin.Valid, why);
            Assert.AreEqual(1.534e-5f, kerbin.RayleighPerM.x, 1e-8f);
        }

        // The pass's six-piece integral against a fine midpoint integral along the same spherical ray: the pieces cut Scatterer's thin aerosol layer (1 km) coarsely.
        private static void FineTau(CloudBridgeAir.Air a, double depressionDeg, double layerM, out double distance, out double[] tau)
        {
            double th = depressionDeg * Math.PI / 180.0, oy = Radius + 8300.0, dx = Math.Cos(th), dy = -Math.Sin(th);
            double b = oy * dy, c = oy * oy - (Radius + layerM) * (Radius + layerM);
            distance = -b - Math.Sqrt(b * b - c);
            const int n = 20000;
            double r = 0.0, m = 0.0, ds = distance / n;
            for (int i = 0; i < n; i++)
            {
                double s = (i + 0.5) * ds, h = Math.Max(0.0, Math.Sqrt((s * dx) * (s * dx) + (oy + s * dy) * (oy + s * dy)) - Radius);
                r += Math.Exp(-h / a.RayleighHeightM) * ds;
                m += Math.Exp(-h / a.MieHeightM) * ds;
            }
            tau = new[] { a.RayleighPerM.x * r + a.MiePerM * m, a.RayleighPerM.y * r + a.MiePerM * m, a.RayleighPerM.z * r + a.MiePerM * m };
        }

        [TestMethod]
        public void TheSixPieceIntegralFollowsTheFineOneWithinAThirdOfAPercentAndTheTransmittanceIsTheComputedOne()
        {
            string why;
            CloudBridgeAir.Air a = ScattererAirReader.Read(Roots(KerbinNode), "Kerbin", out why);
            foreach (double deg in new[] { 10.4, 12.9, 15.45, 19.7, 30.0 })
            {
                double dist;
                double[] fine;
                FineTau(a, deg, 300.0, out dist, out fine);
                double th = deg * Math.PI / 180.0, tr, tg, tb;
                FarViewMath.AirTau(Radius, 0.0, Radius + 8300.0, 0.0, Math.Cos(th), -Math.Sin(th), 0.0, dist, a.RayleighPerM.x, a.RayleighPerM.y, a.RayleighPerM.z, a.MiePerM,
                                   a.RayleighHeightM, a.MieHeightM, 1.0, out tr, out tg, out tb);
                Assert.AreEqual(fine[0], tr, 0.006 * fine[0], deg + " red");
                Assert.AreEqual(fine[1], tg, 0.006 * fine[1], deg + " green");
                Assert.AreEqual(fine[2], tb, 0.006 * fine[2], deg + " blue");
            }
            // The low stratiform layer 33 km away (15.45 degrees): the computed transmittance 0.78 / 0.59 / 0.30 of the design note (32 km: 0.78 / 0.60 / 0.31).
            double d, t;
            double[] tau;
            FineTau(a, 15.45, 300.0, out d, out tau);
            Assert.AreEqual(33.1, d / 1000.0, 0.3);
            Assert.AreEqual(0.78, Math.Exp(-tau[0]), 0.01);
            Assert.AreEqual(0.59, Math.Exp(-tau[1]), 0.01);
            Assert.AreEqual(0.30, Math.Exp(-tau[2]), 0.01);
            t = d;
            Assert.IsTrue(t > 0.0);
        }

        [TestMethod]
        public void ScattererAirHasAboutTwiceTheOpticalDepthOfThePassesOwnAirOnKerbin()
        {
            string why;
            CloudBridgeAir.Air sc = ScattererAirReader.Read(Roots(KerbinNode), "Kerbin", out why);
            // Kerbin's own air as the log of start 52 states it: Rayleigh 5.8e-6 / 3.3e-5 per m, scale height 6067 m (Earth's coefficients: Kerbin's sea-level density is 1.2 kg/m3).
            var own = new CloudBridgeAir.Air
            {
                RayleighPerM = CloudBridgeAir.EarthRayleighPerM, MiePerM = CloudBridgeAir.EarthMiePerM, RayleighHeightM = 6067f,
                MieHeightM = 6067f * CloudBridgeAir.MieToRayleighHeight, Valid = true
            };
            double dist;
            double[] fine;
            FineTau(sc, 14.18, 300.0, out dist, out fine);
            double th = 14.18 * Math.PI / 180.0, tr, tg, tb;
            FarViewMath.AirTau(Radius, 0.0, Radius + 8300.0, 0.0, Math.Cos(th), -Math.Sin(th), 0.0, dist, own.RayleighPerM.x, own.RayleighPerM.y, own.RayleighPerM.z, own.MiePerM,
                               own.RayleighHeightM, own.MieHeightM, 1.0, out tr, out tg, out tb);
            Assert.AreEqual(2.14, fine[0] / tr, 0.05, "the design note: tau ratio 2.0 to 2.2 per channel");
            Assert.AreEqual(2.0, fine[2] / tb, 0.05);
        }

        [TestMethod]
        public void TheDefaultIsAutomaticAndNeedsThePassBehindScatterer()
        {
            bool aerialPerspective, auto;
            AerialPerspectivePlan.Resolve(-1f, false, out aerialPerspective, out auto);
            Assert.IsTrue(AerialPerspectivePlan.AutoByDefault, "a visible look feature is on by default");
            Assert.IsFalse(aerialPerspective);
            Assert.IsTrue(AerialPerspectivePlan.Active(aerialPerspective, auto, true), "default: on behind Scatterer");
            Assert.IsFalse(AerialPerspectivePlan.Active(aerialPerspective, auto, false), "default: off where Scatterer fogs the cloud itself");
            AerialPerspectivePlan.Resolve(-1f, true, out aerialPerspective, out auto);
            Assert.IsTrue(AerialPerspectivePlan.Active(aerialPerspective, auto, false), "the setting alone is on in every order");
            AerialPerspectivePlan.Resolve(2f, false, out aerialPerspective, out auto);
            Assert.IsFalse(AerialPerspectivePlan.Active(aerialPerspective, auto, false), "automatic: off under order 0 (Scatterer fogs the cloud itself)");
            Assert.IsTrue(AerialPerspectivePlan.Active(aerialPerspective, auto, true), "automatic: on behind Scatterer");
            AerialPerspectivePlan.Resolve(1f, false, out aerialPerspective, out auto);
            Assert.IsTrue(AerialPerspectivePlan.Active(aerialPerspective, auto, false), "forced on");
            AerialPerspectivePlan.Resolve(0f, true, out aerialPerspective, out auto);
            Assert.IsFalse(AerialPerspectivePlan.Active(aerialPerspective, auto, true), "forced off wins over the setting");
        }

        [TestMethod]
        public void TheAirSourceFollowsTheKeyAndScattererBeingLoaded()
        {
            Assert.IsTrue(AerialPerspectivePlan.UseScattererAir(AerialPerspectivePlan.SourceFrom(-1f), true));
            Assert.IsFalse(AerialPerspectivePlan.UseScattererAir(AerialPerspectivePlan.SourceFrom(-1f), false));
            Assert.IsFalse(AerialPerspectivePlan.UseScattererAir(AerialPerspectivePlan.SourceFrom(0f), true));
            Assert.IsTrue(AerialPerspectivePlan.UseScattererAir(AerialPerspectivePlan.SourceFrom(1f), false), "asked for: the reader decides whether the node exists");
        }

        [TestMethod]
        public void TheKeysReachTheFarViewSettings()
        {
            Set(CloudLookTuning.TuneAerialPerspective, 2f);
            var far = new FarViewSettings();
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.AerialPerspective, "automatic: the module turns it on per frame");
            Assert.IsTrue(far.AerialPerspectiveAuto);
            Set(CloudLookTuning.TuneAerialPerspective, -1f);
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.AerialPerspective);
            Assert.IsTrue(far.AerialPerspectiveAuto, "the default is automatic");
            Set(CloudLookTuning.TuneAerialPerspective, 0f);
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.AerialPerspective);
            Assert.IsFalse(far.AerialPerspectiveAuto, "cloud.aerialPerspective 0 is the off switch");
        }

        private static int frameCounter = 5000000;

        private static void Set(string key, float value)
        {
            Tuning.SetOverride(key, value);
            Tuning.Instance.BeginFrame(++frameCounter);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frameCounter);
        }

        [TestMethod]
        public void ADisplayColourIsDecodedSoThatAFullyFadedLowStratiformShowsExactlyIt()
        {
            var display = new Color(0.620f, 0.711f, 0.749f, 1f);
            foreach (float white in new[] { 0.415f, 1f, 1.3f })
            {
                Color radiance = AerialPerspectivePlan.Inscatter(1, Color.black, Color.black, Color.black, Vector3.one, 0.23f, display, white, 1f, 2.2f);
                Assert.AreEqual(display.r, CloudOutputStage.Encode(radiance.r, white, 1f, 2.2f), 1e-4f);
                Assert.AreEqual(display.g, CloudOutputStage.Encode(radiance.g, white, 1f, 2.2f), 1e-4f);
                Assert.AreEqual(display.b, CloudOutputStage.Encode(radiance.b, white, 1f, 2.2f), 1e-4f);
            }
            Color share = AerialPerspectivePlan.Inscatter(0, new Color(3f, 2.9f, 2.7f), Color.black, Color.black, CloudBridgeAir.EarthRayleighPerM, 0.23f, display, 1f, 1f, 2.2f);
            Color old = CloudBridgeAir.Inscatter(new Color(3f, 2.9f, 2.7f), Color.black, Color.black, CloudBridgeAir.EarthRayleighPerM);
            Assert.AreEqual(old.r, share.r, 1e-6f, "source 0 with the default share is the law before this package");
            Assert.AreEqual(old.b, share.b, 1e-6f);
            Assert.AreEqual(0.5f, CloudOutputStage.Decode(CloudOutputStage.Encode(0.5f, 0.8f, 1.4f, 2.2f), 0.8f, 1.4f, 2.2f), 1e-5f);
            Assert.AreEqual(0.3f, CloudOutputStage.Decode(0.3f, 1f, 1f, 1f), 0f, "gamma 1 is the stage off");
        }

        // E40 T1 tint, the numbers of K-T2 start 61 (frame 5623, the ground view): the pass's sun (2.807, 2.768, 2.827), the white point's tint at the lowest layer's lower quarter
        // (1.036, 0.994, 0.949), the stage's white 0.7244, gamma 2.2, Kerbin's node. Measured in the game frames: the cloud pixels of the horizon band read blue over red 1.5 to 1.6 against 1.0
        // for True Volumetric Clouds, the low stratiform layers overhead 1.26 against 1.18; the chain (a neutral dark cloud of 0.45 and the pass's aerial perspective at 25 km) gives 1.50 offline. The rows below pin the
        // terms of the aerial perspective colour: the in-scatter's own blue, the white point's share of it, and the blend of the short-path and the horizon colour.
        private static readonly Color Start61Sun = new Color(2.807f, 2.768f, 2.827f, 1f);
        private static readonly Color Start61Tint = new Color(1.036f, 0.994f, 0.949f, 1f);
        private const float Start61White = 0.7244f;
        private static readonly Vector3 KerbinRayleigh = new Vector3(1.53407799e-5f, 3.57074998e-5f, 8.75488967e-5f);

        private static float BlueOverRed(float r, float b)
        {
            return b / r;
        }

        [TestMethod]
        public void TheBlueExponentIsTheRayleighRatioToItsPowerAndZeroIsTheSunsOwnColour()
        {
            Color grey = CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f, 0f);
            Assert.AreEqual(Start61Sun.r * 0.23f, grey.r, 1e-6f);
            Assert.AreEqual(Start61Sun.g * 0.23f, grey.g, 1e-6f);
            Assert.AreEqual(Start61Sun.b * 0.23f, grey.b, 1e-6f, "exponent 0: the sun's colour times the share, nothing added");
            Color blue = CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f, 0.35f);
            Color before = CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f);
            Assert.AreEqual(before.b, blue.b, 1e-6f, "the exponent of before is 0.35");
            float expected = (float)Math.Pow(KerbinRayleigh.z / KerbinRayleigh.x, 0.35) * Start61Sun.b / Start61Sun.r;
            Assert.AreEqual(expected, BlueOverRed(blue.r, blue.b), 1e-4f, "blue over red of the in-scatter follows the Rayleigh ratio to 0.35");
            Assert.AreEqual(CloudBridgeAir.BlueExponent, CloudRules.DefaultAerialPerspectiveBlue, 0f);
            Assert.AreEqual(0f, CloudRules.DefaultAerialPerspectiveNearBlue, 0f);
        }

        [TestMethod]
        public void TheInscatterLeavesTheWhitePointOut()
        {
            // The stage divides each channel by the tint; with the in-scatter multiplied by it first, the display value is that of a stage without tint.
            Color radiance = CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f);
            Color exempt = AerialPerspectivePlan.ExemptFromWhitePoint(radiance, Start61Tint, 2.2f);
            float[] tints = { Start61Tint.r, Start61Tint.g, Start61Tint.b };
            float[] without = { radiance.r, radiance.g, radiance.b }, with = { exempt.r, exempt.g, exempt.b };
            for (int i = 0; i < 3; i++)
            {
                float displayed = CloudOutputStage.Encode(with[i], Start61White, tints[i], 1f, 2.2f);
                float reference = CloudOutputStage.Encode(without[i], Start61White, 1f, 2.2f);
                Assert.AreEqual(reference, displayed, 1e-5f, "channel " + i);
            }
            // Measured in the frame: with the tint in the stage the horizon colour was 0.568 / 0.657 / 0.782 (blue over red 1.378); without it 0.577 / 0.656 / 0.764 (1.324).
            float b = CloudOutputStage.Encode(radiance.b, Start61White, Start61Tint.b, 1f, 2.2f) / CloudOutputStage.Encode(radiance.r, Start61White, Start61Tint.r, 1f, 2.2f);
            float c = CloudOutputStage.Encode(radiance.b, Start61White, 1f, 1f, 2.2f) / CloudOutputStage.Encode(radiance.r, Start61White, 1f, 1f, 2.2f);
            Assert.AreEqual(1.378f, b, 0.01f);
            Assert.AreEqual(1.324f, c, 0.01f);
            Color off = AerialPerspectivePlan.ExemptFromWhitePoint(radiance, Start61Tint, 1f);
            Assert.AreEqual(radiance.b, off.b, 0f, "the stage off: nothing is divided, nothing is changed");
        }

        // E40 FL far: the horizon of Scatterer's sky in the frame of start 61 without our clouds (the stage's display values at row 740 of 960, the sky just above the horizon) is 0.663 / 0.784 / 0.820;
        // the Rayleigh power law reaches 0.577 / 0.656 / 0.764 (green 0.13 short), and a far cloud faded into a violet darker than the sky. Kerbin's gain (aerialPerspectiveTintR / G / B 1.35 / 1.48 / 1.17) puts
        // the colour a far cloud fades into within 0.02 of the sky's, and a gain of one leaves it as it was.
        [TestMethod]
        public void TheGainOnTheFarColourPutsItOnTheHorizonOfTheSky()
        {
            Color law = AerialPerspectivePlan.ExemptFromWhitePoint(CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f, 0.35f), Start61Tint, 2.2f);
            Color same = AerialPerspectivePlan.WithTint(law, Vector3.one);
            Assert.AreEqual(law.g, same.g, 0f, "a gain of one: the law as it was");
            Color tinted = AerialPerspectivePlan.WithTint(law, new Vector3(1.35f, 1.48f, 1.17f));
            float[] sky = { 0.663f, 0.784f, 0.820f };
            float[] shown = { CloudOutputStage.Encode(tinted.r, Start61White, 1f, 1f, 2.2f), CloudOutputStage.Encode(tinted.g, Start61White, 1f, 1f, 2.2f), CloudOutputStage.Encode(tinted.b, Start61White, 1f, 1f, 2.2f) };
            float[] before = { CloudOutputStage.Encode(law.r, Start61White, 1f, 1f, 2.2f), CloudOutputStage.Encode(law.g, Start61White, 1f, 1f, 2.2f), CloudOutputStage.Encode(law.b, Start61White, 1f, 1f, 2.2f) };
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(sky[i], shown[i], 0.03f, "channel " + i + " with the gain");
                Assert.IsTrue(Math.Abs(sky[i] - shown[i]) < Math.Abs(sky[i] - before[i]), "channel " + i + " is nearer the sky than the law was");
            }
            Assert.IsTrue(sky[1] - before[1] > 0.1f, "the law's green was more than 0.1 short");
        }

        [TestMethod]
        public void TheNearSourceIsGreyAndTheHorizonKeepsItsColour()
        {
            Color near = AerialPerspectivePlan.ExemptFromWhitePoint(CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f, 0f), Start61Tint, 2.2f);
            Color far = AerialPerspectivePlan.ExemptFromWhitePoint(CloudBridgeAir.Inscatter(Start61Sun, Color.black, Color.black, KerbinRayleigh, 0.23f, 0.35f), Start61Tint, 2.2f);
            var cloud = new Color(0.2f, 0.2f, 0.2f, 1f);
            // A cloud 25 km away: nothing of the blue gets through (T 0.66 / 0.46 / 0.19 at the pass's air), the pixel is the far colour's mix; at the end of the air (T 0) exactly it.
            Color gone = AerialPerspectivePlan.AerialMix(cloud, 1f, Vector3.zero, near, far);
            Assert.AreEqual(far.r, gone.r, 1e-6f);
            Assert.AreEqual(far.b, gone.b, 1e-6f, "a cloud behind all the air fades into the horizon's colour, as before the near colour existed");
            // A thin air (the same near and far colour) is the single-colour mix of before.
            var t = new Vector3(0.93f, 0.88f, 0.76f);
            Color single = AerialPerspectivePlan.AerialMix(cloud, 1f, t, far, far);
            Assert.AreEqual(cloud.r * t.x + far.r * (1f - t.x), single.r, 1e-6f);
            Assert.AreEqual(cloud.b * t.z + far.b * (1f - t.z), single.b, 1e-6f);
            // A low stratiform layer a few kilometres overhead (T 0.93 / 0.88 / 0.76): the aerial perspective it gets is bluer over red with the single horizon colour than with the near colour.
            Color farOnly = AerialPerspectivePlan.AerialMix(cloud, 1f, t, far, far), blended = AerialPerspectivePlan.AerialMix(cloud, 1f, t, near, far);
            Assert.IsTrue(blended.b / blended.r < farOnly.b / farOnly.r, "the near colour takes the second blue out of the low stratiform layer's aerial perspective");
            Assert.IsTrue(blended.b > blended.r, "the air's own extinction still makes it blue");
        }

        [TestMethod]
        public void TheAirUnderADeckGetsOnlyTheFluxTheDeckPassesOn()
        {
            // PRUEFPROTOKOLL 272: a clear sky passes the whole flux; a deck of tau 26 along the sun (T e^-26) a quarter (1 / (1 + 0.11 x 26)); T 0 stays finite
            Assert.AreEqual(1f, AerialPerspectivePlan.LitShare(1f), 1e-6f);
            Assert.AreEqual(1f / (1f + 0.11f * 26f), AerialPerspectivePlan.LitShare((float)Math.Exp(-26.0)), 1e-4f);
            float dark = AerialPerspectivePlan.LitShare(0f);
            Assert.IsTrue(dark > 0.05f && dark < 0.2f, "T 0: " + dark);
            // the in-scatter scales with the share; the cloud's own colour times the air's transmittance does not
            var cloud = new Color(0.2f, 0.2f, 0.2f, 1f);
            var t = new Vector3(0.93f, 0.88f, 0.76f);
            Color near = new Color(0.5f, 0.55f, 0.6f, 1f), far = new Color(0.4f, 0.5f, 0.7f, 1f);
            Color lit = AerialPerspectivePlan.AerialMix(cloud, 1f, t, near, far, 1f), shaded = AerialPerspectivePlan.AerialMix(cloud, 1f, t, near, far, 0.25f);
            Assert.AreEqual(AerialPerspectivePlan.AerialMix(cloud, 1f, t, near, far).b, lit.b, 1e-7f);
            Assert.AreEqual(cloud.b * t.z + 0.25f * (lit.b - cloud.b * t.z), shaded.b, 1e-6f);
        }

        [TestMethod]
        public void TheFlareTakesTheDirectTransmissionClampedToOne()
        {
            // master plan 46.1: Scatterer's flare times the texture's alpha; the direct beam under a tau-26 deck is nothing, a clear sky everything
            Assert.AreEqual(1f, ScattererFlareClouds.Encode(1f));
            Assert.AreEqual(1f, ScattererFlareClouds.Encode(1.2f));
            Assert.AreEqual(0f, ScattererFlareClouds.Encode(-0.1f));
            Assert.IsTrue(ScattererFlareClouds.Encode((float)Math.Exp(-26.0)) < 1e-6f);
        }

        // The fog mix at the numbers of start 55 (K-T2d, above view, bands 3 and 4 below the horizon row, order 1 and 0 of the same pixels) as an offline row: the pass's mix
        // C T + L (1 - T) with Scatterer's transmittance moves the low stratiform layer of 0.48 neutral (ord1) toward the aerial perspective colour, and the lift is of the size the game measured for Scatterer's own
        // air (ord0 minus ord1 +0.17 / +0.20 / +0.23): the sign and order of magnitude, not the pixel values (the in-scatter colour of the game is not known offline).
        [TestMethod]
        public void TheFogMixLiftsTheLowStratiformTowardTheAerialPerspectiveByTheSizeScattererDidInTheGame()
        {
            string why;
            CloudBridgeAir.Air a = ScattererAirReader.Read(Roots(KerbinNode), "Kerbin", out why);
            double dist;
            double[] fine;
            FineTau(a, 14.18, 300.0, out dist, out fine);
            double[] lowStratiformEncoded = { 0.486, 0.482, 0.483 }, ord0 = { 0.654, 0.678, 0.712 }, inscatterEncoded = { 0.620, 0.711, 0.749 };
            for (int c = 0; c < 3; c++)
            {
                double t = Math.Exp(-fine[c]);
                double lowStratiform = Math.Pow(lowStratiformEncoded[c], 2.2), inscatter = Math.Pow(inscatterEncoded[c], 2.2);
                double mixed = Math.Pow(FarViewMath.Fog(lowStratiform, 1.0, t, inscatter), 1.0 / 2.2);
                Assert.IsTrue(mixed > lowStratiformEncoded[c] && mixed < inscatterEncoded[c], "channel " + c + " moves toward the in-scatter colour and not past it: " + mixed);
                Assert.IsTrue(ord0[c] > lowStratiformEncoded[c], "ord0 is the lifted low stratiform layer of the game (reference of the lift, not asserted against the mix)");
            }
        }
    }
}
