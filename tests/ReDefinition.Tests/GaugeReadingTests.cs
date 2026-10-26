using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Hud;
using ReDefinition.Hud.Readouts;

namespace ReDefinition.Tests
{
    // The round gauge's figures (GaugeReadings): the arc has to show what the
    // digits show, on a scale that stays as it is.
    [TestClass]
    public class GaugeReadingTests
    {
        // An engine with what its tanks hold, what they held as built, and the flow it burns, in tonnes and
        // tonnes a second.
        private static EngineFuel Engine(string name, double left, double full, double flow)
        {
            return new EngineFuel { Name = name, Left = left, Full = full, Flow = flow };
        }

        // A core of 20 t at 0.1 t/s with two pairs of boosters, 4 t and 6 t each at
        // 0.08 t/s, dropped pair by pair as they burn out: 50 s, then 25 s more,
        // then the core alone. The digits are the time until the stage's boosters
        // are empty, as KSP counts a stage; flown in half seconds. Throughout, the
        // arc stands at the digits' share of the timed engine's full burn, falls
        // with them, and is empty as the stage ends.
        [TestMethod]
        public void TheStageArcRunsOutWithTheDigitsWhileBoostersDrop()
        {
            const double step = 0.5, coreFlow = 0.1, boosterFlow = 0.08;
            double core = 20.0;
            double[] pairs = { 4.0, 6.0 };
            double[] full = { 4.0, 6.0 };
            int stagesSeen = 0;
            for (int first = 0; first <= pairs.Length; first++)
            {
                // The stage ends as pair `first` runs dry; after the last pair, as the core does.
                bool boosters = first < pairs.Length;
                double before = double.MaxValue;
                GaugeReading reading = null;
                int steps = 0;
                while ((boosters ? pairs[first] : core) > 1e-9)
                {
                    List<EngineFuel> engines = new List<EngineFuel> { Engine("core", core, 20.0, coreFlow) };
                    for (int pair = first; pair < pairs.Length; pair++)
                    {
                        engines.Add(Engine("booster" + pair + "a", pairs[pair], full[pair], boosterFlow));
                        engines.Add(Engine("booster" + pair + "b", pairs[pair], full[pair], boosterFlow));
                    }
                    double digits = boosters ? pairs[first] / boosterFlow : core / coreFlow;
                    reading = GaugeReadings.Stage(engines, digits);
                    Assert.IsNotNull(reading);

                    int timed = GaugeReadings.Timed(engines, digits);
                    StringAssert.StartsWith(engines[timed].Name, boosters ? "booster" + first : "core");
                    double wholeBurn = engines[timed].Full / engines[timed].Flow;
                    Assert.AreEqual(digits / wholeBurn, reading.Fill, 1e-9, "stage " + first + " at " + digits + " s");
                    Assert.IsTrue(reading.Fill < before, "the arc falls");
                    before = reading.Fill;

                    core -= coreFlow * step;
                    for (int pair = first; pair < pairs.Length; pair++) pairs[pair] -= boosterFlow * step;
                    steps++;
                }
                Assert.IsTrue(steps > 10);
                Assert.IsTrue(reading.Fill < 0.02, "empty as the stage ends: " + reading.Fill);
                Assert.AreEqual(2, reading.Level);
                stagesSeen++;
            }
            Assert.AreEqual(3, stagesSeen);
        }

        // What went wrong in flight: every engine's tanks summed. With the
        // boosters at their last second the core is still four fifths full, and
        // the arc stood there.
        [TestMethod]
        public void TheCoresTanksDoNotHoldTheArcUp()
        {
            List<EngineFuel> engines = new List<EngineFuel>
            {
                Engine("core", 16.0, 20.0, 0.1),
                Engine("boosterA", 0.08, 4.0, 0.08),
                Engine("boosterB", 0.08, 4.0, 0.08),
            };
            GaugeReading reading = GaugeReadings.Stage(engines, 1.0);
            Assert.AreEqual(0.02, reading.Fill, 1e-9);
            Assert.AreEqual("1.0s", reading.Text);
        }

        // No stage gauge without engines or without a burn time left.
        [TestMethod]
        public void NoStageWithoutABurnTimeOrEngines()
        {
            Assert.IsNull(GaugeReadings.Stage(new List<EngineFuel>(), 10.0));
            Assert.IsNull(GaugeReadings.Stage(new List<EngineFuel> { Engine("core", 1.0, 2.0, 0.1) }, 0.0));
        }

        // The impact's tick stands where the time to impact is at the last moment
        // to brake, and the arc reaches it as that moment comes.
        [TestMethod]
        public void TheImpactArcReachesItsTickAtTheMomentToBrake()
        {
            GaugeReading early = GaugeReadings.Impact(60.0, 45.0);
            Assert.AreEqual(GaugeReadings.TimeScale(15.0), early.Mark, 1e-9);
            Assert.IsTrue(early.Fill > early.Mark);
            Assert.AreEqual(0, early.Level);

            GaugeReading close = GaugeReadings.Impact(19.0, 4.0);
            Assert.AreEqual(1, close.Level);

            GaugeReading due = GaugeReadings.Impact(15.0, 0.0);
            Assert.AreEqual(due.Mark, due.Fill, 1e-9);
            Assert.AreEqual(2, due.Level);

            GaugeReading unpowered = GaugeReadings.Impact(30.0, double.NaN);
            Assert.IsTrue(double.IsNaN(unpowered.Mark));
            Assert.IsNull(unpowered.Warning);
        }

        // On the Mun from 5000 m at 50 m/s down (1.63 m/s^2, 5 of thrust): the
        // ground in (-50 + sqrt(2500 + 2 * 1.63 * 5000)) / 1.63 = 53.4 s; the burn
        // lights in 38.4 s, at 1880 m and 112.6 m/s, from where the fall would take
        // 15.1 s more. The tick stands at those 15.1 s: the time to impact the
        // gauge shows as the moment to brake comes. With the countdown at the speed
        // now (92.6 s) it stood at zero.
        [TestMethod]
        public void TheImpactTickMatchesTheFallAtTheMomentToBrake()
        {
            double impact = (-50.0 + Math.Sqrt(2500.0 + 2.0 * 1.63 * 5000.0)) / 1.63;
            double height, countdown, deltaV;
            ReadoutMath.SuicideBurn(5000.0, 50.0, 5.0, 1.63, out height, out countdown, out deltaV);
            double speed = 50.0 + 1.63 * countdown;
            double fallAfter = (-speed + Math.Sqrt(speed * speed + 2.0 * 1.63 * height)) / 1.63;
            Assert.AreEqual(15.06, fallAfter, 0.01);
            GaugeReading reading = GaugeReadings.Impact(impact, countdown);
            Assert.AreEqual(GaugeReadings.TimeScale(fallAfter), reading.Mark, 1e-3);
            Assert.AreEqual(0, reading.Level);
        }

        // The same time to impact stands at the same place, whatever the limit.
        [TestMethod]
        public void TheScalesDoNotMoveWithTheLimits()
        {
            Assert.AreEqual(GaugeReadings.Impact(30.0, 5.0).Fill, GaugeReadings.Impact(30.0, 25.0).Fill, 1e-12);
            Assert.AreEqual(GaugeReadings.Closing(2.0, 50.0).Fill, GaugeReadings.Closing(2.0, 5000.0).Fill, 1e-12);
            Assert.AreEqual(GaugeReadings.Attack(10.0, 30.0).Fill, GaugeReadings.Attack(-10.0, 20.0).Fill, 1e-12);
        }

        // Closing on a target, the tick stands at the safe speed, a hundredth of the distance per second but at
        // least 0.2 m/s: caution above it, warning beyond one and a half times it, an empty arc moving away.
        [TestMethod]
        public void ClosingAgainstTheSafeSpeed()
        {
            // 100 m off: 1 m/s is safe.
            Assert.AreEqual(0, GaugeReadings.Closing(0.9, 100.0).Level);
            Assert.AreEqual(1, GaugeReadings.Closing(1.2, 100.0).Level);
            Assert.AreEqual(2, GaugeReadings.Closing(1.6, 100.0).Level);
            Assert.AreEqual(GaugeReadings.SpeedScale(1.0), GaugeReadings.Closing(0.5, 100.0).Mark, 1e-12);
            // Moving away: an empty arc.
            Assert.AreEqual(0.0, GaugeReadings.Closing(-3.0, 100.0).Fill, 1e-12);
            // Close in, never below 0.2 m/s.
            Assert.AreEqual(0.2, GaugeReadings.SafeClosing(5.0), 1e-12);
        }

        // Before a burn the arc counts down the time to it, caution in the last ten seconds; during it, the
        // delta-v left as a share of what the burn started with, caution in the last tenth.
        [TestMethod]
        public void TheBurnCountsDownThenRunsOut()
        {
            Assert.AreEqual("T-30.0s", GaugeReadings.Countdown(30.0).Text);
            Assert.AreEqual(0, GaugeReadings.Countdown(30.0).Level);
            Assert.AreEqual(1, GaugeReadings.Countdown(5.0).Level);
            Assert.IsTrue(GaugeReadings.Countdown(30.0).Fill > GaugeReadings.Countdown(5.0).Fill);

            Assert.AreEqual(0.5, GaugeReadings.Burning(400.0, 800.0).Fill, 1e-12);
            Assert.AreEqual(0, GaugeReadings.Burning(400.0, 800.0).Level);
            Assert.AreEqual(1, GaugeReadings.Burning(40.0, 800.0).Level);
        }

        // The angle of attack fills the arc from 0 to 40 degrees either way: caution from four fifths of the
        // stall angle, warning beyond it, the warning band starting at the stall.
        [TestMethod]
        public void TheAngleOfAttackAgainstTheStall()
        {
            Assert.AreEqual(0.25, GaugeReadings.Attack(10.0, 30.0).Fill, 1e-12);
            Assert.AreEqual(0, GaugeReadings.Attack(10.0, 30.0).Level);
            Assert.AreEqual(1, GaugeReadings.Attack(-26.0, 30.0).Level);
            Assert.AreEqual(2, GaugeReadings.Attack(31.0, 30.0).Level);
            Assert.AreEqual(0.75, GaugeReadings.Attack(0.0, 30.0).Warning[0], 1e-12);
        }

        // Every band lies on the scale, from its lower to its upper end.
        [TestMethod]
        public void BandsLieOnTheScale()
        {
            GaugeReading[] readings =
            {
                GaugeReadings.Stage(new List<EngineFuel> { Engine("core", 1.0, 2.0, 0.1) }, 10.0),
                GaugeReadings.Countdown(100.0), GaugeReadings.Burning(10.0, 100.0), GaugeReadings.Impact(100.0, 20.0),
                GaugeReadings.Impact(700.0, 5.0), GaugeReadings.Closing(3.0, 40.0), GaugeReadings.Closing(90.0, 100000.0),
                GaugeReadings.Attack(12.0, 30.0),
            };
            foreach (GaugeReading reading in readings)
                foreach (double[] band in new[] { reading.Caution, reading.Warning })
                {
                    if (band == null) continue;
                    Assert.IsTrue(band[0] >= 0.0 && band[1] <= 1.0 && band[0] <= band[1], reading.Text);
                }
        }
    }
}
