using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The pure parts of the layer core (package T01): the shell geometry (below, inside, above, the gas giant), the type table with its
    // interpolated heights and profiles, the coverage threshold, the stack and the CPU optical depth, and the map layout. The shader
    // mirrors these functions; the weather player measures the picture (tools/weather_metrics/layers.py).
    [TestClass]
    public class LayerGeometryTests
    {
        private const double R = 600000.0;

        [TestMethod]
        public void ARayFromTheGroundSeesTheShellAsOneSpanFromBelow()
        {
            LayerSpan a, b;
            // Straight up from 2 m: the shell 1700 to 2500 m is one span of 800 m starting at 1698 m.
            int n = LayerGeometry.ShellSpans(0, 0, R + 2.0, 0, 0, 1, R, 1700.0, 2500.0, double.MaxValue, out a, out b);
            Assert.AreEqual(1, n);
            Assert.AreEqual(1698.0, a.T0, 1e-6);
            Assert.AreEqual(2498.0, a.T1, 1e-6);
        }

        [TestMethod]
        public void ACameraInsideTheShellStartsItsSpanAtZero()
        {
            LayerSpan a, b;
            int n = LayerGeometry.ShellSpans(0, 0, R + 2000.0, 0, 0, 1, R, 1700.0, 2500.0, double.MaxValue, out a, out b);
            Assert.AreEqual(1, n);
            Assert.AreEqual(0.0, a.T0, 1e-9);
            Assert.AreEqual(500.0, a.T1, 1e-6);
        }

        [TestMethod]
        public void ACameraAboveTheShellLookingDownSeesTheShellThenTheGroundEndsTheRay()
        {
            LayerSpan a, b;
            double ground = 8000.0;
            int n = LayerGeometry.ShellSpans(0, 0, R + 8000.0, 0, 0, -1, R, 1700.0, 2500.0, ground, out a, out b);
            Assert.AreEqual(1, n);
            Assert.AreEqual(5500.0, a.T0, 1e-6);
            Assert.AreEqual(6300.0, a.T1, 1e-6);
        }

        [TestMethod]
        public void ARayThatGoesThroughTheHollowShellHasTwoSpansOnABodyWithoutASurfaceAndOneWithIt()
        {
            LayerSpan a, b;
            // From 3000 km away along y, an impact parameter inside the inner sphere: the near wall and the far wall.
            double far = 3000000.0, impact = R + 500.0;
            int gas = LayerGeometry.ShellSpans(impact, -far, 0, 0, 1, 0, R, 1500.0, 2500.0, double.MaxValue, out a, out b);
            Assert.AreEqual(2, gas);
            double chordOuter = 2.0 * Math.Sqrt((R + 2500.0) * (R + 2500.0) - impact * impact);
            double chordInner = 2.0 * Math.Sqrt((R + 1500.0) * (R + 1500.0) - impact * impact);
            Assert.AreEqual(chordOuter - chordInner, a.Length + b.Length, 1e-3);
            // With a surface the ray ends where it meets the ground sphere (impact parameter below R): the far wall drops out.
            double low = R - 100.0;
            double ground = far - Math.Sqrt(R * R - low * low);
            int solid = LayerGeometry.ShellSpans(low, -far, 0, 0, 1, 0, R, 1500.0, 2500.0, ground, out a, out b);
            Assert.AreEqual(1, solid);
            Assert.IsTrue(a.T1 < ground);
        }

        [TestMethod]
        public void TheHorizonDistanceOfADeckIsTheTangentLengthAndTheDipAngleIsTheCosineRule()
        {
            double h = 2000.0;
            Assert.AreEqual(Math.Sqrt(2.0 * R * h + h * h), LayerGeometry.HorizonDistance(R, h), 1e-9);
            // A ray at the dip angle of a camera at altitude h just grazes the sea-level sphere, at the tangent distance (R + h) sin(dip).
            double dip = LayerGeometry.DipAngle(R, h);
            Assert.AreEqual(Math.Sqrt(2.0 * R * h + h * h), (R + h) * Math.Sin(dip), 1e-6);
            // A ray 0.01 degrees steeper than the dip meets the sphere, a ray 0.01 degrees shallower misses it.
            double steeper = dip + 0.01 * Math.PI / 180.0, shallower = dip - 0.01 * Math.PI / 180.0;
            double t0, t1;
            Assert.IsTrue(LayerGeometry.HitSphere(0, 0, R + h, 0, Math.Cos(steeper), -Math.Sin(steeper), R, out t0, out t1));
            Assert.IsFalse(LayerGeometry.HitSphere(0, 0, R + h, 0, Math.Cos(shallower), -Math.Sin(shallower), R, out t0, out t1));
        }

        [TestMethod]
        public void TheSphereHitIsExactForARayStartingOnTheSphere()
        {
            double t0, t1;
            Assert.IsTrue(LayerGeometry.HitSphere(0, 0, R + 2000.0, 0, 0, 1, R + 2000.0, out t0, out t1));
            Assert.AreEqual(0.0, t1, 1e-6);
            Assert.AreEqual(-2.0 * (R + 2000.0), t0, 1e-3);
            Assert.IsFalse(LayerGeometry.HitSphere(0, 0, R + 100.0, 1, 0, 0, R - 1000.0, out t0, out t1));
        }

        [TestMethod]
        public void TheLatitudeAndLongitudeFollowTheMapFrame()
        {
            double lat, lon;
            LayerGeometry.LatLon(1, 0, 0, out lat, out lon);
            Assert.AreEqual(0.0, lat, 1e-9);
            Assert.AreEqual(0.0, lon, 1e-9);
            LayerGeometry.LatLon(0, 1, 0, out lat, out lon);
            Assert.AreEqual(90.0, lon, 1e-9);
            LayerGeometry.LatLon(0, 0, 1, out lat, out lon);
            Assert.AreEqual(90.0, lat, 1e-9);
        }
    }

    [TestClass]
    public class LayerTypeTableTests
    {
        private static CloudLayerDef TwoTypes()
        {
            var a = CloudTypeDef.Slab("A", 0.1f, 0.4f);
            var b = new CloudTypeDef("B", 0.4f, 0.9f, new Curve(new[] { 0f, 0.5f, 1f }, new[] { 0f, 1f, 0f }));
            return new CloudLayerDef("t", 0, 1000f, 5000f, new[] { a, b }, 0.01f);
        }

        [TestMethod]
        public void TheHeightsAtATypeValueAreTheLerpOfTheTwoTypes()
        {
            var table = new LayerTypeTable(TwoTypes());
            foreach (double t in new[] { 0.0, 0.25, 0.5, 0.8, 1.0 })
            {
                double b, top;
                table.InterpolatedHeights(t, out b, out top);
                Assert.AreEqual(0.1 + 0.3 * t, b, 1e-6, "base at " + t);
                Assert.AreEqual(0.4 + 0.5 * t, top, 1e-6, "top at " + t);
            }
        }

        [TestMethod]
        public void TheProfileIsZeroOutsideTheInterpolatedHeightsAndTheBlendInside()
        {
            var table = new LayerTypeTable(TwoTypes());
            Assert.AreEqual(0.0, table.Profile(0.0, 0.05), 1e-9);
            Assert.AreEqual(0.0, table.Profile(0.0, 0.45), 1e-9);
            Assert.AreEqual(1.0, table.Profile(0.0, 0.25), 1e-6);
            // Type B peaks in its middle (0.65); at the value 1 the middle of the layer share 0.65 is 1.
            Assert.AreEqual(1.0, table.Profile(1.0, 0.65), 1e-2);
            // Halfway the type value: heights (0.25, 0.65), share 0.45 is the middle: A's flat 1 and B's peak 1 blend to 1.
            Assert.AreEqual(1.0, table.Profile(0.5, 0.45), 1e-2);
        }

        [TestMethod]
        public void TheSampledProfileKeepsTheAuthoredIntegralWithinOnePercent()
        {
            var curve = new Curve(new[] { 0f, 0.3f, 0.6f, 1f }, new[] { 0f, 1f, 0.8f, 0f });
            var layer = new CloudLayerDef("c", 0, 2000f, 3000f, new[] { new CloudTypeDef("c", 0f, 1f, curve) }, 0.02f);
            var table = new LayerTypeTable(layer);
            double sum = 0.0;
            int n = 4000;
            for (int i = 0; i < n; i++) sum += table.ProfileAt(0, (i + 0.5) / n) / n;
            double exact = 0.5 * 0.3 * 1.0 + 0.5 * 0.3 * (1.0 + 0.8) + 0.5 * 0.4 * 0.8;
            Assert.AreEqual(exact, sum, exact * 0.01);
        }

        [TestMethod]
        public void ATypeNeedsABaseBelowItsTopAndALayerNeedsItsInnerBelowItsOuter()
        {
            Assert.ThrowsException<ArgumentException>(() => CloudTypeDef.Slab("x", 0.5f, 0.5f));
            Assert.ThrowsException<ArgumentException>(() => new CloudLayerDef("x", 0, 2000f, 1000f, new[] { CloudTypeDef.Slab("a", 0f, 1f) }, 0.01f));
            Assert.ThrowsException<ArgumentException>(() => new CloudLayerDef("x", 0, 1000f, 2000f, new CloudTypeDef[0], 0.01f));
        }

        [TestMethod]
        public void TheTightIntervalIsTheLowestBaseAndTheHighestTop()
        {
            float lo, hi;
            TwoTypes().TightAltitudes(out lo, out hi);
            Assert.AreEqual(1000f + 0.1f * 4000f, lo, 1e-3);
            Assert.AreEqual(1000f + 0.9f * 4000f, hi, 1e-3);
        }
    }

    [TestClass]
    public class LayerDensityTests
    {
        [TestMethod]
        public void TheCoverageThresholdPassesTheShareOfARankedMapAndIsHardAtZeroSoftness()
        {
            // A ranked (uniform) map: the share of texels above 1 - C is C, whatever the softness while the transition (centred on the
            // threshold, softness wide) lies inside the map's range 0 to 1.
            foreach (double c in new[] { 0.2, 0.4, 0.75 })
                foreach (double softness in new[] { 0.0, 0.05, 0.1 })
                {
                    double covered = 0.0;
                    int n = 20000;
                    for (int i = 0; i < n; i++) covered += LayerDensity.Shape((i + 0.5) / n, c, softness) / n;
                    Assert.AreEqual(c, covered, 0.003, "C " + c + " softness " + softness);
                }
            Assert.AreEqual(0.0, LayerDensity.Shape(0.59, 0.4, 0.0), 1e-9);
            Assert.AreEqual(1.0, LayerDensity.Shape(0.61, 0.4, 0.0), 1e-9);
        }

        [TestMethod]
        public void TheDistanceFadeIsOneNearZeroFarAndSmoothBetween()
        {
            var layer = new CloudLayerDef("f", 0, 1000f, 2000f, new[] { CloudTypeDef.Slab("a", 0f, 1f) }, 0.01f) { FadeNearM = 1000f, FadeFarM = 3000f };
            Assert.AreEqual(1.0, LayerDensity.DistanceFade(layer, 500.0), 1e-9);
            Assert.AreEqual(0.5, LayerDensity.DistanceFade(layer, 2000.0), 1e-9);
            Assert.AreEqual(0.0, LayerDensity.DistanceFade(layer, 3500.0), 1e-9);
            Assert.AreEqual(1.0, LayerDensity.DistanceFade(new CloudLayerDef("g", 0, 1000f, 2000f, new[] { CloudTypeDef.Slab("a", 0f, 1f) }, 0.01f), 1e9), 1e-9);
        }
    }

    [TestClass]
    public class LayerStackTests
    {
        private const double R = 600000.0;

        private static LayerStackSource TwoSlabs()
        {
            var source = new LayerStackSource(0, R, true);
            source.Add(new CloudLayerDef("high", 0, 3000f, 3400f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 3f / 400f), null, null);
            source.Add(new CloudLayerDef("low", 0, 1500f, 1900f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 2f / 400f), null, null);
            return source;
        }

        [TestMethod]
        public void TheStackIsKeptSortedByInnerAltitude()
        {
            LayerStackSource s = TwoSlabs();
            Assert.AreEqual("low", s.LayerAt(0).Name);
            Assert.AreEqual("high", s.LayerAt(1).Name);
            Assert.AreEqual(2, s.LayerCount);
        }

        [TestMethod]
        public void TheSpansOfAllLayersAreSortedNearestFirst()
        {
            LayerStackSource s = TwoSlabs();
            var spans = new LayerSpan[8];
            int n = LayerStack.Collect(s, 0, 0, R + 2.0, 0, 0, 1, double.MaxValue, spans);
            Assert.AreEqual(2, n);
            Assert.AreEqual(0, spans[0].Layer);
            Assert.AreEqual(1, spans[1].Layer);
            Assert.IsTrue(spans[0].T1 <= spans[1].T0);
            // From above the order flips.
            n = LayerStack.Collect(s, 0, 0, R + 9000.0, 0, 0, -1, 9000.0, spans);
            Assert.AreEqual(2, n);
            Assert.AreEqual(1, spans[0].Layer);
            Assert.AreEqual(0, spans[1].Layer);
        }

        [TestMethod]
        public void TheOpticalDepthOfASlabIsSigmaTimesTheSlantedPathAndTheStackAlphaIsTheProduct()
        {
            LayerStackSource s = TwoSlabs();
            LayerTypeTable[] tables = s.Tables();
            // Vertical: tau = 2 and 3.
            double t0 = LayerStack.OpticalDepth(s, tables, 0, 0, 0, R + 2.0, 0, 0, 1, double.MaxValue, 64);
            double t1 = LayerStack.OpticalDepth(s, tables, 1, 0, 0, R + 2.0, 0, 0, 1, double.MaxValue, 64);
            Assert.AreEqual(2.0, t0, 1e-3);
            Assert.AreEqual(3.0, t1, 1e-3);
            double a0 = 1.0 - Math.Exp(-t0), a1 = 1.0 - Math.Exp(-t1);
            double both = LayerStack.StackAlpha(s, tables, 0, 0, R + 2.0, 0, 0, 1, double.MaxValue, 64);
            Assert.AreEqual(LayerStack.CombinedAlpha(a0, a1), both, 1e-6);
        }

        [TestMethod]
        public void TheOpticalDepthDoesNotDependOnTheCameraBeingInsideThroughOrAboveTheShell()
        {
            var s = new LayerStackSource(0, R, true);
            s.Add(new CloudLayerDef("deck", 0, 1700f, 2500f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f), null, null);
            LayerTypeTable[] t = s.Tables();
            // The whole deck, from below and from above, vertically: sigma * 800.
            Assert.AreEqual(8.0, LayerStack.OpticalDepth(s, t, 0, 0, 0, R + 2.0, 0, 0, 1, double.MaxValue, 128), 1e-3);
            Assert.AreEqual(8.0, LayerStack.OpticalDepth(s, t, 0, 0, 0, R + 8000.0, 0, 0, -1, 8000.0, 128), 1e-3);
            // From inside at 2000 m: up 500 m, down 300 m.
            Assert.AreEqual(5.0, LayerStack.OpticalDepth(s, t, 0, 0, 0, R + 2000.0, 0, 0, 1, double.MaxValue, 128), 1e-3);
            Assert.AreEqual(3.0, LayerStack.OpticalDepth(s, t, 0, 0, 0, R + 2000.0, 0, 0, -1, 2000.0, 128), 1e-3);
        }
    }

    [TestClass]
    public class LayerSwitchesTests
    {
        [TestMethod]
        public void TheSystemIsOffByDefaultAndDrawsOnlyUnderTheOursSource()
        {
            Assert.IsFalse(LayerSwitches.OwnCloudsDefault);
            Assert.IsTrue(LayerSwitches.Active(true, CloudSourceKind.Ours));
            Assert.IsFalse(LayerSwitches.Active(false, CloudSourceKind.Ours));
            Assert.IsFalse(LayerSwitches.Active(true, CloudSourceKind.Eve));
            Assert.IsFalse(LayerSwitches.Active(true, CloudSourceKind.None));
        }

        [TestMethod]
        public void TheLayerCapIsClampedToTheStackAndToTheMaximum()
        {
            Assert.AreEqual(3, LayerSwitches.LayersToDraw(3, 4));
            Assert.AreEqual(1, LayerSwitches.LayersToDraw(3, 1));
            Assert.AreEqual(1, LayerSwitches.LayersToDraw(3, 0));
            Assert.AreEqual(0, LayerSwitches.LayersToDraw(0, 4));
            Assert.AreEqual(4, LayerSwitches.LayersToDraw(9, 99));
        }

        [TestMethod]
        public void TheStepQualityRisesWithTheLevel()
        {
            int s0, u0, s1, u1, s2, u2;
            float m0, m1, m2;
            LayerSwitches.StepQuality(0, out s0, out u0, out m0);
            LayerSwitches.StepQuality(1, out s1, out u1, out m1);
            LayerSwitches.StepQuality(2, out s2, out u2, out m2);
            Assert.IsTrue(s0 < s1 && s1 < s2);
            Assert.IsTrue(u0 <= u1 && u1 <= u2);
            Assert.IsTrue(m0 > m1 && m1 > m2);
        }
    }

    [TestClass]
    public class LayerMapLayoutTests
    {
        [TestMethod]
        public void TheMapsOfALayerAreReadInTheFieldsLayoutAndTurnedByTheLayersShift()
        {
            var map = new WeatherMap();
            map.Resize(8, 4);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 8; x++) map.Values[y * 8 + x] = x / 8f + 0.01f * y;
            var source = new LayerStackSource(0, 600000.0, true);
            var layer = new CloudLayerDef("m", 0, 1000f, 2000f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f) { MapShiftDeg = 45f };
            source.Add(layer, map, null);
            float cov, typ;
            source.SampleMaps(0, 30.0, 10.0, out cov, out typ);
            // A layer turned 45 degrees east shows at longitude 10 what the map holds at longitude -35.
            Assert.AreEqual(map.Sample(30.0, 10.0 - 45.0), cov, 1e-6);
            Assert.AreEqual(0f, typ, 1e-9);
        }

        [TestMethod]
        public void ALayerWithoutMapsIsFullyCoveredWithTheFirstType()
        {
            var source = new LayerStackSource(0, 600000.0, true);
            source.Add(new CloudLayerDef("n", 0, 1000f, 2000f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f), null, null);
            float cov, typ;
            source.SampleMaps(0, -20.0, 100.0, out cov, out typ);
            Assert.AreEqual(1f, cov);
            Assert.AreEqual(0f, typ);
        }

        [TestMethod]
        public void TheStackHoldsAtMostFourLayers()
        {
            var source = new LayerStackSource(0, 600000.0, true);
            for (int i = 0; i < CloudLayerDef.MaxLayers; i++)
                source.Add(new CloudLayerDef("l" + i, 0, 1000f * (i + 1), 1000f * (i + 1) + 500f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f), null, null);
            Assert.ThrowsException<InvalidOperationException>(() =>
                source.Add(new CloudLayerDef("extra", 0, 9000f, 9500f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f), null, null));
        }
    }
}
