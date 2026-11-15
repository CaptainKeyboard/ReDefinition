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
