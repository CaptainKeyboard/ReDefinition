using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Flight.Readouts;

namespace ReDefinition.Tests
{
    // The readouts' sums (ReadoutMath), against cases worked out by hand.
    [TestClass]
    public class ReadoutMathTests
    {
        // Kerbin's low orbit, 700 km from its centre, to the Mun's, 12,000 km: the
        // Mun must lead by 180 (1 - (12,700 / 24,000)^1.5), about 110.7 degrees.
        [TestMethod]
        public void TransferToTheMun()
        {
            double expected = 180.0 * (1.0 - Math.Pow(12700.0 / 24000.0, 1.5));
            Assert.AreEqual(expected, ReadoutMath.TransferAngle(700e3, 12000e3), 1e-9);
            Assert.AreEqual(110.7, ReadoutMath.TransferAngle(700e3, 12000e3), 0.1);
        }

        [TestMethod]
        public void LowerTargetTrails()
        {
            // To a lower orbit the target must trail: wrapped into 0 .. 360.
            double angle = ReadoutMath.TransferAngle(12000e3, 700e3);
            Assert.IsTrue(angle > 180.0 && angle < 360.0, angle.ToString());
        }

        [TestMethod]
        public void TimeToPhaseCloses()
        {
            // 30 degrees too much, closing at 0.1 degrees a second: 300 s.
            Assert.AreEqual(300.0, ReadoutMath.TimeToPhase(140.0, 110.0, 0.15, 0.05), 1e-9);
            // Just past: nearly a full turn more.
            Assert.AreEqual(3590.0, ReadoutMath.TimeToPhase(109.0, 110.0, 0.15, 0.05), 1e-6);
            Assert.IsTrue(double.IsNaN(ReadoutMath.TimeToPhase(10.0, 20.0, 0.1, 0.1)));
        }

        [TestMethod]
        public void SuicideBurnStopsInTime()
        {
            // 100 m/s down, 15 m/s^2 of thrust against 5 of gravity: 10 net.
            double distance, seconds, deltaV;
            ReadoutMath.SuicideBurn(100.0, 15.0, 5.0, out distance, out seconds, out deltaV);
            Assert.AreEqual(500.0, distance, 1e-9);
            Assert.AreEqual(10.0, seconds, 1e-9);
            Assert.AreEqual(150.0, deltaV, 1e-9);
            ReadoutMath.SuicideBurn(100.0, 4.0, 5.0, out distance, out seconds, out deltaV);
            Assert.IsTrue(double.IsNaN(distance));
        }

        [TestMethod]
        public void Writing()
        {
            Assert.AreEqual("412 m", ReadoutMath.Distance(412.3));
            Assert.AreEqual("85.4 km", ReadoutMath.Distance(85367));
            Assert.AreEqual("-598 km", ReadoutMath.Distance(-598435));
            Assert.AreEqual("1.25 Mm", ReadoutMath.Distance(1.25e6));
            Assert.AreEqual("12.3 m/s", ReadoutMath.Speed(12.34));
            Assert.AreEqual("0.0 m/s", ReadoutMath.Speed(-0.01));
            Assert.AreEqual("12.3°", ReadoutMath.Angle(12.34));
            Assert.AreEqual("T - 4m 35s", ReadoutMath.Countdown(275));
            Assert.AreEqual("T + 12s", ReadoutMath.Countdown(-12));
            Assert.AreEqual("2h 05m", ReadoutMath.Duration(7500));
            Assert.AreEqual("1d 2h", ReadoutMath.Duration(21600 + 7200));
        }
    }

    // The catalogue of flight readouts (ReadoutCatalog): every key once, every
    // readout titled, in one of the groups, with a value.
    [TestClass]
    public class ReadoutCatalogTests
    {
        [TestMethod]
        public void KeysOnceTitledGrouped()
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (Readout readout in ReadoutCatalog.All)
            {
                Assert.IsTrue(seen.Add(readout.Key), readout.Key + " twice");
                Assert.IsFalse(string.IsNullOrEmpty(readout.Title), readout.Key);
                Assert.IsTrue(Array.IndexOf(ReadoutCatalog.Groups, readout.Group) >= 0, readout.Key);
                Assert.IsNotNull(readout.Value, readout.Key);
            }
        }
    }
}
