using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.D31
{
    // D31: the mode table (windSource x foreign wind present or absent -> one applier), the foreign-owner detection, the composition of the
    // wind, the per-vessel step and its budget.
    [TestClass]
    public class WindModeTests
    {
        private static ForeignWinds With(params string[] assemblies)
        {
            return ForeignWindDetector.Detect(assemblies);
        }

        [TestMethod]
        public void TheDetectorFindsTheWindModsByAssemblyNameAndSeparatesAppliersFromInfrastructure()
        {
            ForeignWinds none = With("Assembly-CSharp", "ModuleManager");
            Assert.AreEqual(0, none.Found.Count);
            Assert.IsNull(none.Owner);

            ForeignWinds kwp = With("kerbalweatherproject");
            Assert.AreEqual("Kerbal Weather Project", kwp.Owner, "names compare without case");

            ForeignWinds far = With("FerramAerospaceResearch", "WindAPI");
            Assert.IsTrue(far.FarPresent && far.WindApiPresent);
            Assert.IsNull(far.Owner, "FAR and WindAPI carry wind but apply none of their own");

            ForeignWinds aat = With("AdvancedAtmosphereTools");
            Assert.AreEqual(1, aat.Found.Count);
            Assert.IsNull(aat.Owner, "Advanced Atmosphere Tools is named in the log and does not count as an applier");

            Assert.AreEqual("CPWE", With("CPWE", "Windy").Owner, "the first applier in the catalogue order is the owner");
            Assert.AreEqual(0, ForeignWindDetector.Detect(null).Found.Count);
        }

        [TestMethod]
        public void EveryCellOfTheModeTableHasOneApplierAndItsStatusString()
        {
            ForeignWinds absent = ForeignWinds.None;
            ForeignWinds present = With("CPWE");
            foreach (WindSourceMode mode in Enum.GetValues(typeof(WindSourceMode)))
            {
                foreach (WindInjection injection in Enum.GetValues(typeof(WindInjection)))
                {
                    foreach (bool mfi in new[] { true, false })
                    {
                        WindPlan a = WindPlanner.Decide(mode, absent, injection, mfi, true);
                        WindPlan p = WindPlanner.Decide(mode, present, injection, mfi, true);
                        Assert.AreEqual(WindStatus.Text(mode, null), a.Status, mode + " absent");
                        Assert.AreEqual(WindStatus.Text(mode, "CPWE"), p.Status, mode + " present");
                        if (mode == WindSourceMode.Off || mode == WindSourceMode.Foreign)
                        {
                            Assert.IsFalse(a.AppliesAnything || p.AppliesAnything, mode + " applies nothing");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void AutoAddsOnlyTheStormDeltaWhereAForeignModAppliesABaseWindAndEverythingWhereNoneDoes()
        {
            WindPlan alone = WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Auto, true, true);
            Assert.IsTrue(alone.ApplyBase && alone.ApplyStorm);
            Assert.AreEqual(WindRoute.Aero, alone.Route);
            Assert.AreEqual("Wind owner: ReDefinition, mode: H1", alone.OwnerLine());

            WindPlan shared = WindPlanner.Decide(WindSourceMode.Auto, With("KerbalWeatherProject"), WindInjection.Auto, true, true);
            Assert.IsFalse(shared.ApplyBase, "the foreign base wind is not applied a second time");
            Assert.IsTrue(shared.ApplyStorm);
            StringAssert.Contains(shared.OwnerLine(), "Kerbal Weather Project (base wind), ReDefinition (storm delta)");
        }

        [TestMethod]
        public void OursAppliesEverythingAndWarnsWhereAForeignWindCannotBeReplaced()
        {
            WindPlan p = WindPlanner.Decide(WindSourceMode.Ours, With("Windy"), WindInjection.Auto, true, true);
            Assert.IsTrue(p.ApplyBase && p.ApplyStorm);
            StringAssert.Contains(p.Warning, "Windy");
            WindPlan q = WindPlanner.Decide(WindSourceMode.Ours, ForeignWinds.None, WindInjection.Auto, true, true);
            Assert.AreEqual("", q.Warning);
        }

        [TestMethod]
        public void TheRouteFollowsTheInjectionSettingAndTheIntegrator()
        {
            Assert.AreEqual(WindRoute.Aero, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Auto, true, true).Route);
            Assert.AreEqual(WindRoute.Force, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Auto, false, true).Route);
            Assert.AreEqual(WindRoute.Force, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Force, true, true).Route);
            Assert.AreEqual(WindRoute.Force, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Aero, false, true).Route, "Aero without the integrator falls back to Force");
            Assert.AreEqual(WindRoute.Aero, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Aero, true, true).Route);
            Assert.AreEqual(WindRoute.Off, WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Auto, true, false).Route, "windForces off");
        }

        [TestMethod]
        public void WithFarTheWindGoesThroughWindApiOrNotAtAll()
        {
            WindPlan both = WindPlanner.Decide(WindSourceMode.Auto, With("FerramAerospaceResearch", "WindAPI"), WindInjection.Auto, true, true);
            Assert.AreEqual(WindRoute.Far, both.Route);
            Assert.AreEqual("Wind owner: ReDefinition, mode: FAR", both.OwnerLine());
            WindPlan farOnly = WindPlanner.Decide(WindSourceMode.Auto, With("FerramAerospaceResearch"), WindInjection.Auto, true, true);
            Assert.AreEqual(WindRoute.Off, farOnly.Route);
            Assert.IsFalse(string.IsNullOrEmpty(farOnly.Warning), "the log says why");
        }

        // The numeric A/B of the plan: the wind a vehicle gets in total is the foreign mod's base (when it applies one) plus what this mod
        // applies; the base wind must be in it once in every mode.
        [TestMethod]
        public void NoModeAppliesTheBaseWindTwice()
        {
            const double baseMs = 10.0, foreignBaseMs = 10.0;
            var input = new WindInputs
            {
                BaseEast = baseMs, BaseNorth = 0, StormEast = baseMs, StormNorth = 0, Level01 = 0.0, GustFactor = 0, Ut = 100, Seed = 1,
                Strength = 1, GustStrength = 0, MaxWindMs = 50, ViolentWindMs = 50, HeightShare = 1, DensityShare = 1, BodyFixedPos = new Vector3(1, 2, 3)
            };
            foreach (bool foreignApplies in new[] { false, true })
            {
                ForeignWinds foreign = foreignApplies ? With("CPWE") : ForeignWinds.None;
                foreach (WindSourceMode mode in new[] { WindSourceMode.Auto, WindSourceMode.Foreign, WindSourceMode.Off })
                {
                    WindPlan plan = WindPlanner.Decide(mode, foreign, WindInjection.Auto, true, true);
                    input.ApplyBase = plan.AppliesAnything && plan.ApplyBase;
                    input.ApplyStorm = plan.AppliesAnything && plan.ApplyStorm;
                    double ours = WindCompose.Target(input).X;
                    double total = ours + (foreignApplies ? foreignBaseMs : 0.0);
                    if (mode == WindSourceMode.Auto) Assert.AreEqual(baseMs, total, 1e-9, "Auto, foreign " + foreignApplies + ": the base wind once");
                    if (mode == WindSourceMode.Foreign || mode == WindSourceMode.Off) Assert.AreEqual(foreignApplies ? foreignBaseMs : 0.0, total, 1e-9, mode + " adds nothing of ours");
                }
            }
        }

        [TestMethod]
        public void InAStormTheForeignBaseStaysTheForeignsAndOnlyTheStormShareIsOurs()
        {
            var input = new WindInputs
            {
                BaseEast = 10, StormEast = 10, Level01 = 0.7, GustFactor = 0, Ut = 5, Seed = 1, Strength = 1, GustStrength = 0,
                MaxWindMs = 50, ViolentWindMs = 50, HeightShare = 1, DensityShare = 1, BodyFixedPos = new Vector3(1, 2, 3)
            };
            WindPlan shared = WindPlanner.Decide(WindSourceMode.Auto, With("CPWE"), WindInjection.Auto, true, true);
            input.ApplyBase = shared.ApplyBase;
            input.ApplyStorm = shared.ApplyStorm;
            Assert.AreEqual(10.0 * WindCompose.StormShare(0.7), WindCompose.Target(input).X, 1e-9);
            WindPlan alone = WindPlanner.Decide(WindSourceMode.Auto, ForeignWinds.None, WindInjection.Auto, true, true);
            input.ApplyBase = alone.ApplyBase;
            input.ApplyStorm = alone.ApplyStorm;
            Assert.AreEqual(10.0, WindCompose.Target(input).X, 1e-9, "alone: base and storm add up to the field's wind, once");
        }

        [TestMethod]
        public void TheStormShareSplitsTheFieldsWindIntoBaseAndStorm()
        {
            Assert.AreEqual(0.0, WindCompose.StormShare(0.0));
            Assert.AreEqual(0.0, WindCompose.StormShare(PrecipScale.FlurriesFrom));
            Assert.AreEqual(1.0, WindCompose.StormShare(PrecipScale.HeavyFrom));
            Assert.AreEqual(1.0, WindCompose.StormShare(1.0));
            double a = WindCompose.StormShare(0.2), b = WindCompose.StormShare(0.4);
            Assert.IsTrue(a > 0 && b > a && b < 1);
        }

        private static WindInputs Calm(double east, double level)
        {
            return new WindInputs
            {
                BaseEast = east, StormEast = east, Level01 = level, GustFactor = 1, Ut = 321.5, Seed = 3, Strength = 1, GustStrength = 1,
                ApplyBase = true, ApplyStorm = true, MaxWindMs = 12, ViolentWindMs = 25, HeightShare = 1, DensityShare = 1,
                BodyFixedPos = new Vector3(100, 200, 300)
            };
        }

        [TestMethod]
        public void TheCompositionIsDeterministicScalesWithTheSettingsAndStaysInTheCap()
        {
            WindInputs i = Calm(8.0, 0.0);
            Vec3 a = WindCompose.Target(i), b = WindCompose.Target(i);
            Assert.AreEqual(a.X, b.X, 0.0);
            Assert.IsTrue(Math.Abs(a.X - 8.0) <= 8.0 * 0.1 + 1e-9, "fair weather gusts are within +-10 %, was " + a.X);

            i.GustStrength = 0;
            Assert.AreEqual(8.0, WindCompose.Target(i).X, 1e-9, "no gusts: the mean");
            i.Strength = 0.5;
            Assert.AreEqual(4.0, WindCompose.Target(i).X, 1e-9);
            i.Strength = 1;
            i.HeightShare = 0.1;
            Assert.AreEqual(0.8, WindCompose.Target(i).X, 1e-9, "the ground profile");
            i.HeightShare = 1;
            i.DensityShare = 0;
            Assert.AreEqual(0.0, WindCompose.Target(i).X, 1e-12, "no air, no wind");
            i.DensityShare = 1;
            i.BaseEast = i.StormEast = 90.0;
            Assert.AreEqual(12.0, WindCompose.Target(i).X, 1e-9, "the cap on the mean at maxWind");
            i.Level01 = 1.0;
            Assert.AreEqual(25.0, WindCompose.Target(i).X, 1e-9, "the violent wind in the worst weather");
        }

        [TestMethod]
        public void TheGustsComeInBurstsOfPlusMinusThirtyToFortyPercentInAHeavyFall()
        {
            WindInputs i = Calm(10.0, 1.0);
            double min = double.MaxValue, max = double.MinValue;
            for (double ut = 0; ut < 600.0; ut += 0.5)
            {
                i.Ut = ut;
                double x = WindCompose.Target(i).X;
                min = Math.Min(min, x);
                max = Math.Max(max, x);
            }
            Assert.IsTrue(min < 7.5 && max > 12.5, "the swing reaches the third of the mean, " + min + " .. " + max);
            Assert.IsTrue(min >= 6.0 - 1e-3 && max <= 14.0 + 1e-3, "and stays within 40 %");
        }

        [TestMethod]
        public void ACalmSkyAndANonFiniteInputGiveZeroAndNeverNaN()
        {
            Assert.AreEqual(0.0, WindCompose.Target(Calm(0.0, 0.0)).Length, 1e-12);
            var s = new VesselWindState();
            Vec3 w = s.Step(new Vec3(double.NaN, 0, 0), new Vec3(0, 0, -5), 1.2, 0.0, 10.0, 8.0, 1.5);
            Assert.IsTrue(w.IsFinite);
            Assert.AreEqual(0.0, w.Length);
        }

        [TestMethod]
        public void TheVesselStepRampsAfterAnUnpackSlewsAndCutsAtSpin()
        {
            var s = new VesselWindState { DragPerMass = 0.2 };
            s.MarkUnpacked(100.0);
            Vec3 target = new Vec3(10, 0, 0), v = new Vec3(0, 0, 0);
            double maxApplied = 0;
            Vec3 prev = new Vec3(0, 0, 0);
            double maxRate = 0;
            for (int k = 0; k < 400; k++)
            {
                double ut = 100.0 + k * 0.02;
                Vec3 w = s.Step(target, v, 1.2, 0.1, ut, 8.0, 5.0);
                maxApplied = Math.Max(maxApplied, w.Length);
                maxRate = Math.Max(maxRate, (w - prev).Length / 0.02);
                prev = w;
                if (k == 50) Assert.IsTrue(w.Length < 10.0 * 0.4, "one second after the unpack the ramp holds the wind under 40 %, was " + w.Length);
            }
            Assert.AreEqual(10.0, maxApplied, 1e-9, "the whole wind after the ramp and the slew");
            Assert.IsTrue(maxRate <= 8.0 + 1e-6, "the slew limit holds at " + maxRate);

            Vec3 cut = s.Step(target, v, 1.2, 10.0, 109.0, 8.0, 5.0);
            Assert.IsTrue(cut.Length < 10.0, "a spin over the limit takes the wind away at the slew rate");
            for (int k = 0; k < 100; k++) cut = s.Step(target, v, 1.2, 10.0, 109.02 + k * 0.02, 8.0, 5.0);
            Assert.AreEqual(0.0, cut.Length, 1e-9);
            Assert.IsTrue(s.LastSpinCut);
        }

        [TestMethod]
        public void AForcedAbsurdWindIsClampedBelowTheAccelerationLimit()
        {
            // A sail: 650 m2 of drag per tonne (a canopy), hanging at its terminal speed of 5 m/s.
            var s = new VesselWindState { DragPerMass = 650.0 };
            s.MarkUnpacked(0.0);
            Vec3 w = new Vec3(0, 0, 0);
            Vec3 v = new Vec3(0, 0, -5);
            double minShare = 1.0, worstExtra = 0.0;
            for (int k = 0; k < 2000; k++)
            {
                Vec3 before = w;
                w = s.Step(new Vec3(900, 0, 0), v, 1.2, 0.0, k * 0.02, 8.0, 1.5);
                minShare = Math.Min(minShare, s.LastClampShare);
                worstExtra = Math.Max(worstExtra, WindMath.ExtraAcceleration(1.2, 650.0, v - before, w - before));
                Assert.IsTrue(w.IsFinite);
            }
            Assert.IsTrue(minShare < 1.0, "the clamp fired, smallest share " + minShare);
            Assert.IsTrue(worstExtra <= 1.5 + 1e-6, "no step added more than 1.5 m/s2, worst " + worstExtra);
        }

        [TestMethod]
        public void ACanopyThatDriftsWithTheWindFeelsTheWholeWind()
        {
            // The canopy at the wind's speed: the steady wind is not cut, the clamp judges steps.
            var s = new VesselWindState { DragPerMass = 650.0 };
            s.MarkUnpacked(0.0);
            Vec3 w = new Vec3(0, 0, 0);
            double ground = 0.0;
            for (int k = 0; k < 6000; k++)
            {
                double ut = k * 0.02;
                // The canopy follows the air: its ground speed approaches the wind applied with a time constant of a second.
                Vec3 v = new Vec3(ground, 0, -5);
                w = s.Step(new Vec3(15, 0, 0), v, 1.2, 0.0, ut, 8.0, 1.5);
                ground += (w.X - ground) * 0.02;
            }
            Assert.AreEqual(15.0, w.X, 0.05, "after the ramp and the wind-up the canopy has the whole wind");
            Assert.AreEqual(1.0, s.LastClampShare, 1e-9);
        }

        [TestMethod]
        public void AJumpOfTheClockStartsTheRampAgain()
        {
            var s = new VesselWindState();
            s.MarkUnpacked(0.0);
            Vec3 w = new Vec3(0, 0, 0);
            for (int k = 0; k < 300; k++) w = s.Step(new Vec3(10, 0, 0), new Vec3(), 1.2, 0, k * 0.02, 8.0, 5.0);
            Assert.IsTrue(w.Length > 9.0, "the wind has built up, was " + w.Length);
            Vec3 after = s.Step(new Vec3(10, 0, 0), new Vec3(), 1.2, 0, 5000.0, 8.0, 5.0);
            Assert.AreEqual(0.0, after.Length, 1e-9, "a clock that jumped by thousands of seconds does not carry the wind over");
        }

        [TestMethod]
        public void TheSamplePathAllocatesNothingAndCostsUnderTwentyMicroseconds()
        {
            WindInputs i = Calm(10.0, 0.5);
            var s = new VesselWindState { DragPerMass = 0.3 };
            s.MarkUnpacked(0.0);
            Vec3 v = new Vec3(10, 0, -5);
            // Warm-up, then a measured loop.
            for (int k = 0; k < 2000; k++)
            {
                i.Ut = k * 0.02;
                s.Step(WindCompose.Target(i), v, 1.2, 0.1, i.Ut, 8.0, 1.5);
            }
            AppDomain.MonitoringIsEnabled = true;
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            var watch = Stopwatch.StartNew();
            const int n = 20000;
            for (int k = 0; k < n; k++)
            {
                i.Ut = 100.0 + k * 0.02;
                s.Step(WindCompose.Target(i), v, 1.2, 0.1, i.Ut, 8.0, 1.5);
            }
            watch.Stop();
            long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            double microseconds = watch.Elapsed.TotalMilliseconds * 1000.0 / n;
            Console.WriteLine("D31 sample path: " + microseconds.ToString("0.000") + " us per sample, " + allocated + " bytes over " + n + " samples");
            Assert.IsTrue(allocated < 64 * 1024, "the loop allocated " + allocated + " bytes in " + n + " samples");
            Assert.IsTrue(microseconds < 20.0, "a sample took " + microseconds + " us");
        }
    }
}
