using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The viewer's controls as the window uses them, without the window: what the altitude slider shows and when a typed or slid height is placed, the speed Freeze Clouds
    // returns to, what A/B and the presets hold, and when the key list is built. The pure parts are the ones CloudViewer and CloudViewerWindow call.
    [TestClass]
    public class ViewerControlsTests
    {
        // --- The altitude slider follows the pose ---

        [TestMethod]
        public void TheAltitudeSliderShowsTheHeightOfTheLastPlacementWhateverItCameFrom()
        {
            var plan = new AltitudePlan();
            Assert.AreEqual(AltitudeScale.ToSlider(AltitudePlan.DefaultAglM), plan.SliderValue, 1e-6, "before any placement: the default height");
            plan.Want(4550.0);
            Assert.AreEqual(AltitudeScale.ToSlider(4550.0), plan.SliderValue, 1e-6, "a pose button's height");
            plan.Want(120.0);
            Assert.AreEqual(AltitudeScale.ToSlider(120.0), plan.SliderValue, 1e-6, "the next pose");
            plan.Clear();
            Assert.AreEqual(AltitudeScale.ToSlider(AltitudePlan.DefaultAglM), plan.SliderValue, 1e-6, "a new session starts at the default again");
        }

        // The pose button and the fake rig's model use the same plan as the viewer: the slider moves to the pose's height.
        [TestMethod]
        public void APoseButtonMovesTheSliderOfTheModel()
        {
            var rig = new FakeViewerRig();
            rig.ToggleWindow();
            ViewerPose inside = PosePresets.For(FakeViewerRig.KerbinLayers).First(p => p.Name == "inside");
            Assert.AreNotEqual(AltitudeScale.ToSlider(inside.AglM), rig.AltitudeSlider, 1e-3, "the default is not the pose's height");
            rig.PlacePose(inside);
            Assert.AreEqual(AltitudeScale.ToSlider(inside.AglM), rig.AltitudeSlider, 1e-6);
        }

        // --- A typed height is one placement ---

        private const double MaxAgl = 300000.0;

        [TestMethod]
        public void TypingFiveHundredMetresPlacesOnceAtFiveHundredNotAtFiftyOnTheWay()
        {
            var typed = new TypedAltitude();
            double agl;
            var placed = new List<double>();
            double now = 0.0;
            foreach (string text in new[] { "5", "50", "500" })
            {
                typed.Typed(text, now);
                now += 0.2;
                if (typed.TryTake(now, false, MaxAgl, out agl)) placed.Add(agl);
            }
            Assert.AreEqual(0, placed.Count, "nothing is placed while the keys come");
            Assert.IsTrue(typed.TryTake(now, true, MaxAgl, out agl), "Enter or the focus leaving ends the edit");
            Assert.AreEqual(500.0, agl);
            Assert.IsFalse(typed.TryTake(now, true, MaxAgl, out agl), "one placement per edit");
            Assert.IsFalse(typed.Pending);
        }

        [TestMethod]
        public void ATypedHeightThatStandsForAMomentIsPlacedOnceWithoutAnEnter()
        {
            var typed = new TypedAltitude();
            double agl;
            typed.Typed("1200", 10.0);
            Assert.IsFalse(typed.TryTake(10.0 + TypedAltitude.IdleS - 0.1, false, MaxAgl, out agl));
            Assert.IsTrue(typed.TryTake(10.0 + TypedAltitude.IdleS, false, MaxAgl, out agl));
            Assert.AreEqual(1200.0, agl);
            Assert.IsFalse(typed.TryTake(20.0, false, MaxAgl, out agl));
        }

        [TestMethod]
        public void ATypedTextThatIsNoHeightInRangeIsDroppedWithoutAPlacement()
        {
            var typed = new TypedAltitude();
            double agl;
            foreach (string text in new[] { "", "abc", "5", "9.9", "300001", "NaN", "-40" })
            {
                typed.Typed(text, 0.0);
                Assert.IsFalse(typed.TryTake(5.0, true, MaxAgl, out agl), "'" + text + "'");
                Assert.IsFalse(typed.Pending, "'" + text + "' is dropped");
            }
            typed.Typed("10", 0.0);
            Assert.IsTrue(typed.TryTake(5.0, true, MaxAgl, out agl), "the lowest height");
            typed.Typed("300000", 0.0);
            Assert.IsTrue(typed.TryTake(5.0, true, MaxAgl, out agl), "the highest height");
        }

        [TestMethod]
        public void TheEditsOfTwoHeightsAreTwoPlacements()
        {
            var typed = new TypedAltitude();
            double agl;
            typed.Typed("800", 0.0);
            Assert.IsTrue(typed.TryTake(0.1, true, MaxAgl, out agl));
            Assert.AreEqual(800.0, agl);
            typed.Typed("4000", 3.0);
            Assert.IsTrue(typed.TryTake(3.1, true, MaxAgl, out agl));
            Assert.AreEqual(4000.0, agl);
        }

        // --- Freeze Clouds returns to the last speed above 0 ---

        [TestMethod]
        public void TheSpeedMemoryKeepsTheLastSpeedAbove0AndStartsAtOne()
        {
            var memory = new CloudSpeedMemory();
            Assert.AreEqual(1f, memory.Last);
            memory.Seen(4f);
            memory.Seen(0f);
            memory.Seen(float.NaN);
            Assert.AreEqual(4f, memory.Last, "a speed of 0 is not remembered");
            memory.Reset(0f);
            Assert.AreEqual(1f, memory.Last, "a session that starts frozen returns to the default");
            memory.Reset(2.5f);
            Assert.AreEqual(2.5f, memory.Last);
        }

        [TestMethod]
        public void FreezingCloudsThatAlreadyStandStillAndReleasingThemLetsThemFlowAgain()
        {
            var rig = new FakeViewerRig();
            rig.ToggleWindow();
            rig.SetCloudSpeed(0f);
            rig.FreezeClouds(true);
            rig.FreezeClouds(false);
            Assert.AreEqual(1f, rig.CloudSpeed, "no speed above 0 was seen: back to the default");

            rig.SetCloudSpeed(4f);
            rig.SetCloudSpeed(0f);
            rig.FreezeClouds(true);
            rig.FreezeClouds(false);
            Assert.AreEqual(4f, rig.CloudSpeed, "the last speed above 0");

            rig.SetCloudSpeed(3f);
            rig.FreezeClouds(true);
            Assert.AreEqual(0f, rig.CloudSpeed);
            rig.FreezeClouds(false);
            Assert.AreEqual(3f, rig.CloudSpeed, "the speed it was frozen at");
        }
    }
}
