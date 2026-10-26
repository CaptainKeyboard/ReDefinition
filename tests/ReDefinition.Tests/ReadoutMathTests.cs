using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Hud.Readouts;

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

        // From a high orbit down to a low one the transfer angle comes out above 180 degrees.
        [TestMethod]
        public void LowerTargetTrails()
        {
            // To a lower orbit the target must trail: wrapped into 0 .. 360.
            double angle = ReadoutMath.TransferAngle(12000e3, 700e3);
            Assert.IsTrue(angle > 180.0 && angle < 360.0, angle.ToString());
        }

        // The time until the phase angle reaches the transfer angle, from the two orbits' angular speeds; a full
        // turn more once just past, and none when the angle does not close.
        [TestMethod]
        public void TimeToPhaseCloses()
        {
            // 30 degrees too much, closing at 0.1 degrees a second: 300 s.
            Assert.AreEqual(300.0, ReadoutMath.TimeToPhase(140.0, 110.0, 0.15, 0.05), 1e-9);
            // Just past: nearly a full turn more.
            Assert.AreEqual(3590.0, ReadoutMath.TimeToPhase(109.0, 110.0, 0.15, 0.05), 1e-6);
            Assert.IsTrue(double.IsNaN(ReadoutMath.TimeToPhase(10.0, 20.0, 0.1, 0.1)));
        }

        // The burn needed now: a vessel at its burn height lights at once and stops in the height it has; with
        // thrust weaker than gravity there is no burn height.
        [TestMethod]
        public void SuicideBurnStopsInTime()
        {
            // At the burn height already, 100 m/s down, 15 m/s^2 of thrust against 5
            // of gravity: 10 net, 500 m to stop, lit now, 10 s of burn.
            double height, countdown, deltaV;
            ReadoutMath.SuicideBurn(500.0, 100.0, 15.0, 5.0, out height, out countdown, out deltaV);
            Assert.AreEqual(500.0, height, 1e-9);
            Assert.AreEqual(0.0, countdown, 1e-9);
            Assert.AreEqual(150.0, deltaV, 1e-9);
            ReadoutMath.SuicideBurn(500.0, 100.0, 4.0, 5.0, out height, out countdown, out deltaV);
            Assert.IsTrue(double.IsNaN(height));
        }

        // On the Mun, 5000 m up at 50 m/s down, 5 m/s^2 of thrust against 1.63 of
        // gravity. Coasting, the vessel gains speed; the burn lights where
        // 5000 - 50 t - 0.815 t^2 = (50 + 1.63 t)^2 / 6.74, after 38.4 s at
        // (2500 + 2 * 1.63 * 5000) / 10 = 1880 m and 112.6 m/s -- not after the
        // (5000 - 371) / 50 = 92.6 s the speed now would take.
        [TestMethod]
        public void SuicideBurnCountsTheCoastsGravity()
        {
            double height, countdown, deltaV;
            ReadoutMath.SuicideBurn(5000.0, 50.0, 5.0, 1.63, out height, out countdown, out deltaV);
            Assert.AreEqual(1880.0, height, 1e-9);
            Assert.AreEqual(38.38, countdown, 0.01);
            double speed = 50.0 + 1.63 * countdown;
            Assert.AreEqual(speed * speed / (2.0 * 3.37), height, 0.5, "stops just over the ground");
            Assert.AreEqual(5000.0 - 50.0 * countdown - 0.815 * countdown * countdown, height, 0.5, "where the coast ends");
            Assert.AreEqual(5.0 * speed / 3.37, deltaV, 0.1);

            // Without gravity the coast keeps its speed: (5000 - 250) / 50 = 95 s.
            ReadoutMath.SuicideBurn(5000.0, 50.0, 5.0, 0.0, out height, out countdown, out deltaV);
            Assert.AreEqual(250.0, height, 1e-9);
            Assert.AreEqual(95.0, countdown, 1e-9);

            // 300 m up, below the 348 m to light at, the moment has passed.
            ReadoutMath.SuicideBurn(300.0, 50.0, 5.0, 1.63, out height, out countdown, out deltaV);
            Assert.IsTrue(countdown < 0.0, countdown.ToString());
        }

        // How the readouts write distances, speeds, angles, countdowns and durations: units chosen by size, a
        // speed that rounds to nothing without a minus sign, T - before an event and T + after it.
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
        // Every readout of the catalogue has its own key, a title, a group of ReadoutCatalog.Groups and a value.
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

        // One shipped profile per situation, none of them deletable.
        [TestMethod]
        public void ShippedProfilesOnePerSituation()
        {
            List<ReDefinition.Hud.Panel.FlightProfile> profiles = ReDefinition.Hud.Panel.FlightProfiles.Defaults();
            HashSet<ReDefinition.Hud.Panel.PanelSituation> situations = new HashSet<ReDefinition.Hud.Panel.PanelSituation>();
            foreach (ReDefinition.Hud.Panel.FlightProfile profile in profiles)
            {
                Assert.IsTrue(profile.Builtin, profile.Name);
                Assert.IsTrue(situations.Add(profile.Situation), profile.Name);
            }
            Assert.AreEqual(6, situations.Count);
        }

        // Every readout a default profile names is in the catalogue.
        [TestMethod]
        public void DefaultProfilesNameKnownReadouts()
        {
            foreach (ReDefinition.Hud.Panel.FlightProfile profile in ReDefinition.Hud.Panel.FlightProfiles.Defaults())
                foreach (string key in profile.Keys)
                    Assert.IsNotNull(ReadoutCatalog.Find(key), profile.Name + ": " + key);
        }

        // The docking readouts measure from a port that can still dock: by its
        // state's name, every one of ModuleDockingNode's states.
        [TestMethod]
        public void FreePortsByState()
        {
            foreach (string free in new[] { "Ready", "Acquire", "Acquire (dockee)", "Disengage" })
                Assert.IsTrue(ReadoutMath.PortFree(free), free);
            foreach (string taken in new[] { "Docked (docker)", "Docked (dockee)", "Docked (same vessel)", "PreAttached", "Disabled", "", null })
                Assert.IsFalse(ReadoutMath.PortFree(taken), taken ?? "null");
        }

        // A port fits the target where they share a node type: a Clamp-O-Tron
        // (size1) a Clamp-O-Tron, not a Junior (size0).
        [TestMethod]
        public void PortsFitByNodeType()
        {
            HashSet<string> size1 = new HashSet<string> { "size1" };
            Assert.IsTrue(ReadoutMath.PortFits(size1, new HashSet<string> { "size1" }));
            Assert.IsTrue(ReadoutMath.PortFits(new HashSet<string> { "size0", "size1" }, size1));
            Assert.IsFalse(ReadoutMath.PortFits(size1, new HashSet<string> { "size0" }));
            Assert.IsFalse(ReadoutMath.PortFits(size1, null));
        }
    }
}
