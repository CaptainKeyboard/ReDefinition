using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The horizon fade (E40): the pass's aerial perspective behind Scatterer's scattering with Scatterer's own coefficients (CloudHazePlan, ScattererAirReader,
    // CloudBridgeAir.FromScatterer). The numbers are the Kerbin node of StockScattererConfigs (m_betaR 0.01534 / 0.03571 / 0.08755 per km, BETA_MSca 0.01214, HR 3.84 km,
    // HM 1 km) and the camera of K-T2d start 55's above view (8.3 km over the sea, the deck 300 m up, 600 km radius).
    [TestClass]
    public class CloudHazePlanTests
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
        private static void FineTau(CloudBridgeAir.Air a, double depressionDeg, double deckM, out double distance, out double[] tau)
        {
            double th = depressionDeg * Math.PI / 180.0, oy = Radius + 8300.0, dx = Math.Cos(th), dy = -Math.Sin(th);
            double b = oy * dy, c = oy * oy - (Radius + deckM) * (Radius + deckM);
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
            // The deck 33 km away (15.45 degrees): the computed transmittance 0.78 / 0.59 / 0.30 of the design note (32 km: 0.78 / 0.60 / 0.31).
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
        public void TheDefaultIsOffEverywhereAndTheAutomaticRuleNeedsThePassBehindScatterer()
        {
            bool haze, auto;
            CloudHazePlan.Resolve(-1f, false, out haze, out auto);
            Assert.IsFalse(CloudHazePlan.AutoByDefault, "the default flips with the game rows of start 56, not before");
            Assert.IsFalse(haze);
            Assert.IsFalse(CloudHazePlan.Active(haze, auto, true), "default: no aerial perspective, behind Scatterer or not");
            CloudHazePlan.Resolve(-1f, true, out haze, out auto);
            Assert.IsTrue(CloudHazePlan.Active(haze, auto, false), "the setting alone is on in every order");
            CloudHazePlan.Resolve(2f, false, out haze, out auto);
            Assert.IsFalse(CloudHazePlan.Active(haze, auto, false), "automatic: off under order 0 (Scatterer fogs the cloud itself)");
            Assert.IsTrue(CloudHazePlan.Active(haze, auto, true), "automatic: on behind Scatterer");
            CloudHazePlan.Resolve(1f, false, out haze, out auto);
            Assert.IsTrue(CloudHazePlan.Active(haze, auto, false), "forced on");
            CloudHazePlan.Resolve(0f, true, out haze, out auto);
            Assert.IsFalse(CloudHazePlan.Active(haze, auto, true), "forced off wins over the setting");
        }

        [TestMethod]
        public void TheAirSourceFollowsTheKeyAndScattererBeingLoaded()
        {
            Assert.IsTrue(CloudHazePlan.UseScattererAir(CloudHazePlan.SourceFrom(-1f), true));
            Assert.IsFalse(CloudHazePlan.UseScattererAir(CloudHazePlan.SourceFrom(-1f), false));
            Assert.IsFalse(CloudHazePlan.UseScattererAir(CloudHazePlan.SourceFrom(0f), true));
            Assert.IsTrue(CloudHazePlan.UseScattererAir(CloudHazePlan.SourceFrom(1f), false), "asked for: the reader decides whether the node exists");
        }

        [TestMethod]
        public void TheKeysReachTheFarViewSettings()
        {
            Set(CloudLookTuning.TuneFarHaze, 2f);
            var far = new FarViewSettings();
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.Haze, "automatic: the module turns it on per frame");
            Assert.IsTrue(far.HazeAuto);
            Set(CloudLookTuning.TuneFarHaze, -1f);
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.Haze);
            Assert.IsFalse(far.HazeAuto);
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
        public void ADisplayColourIsDecodedSoThatAFullyFadedDeckShowsExactlyIt()
        {
            var display = new Color(0.620f, 0.711f, 0.749f, 1f);
            foreach (float white in new[] { 0.415f, 1f, 1.3f })
            {
                Color radiance = CloudHazePlan.Inscatter(1, Color.black, Color.black, Color.black, Vector3.one, 0.23f, display, white, 1f, 2.2f);
                Assert.AreEqual(display.r, CloudOutputStage.Encode(radiance.r, white, 1f, 2.2f), 1e-4f);
                Assert.AreEqual(display.g, CloudOutputStage.Encode(radiance.g, white, 1f, 2.2f), 1e-4f);
                Assert.AreEqual(display.b, CloudOutputStage.Encode(radiance.b, white, 1f, 2.2f), 1e-4f);
            }
            Color share = CloudHazePlan.Inscatter(0, new Color(3f, 2.9f, 2.7f), Color.black, Color.black, CloudBridgeAir.EarthRayleighPerM, 0.23f, display, 1f, 1f, 2.2f);
            Color old = CloudBridgeAir.Inscatter(new Color(3f, 2.9f, 2.7f), Color.black, Color.black, CloudBridgeAir.EarthRayleighPerM);
            Assert.AreEqual(old.r, share.r, 1e-6f, "source 0 with the default share is the law before this package");
            Assert.AreEqual(old.b, share.b, 1e-6f);
            Assert.AreEqual(0.5f, CloudOutputStage.Decode(CloudOutputStage.Encode(0.5f, 0.8f, 1.4f, 2.2f), 0.8f, 1.4f, 2.2f), 1e-5f);
            Assert.AreEqual(0.3f, CloudOutputStage.Decode(0.3f, 1f, 1f, 1f), 0f, "gamma 1 is the stage off");
        }

        // The fog mix at the numbers of start 55 (K-T2d, above view, bands 3 and 4 below the horizon row, order 1 and 0 of the same pixels) as an offline row: the pass's mix
        // C T + L (1 - T) with Scatterer's transmittance moves the deck of 0.48 neutral (ord1) toward the haze, and the lift is of the size the game measured for Scatterer's own
        // air (ord0 minus ord1 +0.17 / +0.20 / +0.23): the sign and order of magnitude, not the pixel values (the in-scatter colour of the game is not known offline).
        [TestMethod]
        public void TheFogMixLiftsTheDeckTowardTheHazeByTheSizeScattererDidInTheGame()
        {
            string why;
            CloudBridgeAir.Air a = ScattererAirReader.Read(Roots(KerbinNode), "Kerbin", out why);
            double dist;
            double[] fine;
            FineTau(a, 14.18, 300.0, out dist, out fine);
            double[] deckEncoded = { 0.486, 0.482, 0.483 }, ord0 = { 0.654, 0.678, 0.712 }, hazeEncoded = { 0.620, 0.711, 0.749 };
            for (int c = 0; c < 3; c++)
            {
                double t = Math.Exp(-fine[c]);
                double deck = Math.Pow(deckEncoded[c], 2.2), haze = Math.Pow(hazeEncoded[c], 2.2);
                double mixed = Math.Pow(FarViewMath.Fog(deck, 1.0, t, haze), 1.0 / 2.2);
                Assert.IsTrue(mixed > deckEncoded[c] && mixed < hazeEncoded[c], "channel " + c + " moves toward the haze and not past it: " + mixed);
                Assert.IsTrue(ord0[c] > deckEncoded[c], "ord0 is the lifted deck of the game (reference of the lift, not asserted against the mix)");
            }
        }
    }
}
