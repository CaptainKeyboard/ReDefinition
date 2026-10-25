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
        // A one-tonne part, all of it dry mass, at `y` along the roll axis, with the wheel torque given.
        private static PartFacts Part(float y, Vector3 wheel = default(Vector3))
        {
            return new PartFacts { Mass = 1.0, DryMass = 1.0, Position = new Vector3(0f, y, 0f), WheelTorque = wheel };
        }

        // The case worked out above: a wheel with 1 kN m about pitch and yaw turns the two point masses
        // 90 degrees in 2 sqrt(pi) s.
        [TestMethod]
        public void ReactionWheelTurnsTwoPointMasses()
        {
            List<PartFacts> parts = new List<PartFacts> { Part(1f, new Vector3(1f, 0f, 1f)), Part(-1f) };
            Assert.AreEqual(2.0 * Math.Sqrt(Math.PI), Turning.Seconds(parts, new[] { 0, 1 }, -1, -1), 1e-6);
        }

        // A wheel stronger about pitch than about yaw: the slower axis gives the time.
        [TestMethod]
        public void SlowerAxisDecides()
        {
            List<PartFacts> parts = new List<PartFacts> { Part(1f, new Vector3(4f, 0f, 1f)), Part(-1f) };
            Assert.AreEqual(2.0 * Math.Sqrt(Math.PI), Turning.Seconds(parts, new[] { 0, 1 }, -1, -1), 1e-6);
        }

        // Without a wheel, RCS or gimbal the vessel cannot turn: the time is infinite.
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
            // Stage 2 drops the tank as it fires, as KSP's DeltaVStageInfo counts it.
            Assert.AreEqual(0.0, Turning.Seconds(parts, new[] { 0, 1 }, 2, -1), 1e-9);
            Assert.IsTrue(Turning.Seconds(parts, new[] { 0, 1 }, 3, -1) > 0.0);
        }

        // A part decoupled at stage s is on the vessel while the stages above s burn,
        // gone as s fires; never decoupled (-1), always on.
        [TestMethod]
        public void AttachedUntilItsStageFires()
        {
            PartFacts booster = new PartFacts { DecoupleStage = 2 };
            Assert.IsTrue(booster.AttachedIn(-1));
            Assert.IsTrue(booster.AttachedIn(3));
            Assert.IsFalse(booster.AttachedIn(2));
            Assert.IsFalse(booster.AttachedIn(1));
            PartFacts pod = new PartFacts { DecoupleStage = -1 };
            Assert.IsTrue(pod.AttachedIn(0));
        }

        // A gimballing engine steers while its stage burns, not once the stage that
        // drops it fires.
        [TestMethod]
        public void GimbalCountsUntilItsEngineIsDropped()
        {
            PartFacts engine = Part(-1f);
            engine.GimbalSide = 1.0;
            engine.ActivatesAt = 2;
            engine.DecoupleStage = 1;
            PartFacts pod = Part(1f);
            List<PartFacts> parts = new List<PartFacts> { pod, engine };
            Assert.IsFalse(double.IsInfinity(Turning.Seconds(parts, new[] { 0, 1 }, 2, 2)));
            Assert.IsTrue(double.IsInfinity(Turning.Seconds(parts, new[] { 0, 1 }, 2, -1)));
        }

        // A group's RCS counts the parts still on as the stage starts: a monopropellant
        // tank the stage drops as it fires is gone, with its propellant.
        [TestMethod]
        public void RcsCountsThePartsStillOn()
        {
            PartFacts pod = new PartFacts { DecoupleStage = -1, DryMass = 1.0, RcsThrust = 1.0, RcsThrustOverIsp = 1.0 / 240.0 };
            pod.RcsMix["MonoPropellant"] = new Dictionary<string, double> { { "MonoPropellant", 1.0 } };
            pod.RcsResources["MonoPropellant"] = 0.3;
            PartFacts tank = new PartFacts { DecoupleStage = 2, DryMass = 0.1 };
            tank.RcsResources["MonoPropellant"] = 0.4;
            List<PartFacts> parts = new List<PartFacts> { pod, tank };
            double thrust, overIsp, propellant, deltaV;
            SectionRcs.Of(parts, new[] { 0, 1 }, 3, 1.8, out thrust, out overIsp, out propellant, out deltaV);
            Assert.AreEqual(0.7, propellant, 1e-9);
            Assert.AreEqual(240.0 * StageTable.StandardGravity * Math.Log(1.8 / 1.1), deltaV, 1e-6);
            SectionRcs.Of(parts, new[] { 0, 1 }, 2, 1.3, out thrust, out overIsp, out propellant, out deltaV);
            Assert.AreEqual(0.3, propellant, 1e-9);
            Assert.AreEqual(240.0 * StageTable.StandardGravity * Math.Log(1.3 / 1.0), deltaV, 1e-6);
            Assert.AreEqual(1.0, thrust, 1e-12);
        }
    }
}
