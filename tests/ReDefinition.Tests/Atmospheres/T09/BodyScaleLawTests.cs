using System;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // P1 (planet independence): what the weather generator and the cloud stage derive from the body's own data, on two bodies of different radius and atmosphere: the home planet (radius 600 km,
    // atmosphere 70 km, troposphere 10.5 km) and a second body (the stock ocean moon: radius 500 km, atmosphere 50 km, troposphere 7.5 km, a day twice as long).
    [TestClass]
    public class BodyScaleLawTests
    {
        private static BodyParams Params(bool second)
        {
            BodyFacts f = second ? FieldFixture.SecondFacts() : FieldFixture.HomeFacts();
            var climate = new BodyClimate(second ? "Secondworld" : "Testworld", FieldFixture.BodyIndex, f.RadiusM, FieldFixture.HomeYear, f.DaySeconds, BodyStatus.Full, 1);
            return FieldFixture.Params(climate, f, second ? "Laythe" : "Kerbin");
        }

        // ---- the systems: the Rossby law ----

        [TestMethod]
        public void TheSystemScaleIsTheDeformationRadiusOfTheBodyNotAFractionOfItsRadius()
        {
            foreach (bool second in new[] { false, true })
            {
                BodyParams p = Params(second);
                var d = new BodyDerived(p);
                // L_d = N H_trop / f at 45 degrees, from the body's gravity, temperature, lapse rate, tropopause and rotation
                double ld = d.Brunt * p.TropopauseM / d.F45;
                Assert.AreEqual(ld, d.DeformationRadiusM, 1e-6 * ld);
                double expected = Math.Max(p.SystemRadiusMinShare * p.RadiusM, Math.Min(p.SystemRadiusMaxShare * p.RadiusM, p.SystemRadiusFactor * ld));
                Assert.AreEqual(expected, d.SystemRadiusM, 1e-6 * expected, "the system radius is 0.75 deformation radii, kept between 0.12 and 0.45 of the body's radius");
                // the wavelength of a system is 3 r times 0.7 to 1.5: a quarter to a ninth of the circumference, not a third to three quarters of it
                double circumference = 2.0 * Math.PI * p.RadiusM;
                double lambdaMin = 3.0 * d.SystemRadiusM * p.SystemLambdaMin, lambdaMax = 3.0 * d.SystemRadiusM * p.SystemLambdaMax;
                // (the upper bound is the clamp: 3 x 0.45 x 1.5 / 2 pi of the circumference)
                Assert.IsTrue(lambdaMax <= 0.33 * circumference && lambdaMin > 0.05 * circumference,(second ? "second" : "home") + " body: wavelengths " + lambdaMin / 1000 + " to " + lambdaMax / 1000 + " km of " + circumference / 1000);
                Assert.IsTrue(d.SlotsPerHemisphere >= 3 && d.SlotsPerHemisphere <= 8);
            }
            double rHome = new BodyDerived(Params(false)).SystemRadiusM, rSecond = new BodyDerived(Params(true)).SystemRadiusM;
            Assert.AreNotEqual(Math.Round(rHome), Math.Round(rSecond), "another body, another system size");
            // a body that turns ten times slower has a deformation radius ten times larger (f is ten times smaller): bounded by 0.45 of the radius
            BodyParams slow = Params(false).Clone();
            slow.RotationPeriodS *= 10.0;
            Assert.AreEqual(slow.SystemRadiusMaxShare * slow.RadiusM, new BodyDerived(slow).SystemRadiusM, 1.0, "a slow rotator's system fills the allowed share of the radius");
        }

        [TestMethod]
        public void TheSystemLeversAreTheBodysOwnData()
        {
            BodyParams p = Params(false).Clone();
            double r = new BodyDerived(p).SystemRadiusM;
            p.SystemRadiusFactor *= 0.5;
            Assert.AreEqual(0.5 * r, new BodyDerived(p).SystemRadiusM, 1e-6 * r, "the share of the deformation radius is a lever of the body's GENERATOR node (LEVERS)");
        }

        // ---- the smaller structures: lengths in tropopause depths, speeds in jet speeds, heights in proportion ----

        [TestMethod]
        public void ConvectiveLengthsAreMultiplesOfTheTroposphereDepthAndSpeedsOfTheJetSpeed()
        {
            foreach (bool second in new[] { false, true })
            {
                BodyParams p = Params(second);
                var d = new BodyDerived(p);
                double h = p.TropopauseM, a = p.RadiusM;
                string tag = second ? "second body" : "home body";
                Assert.AreEqual(h, second ? 7500.0 : 10500.0, 1e-6, tag + ": the tropopause is 0.15 of the atmosphere's depth");
                Assert.AreEqual(BodyDerived.CellLatticeDepths * h, d.CellLatticeM, 1e-6, tag + ": the cell lattice");
                Assert.AreEqual(BodyDerived.TradePatchDepths1 * h / a, d.TradePatchRad1, 1e-12, tag + ": trade-wind cumulus patches");
                Assert.AreEqual(BodyDerived.TradePatchDepths2 * h / a, d.TradePatchRad2, 1e-12);
                Assert.AreEqual(BodyDerived.RainCellDepths * h / a, d.RainCellRad, 1e-12, tag + ": rain cells");
                Assert.AreEqual(BodyDerived.GustCellDepths * h / a, d.GustCellRad, 1e-12, tag + ": gust cells");
                Assert.AreEqual(BodyDerived.TradeDriftJetShare * d.JetSpeed0, d.TradeDriftMs, 1e-9, tag + ": trade pattern drift");
                Assert.AreEqual(BodyDerived.RainDriftJetShare * d.JetSpeed0, d.RainDriftMs, 1e-9);
                Assert.AreEqual(BodyDerived.ItczWaveJetShare * d.JetSpeed0, d.ItczWaveMs, 1e-9);
                Assert.AreEqual(h / BodyDerived.CalibrationTropopauseM, d.HeightScale, 1e-12, tag + ": heights in proportion to the troposphere");
            }
            // the second body's structures are in the proportion of the troposphere depths (0.714), not of the radii (0.833)
            var home = new BodyDerived(Params(false));
            var other = new BodyDerived(Params(true));
            Assert.AreEqual(7.5 / 10.5, other.CellLatticeM / home.CellLatticeM, 1e-9);
            Assert.AreEqual(7.5 / 10.5 * 600.0 / 500.0, other.RainCellRad / home.RainCellRad, 1e-9, "an angle: depth over radius");
        }

        [TestMethod]
        public void TheHistoricalProfilesKeepTheFixedNumbers()
        {
            BodyParams p = Params(false);
            foreach (BodyParams old in new[] { p.Round3(), p.Round3().Round2(), p.Round3().Round2().Round1(), p.Signed() })
            {
                var d = new BodyDerived(old);
                Assert.IsFalse(old.BodyScaledLengths);
                Assert.AreEqual(0.09, d.TradePatchRad1);
                Assert.AreEqual(0.15, d.TradePatchRad2);
                Assert.AreEqual(0.035, d.RainCellRad);
                Assert.AreEqual(0.04, d.GustCellRad);
                Assert.AreEqual(4.0, d.TradeDriftMs);
                Assert.AreEqual(12.0, d.RainDriftMs);
                Assert.AreEqual(WeatherGenerator.ItczWaveSpeedMs, d.ItczWaveMs);
                Assert.AreEqual(1.0, d.HeightScale);
            }
            // on the home body the new numbers are within a few per cent of the old ones: the look of the home planet does not change
            var home = new BodyDerived(p);
            Assert.AreEqual(1.0, home.HeightScale, 1e-12);
            Assert.AreEqual(0.09, home.TradePatchRad1, 0.09 * 0.04);
            Assert.AreEqual(0.15, home.TradePatchRad2, 0.15 * 0.04);
            Assert.AreEqual(0.035, home.RainCellRad, 0.035 * 0.04);
            Assert.AreEqual(0.04, home.GustCellRad, 0.04 * 0.04);
            Assert.AreEqual(12.0, home.RainDriftMs, 12.0 * 0.03);
            Assert.AreEqual(4.0, home.TradeDriftMs, 4.0 * 0.03);
            Assert.AreEqual(6.0, home.ItczWaveMs, 6.0 * 0.03);
        }

        [TestMethod]
        public void TheHeightsOfAColumnScaleWithTheTroposphere()
        {
            // a tower of the double height in a column of the double scale has the same density at the double altitude: the shape is one, its metres are the body's
            var one = new Weather { CellStrength = 0.8, CellTopM = 6000.0, HeightScale = 1.0 };
            var two = new Weather { CellStrength = 0.8, CellTopM = 12000.0, HeightScale = 2.0 };
            for (double alt = 0.0; alt <= 7000.0; alt += 250.0)
            {
                Assert.AreEqual(one.DensityAt(alt), two.DensityAt(2.0 * alt), 1e-12, "density at " + alt);
                Assert.AreEqual(LayerCoupling.CellDensity(0.8, 6000.0, alt, 1.0), LayerCoupling.CellDensity(0.8, 12000.0, 2.0 * alt, 2.0), 1e-12);
            }
            // a sample of a generator carries its body's scale and the base of the window's deck follows it
            foreach (bool second in new[] { false, true })
            {
                var g = new WeatherGenerator(Params(second), 3UL);
                Weather w;
                g.Sample(0.4, 1.0, 100.0, 5.0e6, out w);
                Assert.AreEqual(g.D.HeightScale, w.HeightScale, 1e-12);
                Assert.IsTrue(w.LayerBaseM >= 600.0 * g.D.HeightScale - 1e-6 && w.LayerBaseM <= 1800.0 * g.D.HeightScale + 1e-6, "deck base " + w.LayerBaseM + " at scale " + g.D.HeightScale);
                Assert.IsTrue(w.CellTopM >= 0.35 * g.Body.TropopauseM - 1e-6 && w.CellTopM <= g.Body.TropopauseM + 1e-6, "tower top follows the troposphere");
            }
        }

        // ---- the second body: determinism, its own scales for the cloud stage ----

        [TestMethod]
        public void TheSecondBodyIsDeterministicAndDiffersFromTheHomeBody()
        {
            var a = new WeatherGenerator(Params(true), 7UL);
            var b = new WeatherGenerator(Params(true), 7UL);
            var c = new WeatherGenerator(Params(true), 8UL);
            ulong ha = 0, hb = 0, hc = 0, hh = 0;
            var home = new WeatherGenerator(Params(false), 7UL);
            for (int i = 0; i < 400; i++)
            {
                double lat = -1.4 + 2.8 * ((i * 37) % 400) / 400.0, lon = -3.1 + 6.2 * ((i * 91) % 400) / 400.0, ut = 3.0e6 + i * 5000.0;
                Weather w;
                a.Sample(lat, lon, 500.0, ut, out w); ha = ha * 31 + WeatherGenerator.HashOf(ref w, (ulong)i);
                b.Sample(lat, lon, 500.0, ut, out w); hb = hb * 31 + WeatherGenerator.HashOf(ref w, (ulong)i);
                c.Sample(lat, lon, 500.0, ut, out w); hc = hc * 31 + WeatherGenerator.HashOf(ref w, (ulong)i);
                home.Sample(lat, lon, 500.0, ut, out w); hh = hh * 31 + WeatherGenerator.HashOf(ref w, (ulong)i);
            }
            Assert.AreEqual(ha, hb, "the same body and seed: the same weather");
            Assert.AreNotEqual(ha, hc, "another seed");
            Assert.AreNotEqual(ha, hh, "another body");
        }

        [TestMethod]
        public void TheFieldGivesTheStageTheScalesOfItsBody()
        {
            foreach (bool second in new[] { false, true })
            {
                GeneratorField field = second ? FieldFixture.SecondField() : FieldFixture.Field();
                var inp = new CloudMesoInputs();
                Assert.IsTrue(field.CloudScalesOf(FieldFixture.BodyIndex, inp));
                Assert.AreEqual(second ? 500.0 : 600.0, inp.RadiusKm, 1e-9);
                Assert.AreEqual(second ? 7.5 : 10.5, inp.DepthKm, 1e-9);
                Assert.IsTrue(inp.Edge1Rad > 0.2 && inp.Edge1Rad < Math.PI / 2.0, "the first circulation cell's edge " + inp.Edge1Rad);
            }
            Assert.IsFalse(new GeneratorField().CloudScalesOf(2, new CloudMesoInputs()), "a body without a generator state has no scales");
        }

        [TestMethod]
        public void TheCircumferenceOfBodyHoldsTheSameNumberOfPatchesPerDepthOnBothBodies()
        {
            // the stage's pattern size in km: the cfg's depths times the body's depth, kept between two texels and a sixth of the circumference
            var home = TypeMixFixture.HomeScales(new CloudMesoInputs { Width = 512, Height = 256 });
            var small = new CloudMesoInputs { Width = 512, Height = 256, RadiusKm = 500.0, DepthKm = 7.5 };
            Assert.AreEqual(5.25 * 10.5, CloudMesoStage.SizeKm(0f, 5.25f, home), 1e-4);
            Assert.AreEqual(5.25 * 7.5, CloudMesoStage.SizeKm(0f, 5.25f, small), 1e-4);
            Assert.AreEqual(40f, CloudMesoStage.SizeKm(40f, 5.25f, small), 1e-4, "an explicit size in km wins");
            Assert.AreEqual(2.0 * 2.0 * Math.PI * 500.0 / 512.0, CloudMesoStage.SizeKm(0f, 0.01f, small), 1e-3, "not finer than two texels");
            Assert.AreEqual(2.0 * Math.PI * 500.0 / 6.0, CloudMesoStage.SizeKm(0f, 500f, small), 1e-3, "not coarser than a sixth of the circumference");
        }

        // ---- the climate's latitude coordinate follows the circulation cells ----

        [TestMethod]
        public void TheLatitudeCoordinateFollowsTheCirculationCellsOfTheBody()
        {
            double d2r = Math.PI / 180.0;
            // three cells at 30 and 60 degrees: the plain latitude / 90 degrees (the home body's curves are unchanged)
            var three = new CloudMesoInputs { Edge1Rad = 30.0 * d2r, Edge2Rad = 60.0 * d2r };
            for (int row = 0; row < 256; row += 9)
            {
                double lat = Math.Abs(90.0 - (row + 0.5) * 180.0 / 256.0);
                Assert.AreEqual(lat / 90.0, CloudMesoStage.LatitudeCoordinate(three, row, 256), 1e-5, "row " + row);
            }
            // two cells (the second edge at the pole): the middle of the range is the first edge
            var two = new CloudMesoInputs { Edge1Rad = 40.0 * d2r, Edge2Rad = 90.0 * d2r };
            Assert.AreEqual(1.0 / 3.0, CloudMesoStage.LatitudeCoordinate(two, FromLat(40.0), 256), 0.01);
            Assert.AreEqual(1.0, CloudMesoStage.LatitudeCoordinate(two, 0, 256), 0.01);
            Assert.AreEqual(0.0, CloudMesoStage.LatitudeCoordinate(two, 127, 256), 0.01);
            // one cell, or no edges: the plain latitude
            var one = new CloudMesoInputs { Edge1Rad = 90.0 * d2r, Edge2Rad = 90.0 * d2r };
            var none = new CloudMesoInputs { Edge1Rad = double.NaN, Edge2Rad = double.NaN };
            Assert.AreEqual(50.0 / 90.0, CloudMesoStage.LatitudeCoordinate(one, FromLat(50.0), 256), 0.01);
            Assert.AreEqual(50.0 / 90.0, CloudMesoStage.LatitudeCoordinate(none, FromLat(50.0), 256), 0.01);
            // monotone in the latitude for any edges
            foreach (var inp in new[] { three, two, one, none })
            {
                float last = -1f;
                for (int row = 127; row >= 0; row--)
                {
                    float c = CloudMesoStage.LatitudeCoordinate(inp, row, 256);
                    Assert.IsTrue(c >= last - 1e-6f, "the coordinate grows towards the pole");
                    last = c;
                }
            }
        }

        private static int FromLat(double latDeg)
        {
            return (int)Math.Round(128.0 - latDeg / 90.0 * 128.0 - 0.5);
        }
    }
}
