using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;
using UnityEngine;

namespace ReDefinition.Tests
{
    // How fast a vessel turns (Turning), against cases worked out by hand: two
    // one-tonne parts a metre either side of the centre along the roll axis make
    // 2 t m^2 about pitch and yaw; 1 kN m gives 0.5 rad/s^2, and 90 degrees from
    // rest to rest takes 2 * sqrt((pi / 2) / 0.5) = 2 sqrt(pi) s.
    [TestClass]
    public class TurningTests
    {
        private static PartFacts Part(float y, Vector3 wheel = default(Vector3))
        {
            return new PartFacts { Mass = 1.0, DryMass = 1.0, Position = new Vector3(0f, y, 0f), WheelTorque = wheel };
        }

        [TestMethod]
        public void ReactionWheelTurnsTwoPointMasses()
        {
            List<PartFacts> parts = new List<PartFacts> { Part(1f, new Vector3(1f, 0f, 1f)), Part(-1f) };
            Assert.AreEqual(2.0 * Math.Sqrt(Math.PI), Turning.Seconds(parts, new[] { 0, 1 }, -1, -1), 1e-6);
        }

        [TestMethod]
        public void SlowerAxisDecides()
        {
            List<PartFacts> parts = new List<PartFacts> { Part(1f, new Vector3(4f, 0f, 1f)), Part(-1f) };
            Assert.AreEqual(2.0 * Math.Sqrt(Math.PI), Turning.Seconds(parts, new[] { 0, 1 }, -1, -1), 1e-6);
        }

        [TestMethod]
        public void NothingToTurnWithIsInfinite()
        {
            List<PartFacts> parts = new List<PartFacts> { Part(1f), Part(-1f) };
            Assert.IsTrue(double.IsPositiveInfinity(Turning.Seconds(parts, new[] { 0, 1 }, -1, -1)));
        }

        // A pair of RCS nozzles at the top pushing along x makes a pitch or yaw
        // torque only one way each: the weaker way counts, so one nozzle alone
        // turns nothing, and a pushing pair both ways turns by its thrust times
        // its lever.
        [TestMethod]
        public void RcsCountsTheWeakerWay()
        {
            PartFacts top = Part(1f);
            top.Nozzles.Add(new Nozzle { At = new Vector3(0f, 1f, 0f), Force = new Vector3(0f, 0f, 1f), Pitch = true, Yaw = true });
            PartFacts bottom = Part(-1f);
            List<PartFacts> parts = new List<PartFacts> { top, bottom };
            Assert.IsTrue(double.IsPositiveInfinity(Turning.Seconds(parts, new[] { 0, 1 }, -1, -1)));

            top.Nozzles.Add(new Nozzle { At = new Vector3(0f, 1f, 0f), Force = new Vector3(0f, 0f, -1f), Pitch = true, Yaw = true });
            top.Nozzles.Add(new Nozzle { At = new Vector3(0f, 1f, 0f), Force = new Vector3(1f, 0f, 0f), Pitch = true, Yaw = true });
            top.Nozzles.Add(new Nozzle { At = new Vector3(0f, 1f, 0f), Force = new Vector3(-1f, 0f, 0f), Pitch = true, Yaw = true });
            Assert.AreEqual(2.0 * Math.Sqrt(Math.PI), Turning.Seconds(parts, new[] { 0, 1 }, -1, -1), 1e-6);
        }

        // A part's own box adds m (b^2 + c^2) / 12: a single one-tonne part, 2 m
        // long along the roll axis, has 1/3 t m^2 about pitch.
        [TestMethod]
        public void PartBoxAddsItsOwnInertia()
        {
            PartFacts part = Part(0f, new Vector3(1f, 0f, 1f));
            part.Size = new Vector3(0f, 2f, 0f);
            double expected = 2.0 * Math.Sqrt(Math.PI / 2.0 / 3.0);
            Assert.AreEqual(expected, Turning.Seconds(new List<PartFacts> { part }, new[] { 0 }, -1, -1), 1e-6);
        }

        // Parts decoupled before the stage are gone; a part's mass as the stage
        // starts comes from what its resources hold then.
        [TestMethod]
        public void StageDecidesPartsAndMasses()
        {
            PartFacts tank = Part(-1f);
            tank.Mass = 5.0;
            tank.StageFuel[3] = new[] { 4.0, 0.0 };
            tank.DecoupleStage = 2;
            PartFacts pod = Part(1f, new Vector3(1f, 0f, 1f));
            List<PartFacts> parts = new List<PartFacts> { pod, tank };
            Assert.AreEqual(5.0, Turning.MassAt(tank, 3), 1e-9);
            Assert.AreEqual(1.0, Turning.MassAt(tank, 2), 1e-9);
            // Stage 1: the tank is gone, the pod alone has no inertia to turn.
            Assert.AreEqual(0.0, Turning.Seconds(parts, new[] { 0, 1 }, 1, -1), 1e-9);
        }
    }
}
