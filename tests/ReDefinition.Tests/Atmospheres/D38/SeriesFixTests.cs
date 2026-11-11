using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.D38
{
    // The fixes after the first combined game series K-W3b (L0): the body provider that reads REDEFINITION_BODY nodes is installed by a
    // module (without it every body read the generic rules and the Kerbin AURORA patch never reached the climate), the envelope of two
    // shell-off frames, and the pure parts of the picture sheets.
    [TestClass]
    public class SeriesFixTests
    {
        [TestMethod]
        public void TheBodyConfigModuleInstallsTheNodeReaderAndRemovesItAtShutdown()
        {
            var module = new BodyConfigModule();
            try
            {
                module.Init();
                Assert.IsInstanceOfType(Bodies.Provider, typeof(BodyConfigProvider));
            }
            finally
            {
                module.Shutdown();
            }
            Assert.IsInstanceOfType(Bodies.Provider, typeof(DefaultBodyProvider));
        }

        [TestMethod]
        public void ThePatchedAuroraRuleReachesTheClimate()
        {
            // What ModuleManager hands over after @REDEFINITION_BODY[X]:FINAL { @AURORA { @enabled = true } }.
            string plain = "REDEFINITION_BODY { name = X ; AURORA { enabled = false } }";
            string patched = "REDEFINITION_BODY { name = X ; AURORA { enabled = true } }";
            BodyFacts facts = BodyTestSupport.Planet("X", BodyTestSupport.HomeYear);
            Assert.IsFalse(BodyTestSupport.Run(facts, plain).Climate.Aurora.Enabled);
            Assert.IsTrue(BodyTestSupport.Run(facts, patched).Climate.Aurora.Enabled);
        }

        [TestMethod]
        public void TheEnvelopeIsThePixelWiseLargerOfTwoOffFrames()
        {
            float[] e = AuroraMetrics.Envelope(new[] { 0.1f, 0.5f, 0.2f }, new[] { 0.3f, 0.4f, 0.2f });
            CollectionAssert.AreEqual(new[] { 0.3f, 0.5f, 0.2f }, e);
        }

        [TestMethod]
        public void AMaskAgainstTheEnvelopeDropsWhatTheSkyDoesByItself()
        {
            float[] off = { 0.10f, 0.10f, 0.10f, 0.10f };
            float[] noise = { 0.12f, 0.10f, 0.11f, 0.10f };
            float[] on = { 0.12f, 0.10f, 0.30f, 0.10f };
            float[] raw = AuroraMetrics.Mask(on, off);
            float[] guarded = AuroraMetrics.Mask(on, AuroraMetrics.Envelope(off, noise));
            Assert.AreEqual(0.02f, raw[0], 1e-6f, "the raw mask counts the sky's own flicker");
            Assert.AreEqual(0f, guarded[0], 1e-6f, "the guarded mask does not");
            Assert.AreEqual(0.19f, guarded[2], 1e-6f, "a real brightening stays");
        }

        [TestMethod]
        public void TheHeatMapIsRedWhereBrighterAndBlueWhereDarker()
        {
            float scale;
            Color[] map = SeriesPictures.HeatMap(new[] { 0.5f, 0.1f, 0.3f }, new[] { 0.1f, 0.5f, 0.3f }, 3, 1, 0.01f, out scale);
            Assert.AreEqual(0.4f, scale, 1e-6f);
            Assert.IsTrue(map[0].r > map[0].b, "brighter is red");
            Assert.IsTrue(map[1].b > map[1].r, "darker is blue");
            Assert.AreEqual(map[2].r, map[2].b, 0.2f, "unchanged stays grey");
        }

        [TestMethod]
        public void ASheetHasTheNaturalViewLeftAndTheAidViewRight()
        {
            var left = new[] { Color.red, Color.red, Color.red, Color.red };
            var right = new[] { Color.blue, Color.blue, Color.blue, Color.blue };
            Color[] sheet = SeriesPictures.SideBySide(left, right, 2, 2);
            Assert.AreEqual(8, sheet.Length);
            Assert.AreEqual(Color.red, sheet[0]);
            Assert.AreEqual(Color.red, sheet[1]);
            Assert.AreEqual(Color.blue, sheet[2]);
            Assert.AreEqual(Color.blue, sheet[3]);
            Assert.AreEqual(Color.red, sheet[4]);
        }

        [TestMethod]
        public void ALargeViewIsScaledDownByAWholeFactor()
        {
            Assert.AreEqual(1, SeriesPictures.Factor(640));
            Assert.AreEqual(2, SeriesPictures.Factor(1600));
            Assert.AreEqual(3, SeriesPictures.Factor(2400));
            int w, h;
            Color[] small = SeriesPictures.Downscale(new[] { Color.white, Color.black, Color.white, Color.black }, 2, 2, 2, out w, out h);
            Assert.AreEqual(1, w);
            Assert.AreEqual(1, h);
            Assert.AreEqual(0.5f, small[0].r, 1e-6f);
        }
    }
}
