using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Landing;

namespace ReDefinition.Tests
{
    // The speed limit of the descent against a landing MechJeb flew itself. The recording is the Two-Stage Lander from a 25 km orbit of the Mun,
    // taken twice a second by the flight trace with MechJeb's own limit beside it (Data/mechjeb-landing-trace.csv).
    [TestClass]
    public class MechJebLandingReplayTests
    {
        // The Mun's gravity at the surface, in m/s2.
        private const double Gravity = 0.166 * 9.81;

        private sealed class Row
        {
            internal double Height;
            internal double Asl;
            internal double Speed;
            internal double Throttle;
            internal double Thrust;
            internal double Limit;
            internal double BrakeEnd;
            internal double Acceleration;
        }

        private static List<Row> Load()
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "mechjeb-landing-trace.csv");
            var culture = CultureInfo.InvariantCulture;
            var rows = new List<Row>();
            foreach (string line in File.ReadLines(file).Skip(1))
            {
                string[] cell = line.Split(',');
                if (cell.Length < 17 || cell[13].Length == 0) continue;
                rows.Add(new Row
                {
                    Height = double.Parse(cell[3], culture),
                    Asl = double.Parse(cell[4], culture),
                    Speed = double.Parse(cell[5], culture),
                    Throttle = double.Parse(cell[8], culture),
                    Thrust = double.Parse(cell[9], culture),
                    Limit = double.Parse(cell[13], culture),
                    BrakeEnd = double.Parse(cell[14], culture),
                    Acceleration = double.Parse(cell[15], culture),
                });
            }
            return rows;
        }

        // Every recorded limit is the one the sum gives from the height over the brake's end and the vessel's acceleration.
        [TestMethod]
        public void TheLimitIsTheOneMechJebRecorded()
        {
            List<Row> rows = Load().Where(row => row.Limit > 0.0).ToList();
            Assert.IsTrue(rows.Count > 500, "The recording has too few lines with a limit: " + rows.Count);
            foreach (Row row in rows)
            {
                double ours = SafeDescentSpeedPolicy.Limit(row.Asl - row.BrakeEnd, row.Acceleration, Gravity);
                Assert.AreEqual(row.Limit, ours, 0.003 * row.Limit + 0.2, "At " + row.Asl.ToString("0") + " m the limit was " + row.Limit + ", ours " + ours.ToString("0.0"));
            }
        }

        // The brake is lit with the vessel faster than 0.9 of the limit (CoastToDeceleration). MechJeb then holds it until the last 50 m, and does
        // not light it for a moment and cut it again.
        [TestMethod]
        public void TheBrakeIsLitAboveTheLimitAndHeldUntilTheEnd()
        {
            List<Row> rows = Load();
            int start = rows.FindIndex(row => row.Thrust > 0.0 && row.Throttle > 0.05 && row.Asl < 20000.0);
            Assert.IsTrue(start > 0, "No brake in the recording.");
            Assert.IsTrue(rows[start].Speed > 0.9 * rows[start].Limit, "The brake was lit below the limit.");
            int cut = 0;
            for (int i = start + 1; i < rows.Count; i++)
                if (rows[i].Throttle < 0.05 && rows[i - 1].Throttle >= 0.05 && rows[i].Height > 50.0) cut++;
            Assert.AreEqual(0, cut, "The throttle was cut and lit again above the brake's end.");
        }
    }
}
