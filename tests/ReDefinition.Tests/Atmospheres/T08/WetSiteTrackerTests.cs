using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T08
{
    // The site tracker of the wet ground, the loop the renderer runs every frame (package T08).
    //
    // Reference expectations (written before the tests):
    //   * a warp jump of 6 hours, taken in catch-up steps of at most 48 per call, reaches the state the same hours in 1 s frames reach
    //     within 5 %, in weather that rains in gusts (two showers of different strength) and leaves real water standing at the end;
    //   * a jump of 24 hours takes many calls, the tracker says it is catching up until the cursor has reached the clock, and one
    //     call never takes more than the budget;
    //   * a clock that goes back, a gap longer than the lookback and a first call each ask for a new start;
    //   * with nothing to catch up the tracker takes one step per sampling interval, not one per call.
    [TestClass]
    public class WetSiteTrackerTests
    {
        // Two showers: 1.0 h to 1.9 h at a peak of 9 mm/h and 4.2 h to 5.4 h at a peak of 14 mm/h, each a sin-squared bump, on a day of
        // 15 C, 60 % humidity (95 % in the rain), 3 m/s of wind and sun.
        private static WetInput Gusty(double ut)
        {
            double h = ut / 3600.0;
            double rain = 0.0;
            if (h > 1.0 && h < 1.9) rain += 9.0 * Math.Pow(Math.Sin(Math.PI * (h - 1.0) / 0.9), 2.0);
            if (h > 4.2 && h < 5.4) rain += 14.0 * Math.Pow(Math.Sin(Math.PI * (h - 4.2) / 1.2), 2.0);
            return new WetInput { WaterMmH = (float)rain, TemperatureK = 288.15f, FreezeK = 273.15f, WindMs = 3f, Humidity01 = rain > 0.0 ? 0.95f : 0.6f, Sun01 = 1f };
        }

        [TestMethod]
        public void ASixHourJumpInCatchUpStepsEqualsTheSameHoursInFrames()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            var framed = new WetState();
            for (int sec = 0; sec < 6 * 3600; sec++)
            {
                WetInput w = Gusty(sec + 0.5);
                WetnessModel.Step(ref framed, ref w, WetClass.Terrain, ref t, 1f);
            }
            var tracker = new WetSiteTracker();
            tracker.Start(6 * 3600.0, 6 * 3600.0);
            int calls = 0, total = 0;
            while (tracker.CatchingUp && calls < 100)
            {
                int n = tracker.Advance(Gusty, WetClass.Terrain, ref t, 6 * 3600.0, 48, 30.0);
                Assert.IsTrue(n <= 48);
                total += n;
                calls++;
            }
            Assert.IsFalse(tracker.CatchingUp);
            Assert.AreEqual(12, total, "twelve steps of half an hour");
            Assert.IsTrue(framed.StandingMm > 1.5f && framed.StoredMm > 1.0f, "the scenario leaves water behind: " + framed.StandingMm + " / " + framed.StoredMm);
            Assert.AreEqual(framed.StoredMm, tracker.State.StoredMm, 0.05f * framed.StoredMm, "layer");
            Assert.AreEqual(framed.StandingMm, tracker.State.StandingMm, 0.05f * framed.StandingMm, "standing");
        }

        [TestMethod]
        public void AJumpOfADayTakesManyCallsAndReportsCatchingUpUntilItIsThere()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            double lookback = t.LookbackH * 3600.0;
            double ut = 40 * 3600.0;
            var tracker = new WetSiteTracker();
            Assert.IsTrue(tracker.NeedsStart(ut, lookback));
            tracker.Start(ut, lookback);
            int calls = 0;
            bool everOverBudget = false;
            while (tracker.CatchingUp && calls < 100)
            {
                int n = tracker.Advance(Gusty, WetClass.Terrain, ref t, ut, 48, 30.0);
                if (n > 48) everOverBudget = true;
                calls++;
            }
            Assert.IsFalse(everOverBudget);
            Assert.IsTrue(calls >= 4 && calls < 100, "192 half-hour steps in calls of 48: " + calls);
            Assert.AreEqual(ut, tracker.CursorUt, 1.0);
            Assert.IsFalse(tracker.NeedsStart(ut, lookback));
        }

        [TestMethod]
        public void ABackwardClockAndALongGapAskForANewStart()
        {
            var tracker = new WetSiteTracker();
            tracker.CursorUt = 100000.0;
            Assert.IsTrue(tracker.NeedsStart(99000.0, 345600.0), "back");
            Assert.IsFalse(tracker.NeedsStart(100500.0, 345600.0), "a short step on");
            Assert.IsTrue(tracker.NeedsStart(100000.0 + 346000.0, 345600.0), "a gap over the lookback");
            tracker.Forget();
            Assert.IsTrue(tracker.NeedsStart(0.0, 345600.0), "after Forget");
        }

        [TestMethod]
        public void WithNothingToCatchUpItStepsOncePerSamplingInterval()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            var tracker = new WetSiteTracker();
            tracker.CursorUt = 1000.0;
            int steps = 0;
            for (double ut = 1000.0; ut < 1000.0 + 600.0; ut += 1.0 / 60.0)
                steps += tracker.Advance(Gusty, WetClass.Terrain, ref t, ut, 48, 30.0);
            Assert.AreEqual(20, steps, 1, "600 s at one step per 30 s");
            Assert.IsFalse(tracker.CatchingUp);
        }
    }
}
