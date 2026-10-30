using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Landing;

namespace ReDefinition.Tests
{
    // The bound of the convective flux against what the game reported on a part in passes through Kerbin's air. The recording is
    // Data/heat-flux-points.csv, from two Mun returns: the density, speed and Mach number once a second, and the flux and exposed area of the hottest part.
    [TestClass]
    public class HeatBoundReplayTests
    {
        // Physics.cfg of the game as it came, the constants the bound is worked from.
        private static readonly HeatConstants Stock = new HeatConstants
        {
            MachFactor = 7.0,
            MachDensityExponent = 0.5,
            MachVelocityExponent = 3.0,
            TurbulentStart = 100.0,
            TurbulentEnd = 200.0,
            TurbulentMultiplier = 50.0,
            LerpStartMach = 2.0,
            LerpEndMach = 5.0,
            LerpExponent = 3.0,
            LowMachTemperatureFactor = 1.0,
            HighMachTemperatureScalar = 21.0,
            HighMachTemperatureExponent = 0.75,
        };

        // The bound is over the flux the game reported at every point, and not by orders of magnitude.
        [TestMethod]
        public void TheBoundIsOverWhatTheGameReported()
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "heat-flux-points.csv");
            double[][] rows = File.ReadLines(file).Skip(1).Where(line => line.Length > 0)
                .Select(line => line.Split(',').Select(cell => double.Parse(cell, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
            Assert.IsTrue(rows.Length > 250, "The recording has too few points: " + rows.Length);
            double highest = 0.0, lowest = double.MaxValue;
            foreach (double[] row in rows)
            {
                double bound = HeatBound.FluxKW(row[0], row[1], row[2], 1.0, Stock);
                double reported = row[3] / row[4];
                Assert.IsTrue(reported <= bound, "The game's flux " + reported.ToString("0") + " kW/m2 was over the bound " + bound.ToString("0") + " at " + row[1].ToString("0") + " m/s.");
                highest = Math.Max(highest, reported / bound);
                lowest = Math.Min(lowest, reported / bound);
            }
            Assert.IsTrue(highest < 0.5 && lowest > 0.05, "The bound is not near the game's flux: " + lowest.ToString("0.00") + " to " + highest.ToString("0.00") + " of it.");
        }

        // Below the Mach range of the law there is no bound, and above it the shock temperature is the high-speed one.
        [TestMethod]
        public void TheShockTemperatureFollowsTheMachRange()
        {
            Assert.AreEqual(0.0, HeatBound.FluxKW(0.001, 300.0, 1.0, 1.0, Stock));
            Assert.AreEqual(21.0 * Math.Pow(3000.0, 0.75), HeatBound.ShockTemperature(3000.0, 9.0, Stock), 1e-6);
            Assert.AreEqual(600.0, HeatBound.ShockTemperature(600.0, 1.0, Stock), 1e-6);
        }
    }
}
