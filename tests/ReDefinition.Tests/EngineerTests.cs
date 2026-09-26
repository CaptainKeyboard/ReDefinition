using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The engineer view's numbers: how they are written (EngineerFormat) and how
    // the stage table is worked out from KSP's stages (StageTable).
    [TestClass]
    public class EngineerTests
    {
        [TestMethod]
        public void SpeedsAreWholeMetresPerSecondWithThousands()
        {
            Assert.AreEqual("6,008 m/s", EngineerFormat.Speed(6008.4));
            Assert.AreEqual("0 m/s", EngineerFormat.Speed(double.NaN));
        }

        [TestMethod]
        public void MassesAreTonnesAndKilogramsBelowOne()
        {
            Assert.AreEqual("552.3 t", EngineerFormat.Mass(552.255));
            Assert.AreEqual("12.53 t", EngineerFormat.Mass(12.525));
            Assert.AreEqual("950 kg", EngineerFormat.Mass(0.95));
        }

        [TestMethod]
        public void ForcesAreKilonewtonsAndMeganewtonsFromAThousand()
        {
            Assert.AreEqual("14.78 kN", EngineerFormat.Force(14.783));
            Assert.AreEqual("791.6 kN", EngineerFormat.Force(791.59));
            Assert.AreEqual("4.00 MN", EngineerFormat.Force(4000));
        }

        [TestMethod]
        public void TimesAreSecondsThenMinutesThenHours()
        {
            Assert.AreEqual("23.7s", EngineerFormat.Time(23.7));
            Assert.AreEqual("1m 53s", EngineerFormat.Time(113));
            Assert.AreEqual("1h 02m 03s", EngineerFormat.Time(3723));
            Assert.AreEqual("0.0s", EngineerFormat.Time(double.PositiveInfinity));
        }

        private static StageRow Stage(int number, double deltaV, double twr = 0, double start = 0, double end = 0)
        {
            return new StageRow
            {
                Number = number, DeltaV = deltaV, Twr = twr, StartMass = start, EndMass = end,
                HasEngines = twr > 0, Thrust = twr > 0 ? 1 : 0,
            };
        }

        [TestMethod]
        public void DeltaVLeftCountsTheStageAndEveryStageAfterIt()
        {
            List<StageRow> rows = StageTable.Complete(new[] { Stage(2, 899), Stage(0, 0), Stage(1, 1599) });

            Assert.AreEqual(0, rows[0].Number, "stage 0 first, as KSP lists them");
            Assert.AreEqual(0.0, rows[0].DeltaVRemaining);
            Assert.AreEqual(1599.0, rows[1].DeltaVRemaining);
            Assert.AreEqual(2498.0, rows[2].DeltaVRemaining);
            Assert.AreEqual(2498.0, StageTable.TotalDeltaV(rows));
        }

        [TestMethod]
        public void TheTwrAtTheEndIsTheSameThrustOverTheLighterVessel()
        {
            List<StageRow> rows = StageTable.Complete(new[] { Stage(1, 1000, 1.5, 20, 10) });

            Assert.AreEqual(3.0, rows[0].TwrMax, 1e-9);
        }

        [TestMethod]
        public void TheLaunchTwrIsTheFirstStageThatBurns()
        {
            List<StageRow> rows = StageTable.Complete(new[] { Stage(3, 0), Stage(2, 900, 1.2, 10, 5), Stage(0, 500, 0.8, 2, 1) });

            Assert.AreEqual(2, StageTable.FirstBurning(rows).Number, "stage 3 is a decoupler that burns nothing");
        }

        [TestMethod]
        public void OnlyStagesWithDeltaVAreShownUnlessAllAreWanted()
        {
            List<StageRow> rows = StageTable.Complete(new[] { Stage(1, 0), Stage(0, 300, 1, 2, 1) });

            Assert.AreEqual(1, StageTable.Shown(rows, false).Count);
            Assert.AreEqual(2, StageTable.Shown(rows, true).Count);
        }

        [TestMethod]
        public void RocketDeltaVIsTsiolkovskyWithKspsStandardGravity()
        {
            // 300 s, from 10 t to 5 t: 300 * 9.80665 * ln 2.
            Assert.AreEqual(2039.2, StageTable.RocketDeltaV(300, 10, 5), 0.1);
            Assert.AreEqual(0.0, StageTable.RocketDeltaV(300, 5, 10), "no propellant burnt");
        }
    }
}
