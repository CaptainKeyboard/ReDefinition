using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // The weather seam in Common: the level scale, the gust function, the merge of providers, the view, the null field, the sun's
    // transmission, and the rules of the seam's source (documented types, no Unity objects in public members).
    [TestClass]
    public class WeatherSeamTests
    {
        private sealed class FixedSource : WeatherSourceBase
        {
            private readonly string name;
            private readonly int priority;
            private readonly WeatherSample sample;

            internal FixedSource(string name, int priority, WeatherSample sample)
            {
                this.name = name;
                this.priority = priority;
                this.sample = sample;
            }

            public override string Name { get { return name; } }
            public override int Priority { get { return priority; } }

            public override bool TrySample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample s)
            {
                s = sample;
                return true;
            }
        }

        private sealed class FixedField : IWeatherField
        {
            internal WeatherSample Value;
            private long version;

            public bool Sample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                sample = Value;
                return true;
            }

            public bool GetMap(int bodyIndex, WeatherMapKind kind, double ut, WeatherMap target) { return false; }
            public bool FillWindow(int bodyIndex, double centreLatDeg, double centreLonDeg, double radiusKm, double ut, WeatherWindow target) { return false; }
            public void Advance(double budgetMs) { version++; }
            public long Version { get { return version; } }
            public long DefinitionId { get { return 7; } }
            public bool ThreadSafe { get { return true; } }
            public double StepSeconds { get { return 3600.0; } }
            public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
        }

        private static WeatherSample Make(float level, float visibilityM, float wind, float optical, float cover)
        {
            WeatherSample s = WeatherSample.Clear(0);
            s.Level01 = level;
            s.Type = level > 0 ? PrecipType.Snow : PrecipType.None;
            s.VisibilityM = visibilityM;
            s.WindWorld = new Vector3(wind, 0f, 0f);
            s.OpticalDepthVertical = optical;
            s.CloudCover01 = cover;
            s.TemperatureK = 260f;
            return s;
        }

        // The hub is static; every test that changes it puts it back.
        private static void Reset(IWeatherSource[] sources)
        {
            foreach (IWeatherSource s in sources) WeatherHub.Unregister(s);
            WeatherHub.Field = null;
        }

        [DataTestMethod]
        [DataRow(0.039f, 0f, PrecipBand.None)]
        [DataRow(0.039f, 30f, PrecipBand.None)]
        [DataRow(0.04f, 0f, PrecipBand.Flurries)]
        [DataRow(0.1999f, 0f, PrecipBand.Flurries)]
        [DataRow(0.2f, 0f, PrecipBand.Light)]
        [DataRow(0.3999f, 0f, PrecipBand.Light)]
        [DataRow(0.4f, 0f, PrecipBand.Moderate)]
        [DataRow(0.5999f, 0f, PrecipBand.Moderate)]
        [DataRow(0.6f, 0f, PrecipBand.Heavy)]
        [DataRow(0.7999f, 30f, PrecipBand.Heavy)]
        [DataRow(0.8f, 15.5f, PrecipBand.Heavy)]
        [DataRow(0.8f, 15.6f, PrecipBand.Blizzard)]
        [DataRow(1.0f, 0f, PrecipBand.Heavy)]
        [DataRow(1.0f, 15.59f, PrecipBand.Heavy)]
        [DataRow(1.0f, 20f, PrecipBand.Blizzard)]
        [DataRow(float.NaN, 20f, PrecipBand.None)]
        public void TheBandsFollowTheThresholdsAndBlizzardNeedsTheWind(float level, float wind, PrecipBand expected)
        {
            Assert.AreEqual(expected, PrecipScale.BandOf(level, wind), level + " at " + wind);
        }

        [TestMethod]
        public void TheLevelOfARateIsLinearBetweenTheBandRatesAndMonotone()
        {
            float[] rates = { 0.1f, 0.5f, 1.5f, 4f, 10f };
            Assert.AreEqual(0f, PrecipScale.LevelFromRate(0f, rates));
            Assert.AreEqual(0f, PrecipScale.LevelFromRate(-3f, rates));
            Assert.AreEqual(0.04f, PrecipScale.LevelFromRate(0.1f, rates), 1e-6);
            Assert.AreEqual(0.2f, PrecipScale.LevelFromRate(0.5f, rates), 1e-6);
            Assert.AreEqual(0.4f, PrecipScale.LevelFromRate(1.5f, rates), 1e-6);
            Assert.AreEqual(0.6f, PrecipScale.LevelFromRate(4f, rates), 1e-6);
            Assert.AreEqual(0.8f, PrecipScale.LevelFromRate(10f, rates), 1e-6);
            Assert.AreEqual(0.3f, PrecipScale.LevelFromRate(1.0f, rates), 1e-6, "halfway between Light and Moderate");
            Assert.AreEqual(1f, PrecipScale.LevelFromRate(16f, rates), 1e-6, "five rates: the top band is as wide as the one below");
            Assert.AreEqual(1f, PrecipScale.LevelFromRate(900f, rates));
            float previous = -1f;
            for (float rate = 0f; rate < 30f; rate += 0.01f)
            {
                float level = PrecipScale.LevelFromRate(rate, rates);
                Assert.IsTrue(level >= previous, "monotone at " + rate);
                previous = level;
            }
            float[] six = { 0.1f, 0.5f, 1.5f, 4f, 10f, 12f };
            Assert.AreEqual(1f, PrecipScale.LevelFromRate(12f, six), 1e-6);
        }

        [TestMethod]
        public void TheLevelOfARateNeedsFiveBandRates()
        {
            Assert.ThrowsException<ArgumentException>(() => PrecipScale.LevelFromRate(1f, null));
            Assert.ThrowsException<ArgumentException>(() => PrecipScale.LevelFromRate(1f, new[] { 1f, 2f }));
        }

        [TestMethod]
        public void TheGustIsTheSameForTheSameInputsAndStaysWithinItsDepth()
        {
            var rng = new System.Random(12345);
            double maxWave = 0;
            for (int i = 0; i < 100000; i++)
            {
                double ut = rng.NextDouble() * 1e7;
                var pos = new Vector3((float)(rng.NextDouble() * 1.2e6 - 6e5), (float)(rng.NextDouble() * 1.2e6 - 6e5), (float)(rng.NextDouble() * 1.2e6 - 6e5));
                var dir = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, 0f);
                float level = (float)rng.NextDouble();
                int seed = rng.Next();
                float a = WindNoise.Gust(ut, pos, dir, level, seed);
                float b = WindNoise.Gust(ut, pos, dir, level, seed);
                Assert.AreEqual(a, b, "deterministic");
                float depth = WindNoise.Depth(level);
                Assert.IsTrue(a >= 1f - depth - 1e-5f && a <= 1f + depth + 1e-5f, "within the depth");
                maxWave = Math.Max(maxWave, Math.Abs(WindNoise.Wave(ut, pos, dir, seed)));
            }
            Assert.IsTrue(maxWave > 0.8 && maxWave <= 1.0, "the wave uses its range: " + maxWave);
            Assert.AreEqual(0.1f, WindNoise.Depth(0f), 1e-6);
            Assert.AreEqual(0.1f, WindNoise.Depth(0.04f), 1e-6);
            Assert.AreEqual(0.4f, WindNoise.Depth(1f), 1e-6);
            Assert.AreNotEqual(WindNoise.Gust(100.0, Vector3.zero, Vector3.right, 0.5f, 1), WindNoise.Gust(100.0, Vector3.zero, Vector3.right, 0.5f, 2), "the seed changes the pattern");
        }

        [TestMethod]
        public void TheGustTravelsWithThePositionAlongTheWind()
        {
            Vector3 dir = Vector3.right;
            // A point downwind sees the pattern later: the wave at (x, t) equals the wave at (0, t - x/c) for the travel speed of the pattern.
            float at0 = WindNoise.Wave(500.0, Vector3.zero, dir, 9);
            float perpendicular = WindNoise.Wave(500.0, new Vector3(0f, 800f, 300f), dir, 9);
            Assert.AreEqual(at0, perpendicular, 1e-4f, "points across the wind see the same gust");
            float downwind = WindNoise.Wave(500.0, new Vector3(120f, 0f, 0f), dir, 9);
            float later = WindNoise.Wave(500.0 - 10.0, Vector3.zero, dir, 9);
            Assert.AreEqual(downwind, later, 1e-3f, "12 m/s travel speed: 120 m downwind is 10 s earlier");
        }

        [TestMethod]
        public void TheGustAtOnePlaceChangesWithinTheWindSlewLimit()
        {
            // The strongest case: depth 0.4 on a 16 m/s wind. The change of the wind per second must stay under 8 m/s per s.
            float mean = 16f;
            float depth = WindNoise.Depth(1f);
            float previous = mean * WindNoise.Gust(0.0, Vector3.zero, Vector3.right, 1f, 3);
            float worst = 0f;
            const double dt = 0.05;
            for (int i = 1; i < 100000; i++)
            {
                float current = mean * WindNoise.Gust(i * dt, Vector3.zero, Vector3.right, 1f, 3);
                worst = Math.Max(worst, Math.Abs(current - previous) / (float)dt);
                previous = current;
            }
            Assert.IsTrue(worst <= WindNoise.MaxWindSlewMsPerS, "worst change " + worst + " m/s per s");
            Assert.IsTrue(depth * mean > 6f, "the case is the strong one");
        }

        [TestMethod]
        public void TheLimitedGustStaysWithinTheSlewLimitForAStrongWindAndAMovingObserver()
        {
            // A mean wind of 25 m/s and an observer at 100 m/s: Gust alone exceeds the limit, so the consumer applies LimitWind.
            const float mean = 25f;
            const double dt = 0.05;
            float rawWorst = 0f, limitedWorst = 0f;
            float previousRaw = mean * WindNoise.Gust(0.0, Vector3.zero, Vector3.right, 1f, 3);
            float limited = previousRaw;
            for (int i = 1; i < 40000; i++)
            {
                double ut = i * dt;
                var position = new Vector3((float)(100.0 * ut), 0f, 0f);
                float raw = mean * WindNoise.Gust(ut, position, Vector3.right, 1f, 3);
                rawWorst = Math.Max(rawWorst, Math.Abs(raw - previousRaw) / (float)dt);
                previousRaw = raw;
                float next = WindNoise.LimitWind(limited, raw, (float)dt);
                limitedWorst = Math.Max(limitedWorst, Math.Abs(next - limited) / (float)dt);
                limited = next;
            }
            Assert.IsTrue(rawWorst > WindNoise.MaxWindSlewMsPerS, "the raw gust is the case that needs the limit: " + rawWorst);
            Assert.IsTrue(limitedWorst <= WindNoise.MaxWindSlewMsPerS + 1e-3f, "limited change " + limitedWorst + " m/s per s");
        }

        [TestMethod]
        public void TheSlewLimitsHoldOverManyRandomSteps()
        {
            Assert.AreEqual(0.25f, WindNoise.MaxVisibilitySlewPerS);
            Assert.AreEqual(30f, WindNoise.MaxFlakeDirectionDegPerS);
            Assert.AreEqual(8f, WindNoise.MaxWindSlewMsPerS);
            var rng = new System.Random(99);
            float vis = 5000f, dir = 10f, wind = 5f;
            for (int i = 0; i < 100000; i++)
            {
                float dt = (float)(rng.NextDouble() * 0.2);
                float targetVis = (float)(rng.NextDouble() * 20000), targetDir = (float)(rng.NextDouble() * 720 - 360), targetWind = (float)(rng.NextDouble() * 40);
                float v = WindNoise.LimitVisibility(vis, targetVis, dt);
                Assert.IsTrue(Math.Abs(v - vis) <= vis * 0.25f * dt + 1e-3f, "visibility slew");
                float d = WindNoise.LimitFlakeDirection(dir, targetDir, dt);
                float turn = (float)(((d - dir) % 360.0 + 540.0) % 360.0 - 180.0);
                Assert.IsTrue(Math.Abs(turn) <= 30f * dt + 1e-3f, "direction slew");
                float w = WindNoise.LimitWind(wind, targetWind, dt);
                Assert.IsTrue(Math.Abs(w - wind) <= 8f * dt + 1e-4f, "wind slew");
                vis = Math.Max(v, 1f);
                dir = d;
                wind = w;
            }
            Assert.AreEqual(100f, WindNoise.LimitVisibility(100f, 100f, 1f));
            Assert.AreEqual(349.9f, WindNoise.LimitFlakeDirection(340f, 10f, 0.33f), 1e-3f, "the short way round 360 is to turn up, 30 degrees per second");
            Assert.AreEqual(370f, WindNoise.LimitFlakeDirection(340f, 10f, 2f), 1e-3f, "reaches it when the step is long enough (the angle is not wrapped)");
            Assert.AreEqual(-5f, WindNoise.LimitFlakeDirection(10f, -10f, 0.5f), 1e-3f, "the short way down");
        }

        [TestMethod]
        public void TheHighestPriorityOwnsTheWindAndVisibilityAndTransmissionMultiply()
        {
            var field = new FixedField { Value = Make(0.5f, 20000f, 3f, 0.5f, 0.4f) };
            var low = new FixedSource("low", 5, Make(0.2f, 50000f, 9f, 1f, 0.5f));
            var high = new FixedSource("high", 10, Make(0.7f, 40000f, 14f, 0.25f, 0.5f));
            var negative = new FixedSource("negative", -3, Make(0.1f, 100000f, 99f, 0f, 0f));
            WeatherHub.Field = field;
            WeatherHub.Register(low);
            WeatherHub.Register(negative);
            WeatherHub.Register(high);
            try
            {
                WeatherSample s;
                Assert.IsTrue(WeatherHub.Sample(2, 10.0, 20.0, 0.0, 100.0, out s));
                Assert.AreEqual(14f, s.WindWorld.x, "the highest priority owns the wind");
                Assert.AreEqual(0.7f, s.Level01, "the largest level");
                float expectedVisibility = WeatherSample.ClearVisibilityM * (20000f / 100000f) * (50000f / 100000f) * (40000f / 100000f);
                Assert.AreEqual(expectedVisibility, s.VisibilityM, 1e-2f, "visibility multiplies");
                Assert.AreEqual(1.75f, s.OpticalDepthVertical, 1e-5f, "optical depths add, so transmissions multiply");
                Assert.AreEqual(1f - 0.6f * 0.5f * 0.5f, s.CloudCover01, 1e-5f, "cover combines as independent layers");
                Assert.AreEqual(2, s.BodyIndex);

                WeatherHub.Unregister(high);
                Assert.IsTrue(WeatherHub.Sample(2, 10.0, 20.0, 0.0, 100.0, out s));
                Assert.AreEqual(9f, s.WindWorld.x, "the next one takes the wind");
                WeatherHub.Unregister(low);
                Assert.IsTrue(WeatherHub.Sample(2, 10.0, 20.0, 0.0, 100.0, out s));
                Assert.AreEqual(3f, s.WindWorld.x, "the field is priority 0, above a negative priority");
            }
            finally
            {
                Reset(new IWeatherSource[] { low, high, negative });
            }
        }

        [TestMethod]
        public void TheHubVersionChangesWithTheFieldAndTheRegistrations()
        {
            var field = new FixedField { Value = Make(0f, 100000f, 0f, 0f, 0f) };
            var source = new FixedSource("s", 1, Make(0f, 100000f, 0f, 0f, 0f));
            WeatherHub.Field = field;
            try
            {
                long v0 = WeatherHub.Version;
                field.Advance(1.0);
                long v1 = WeatherHub.Version;
                Assert.AreNotEqual(v0, v1, "the field's version");
                WeatherHub.Register(source);
                long v2 = WeatherHub.Version;
                Assert.AreNotEqual(v1, v2, "a provider added");
                WeatherHub.Register(source);
                Assert.AreEqual(v2, WeatherHub.Version, "the same provider twice is not added again");
                Assert.AreEqual(1, WeatherHub.SourceCount);
                Assert.IsTrue(WeatherHub.Unregister(source));
                Assert.IsFalse(WeatherHub.Unregister(source));
            }
            finally
            {
                Reset(new IWeatherSource[] { source });
            }
        }

        [TestMethod]
        public void AReadThroughTheHubAllocatesNothing()
        {
            var field = new FixedField { Value = Make(0.5f, 20000f, 3f, 0.5f, 0.4f) };
            var a = new FixedSource("a", 5, Make(0.2f, 50000f, 9f, 1f, 0.5f));
            var b = new FixedSource("b", 10, Make(0.7f, 40000f, 14f, 0.25f, 0.5f));
            WeatherHub.Field = field;
            WeatherHub.Register(a);
            WeatherHub.Register(b);
            try
            {
                WeatherSample s;
                for (int i = 0; i < 100; i++) WeatherHub.Sample(0, 1.0, 2.0, 3.0, 4.0, out s);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) WeatherHub.Sample(0, 1.0, 2.0, 3.0, 4.0, out s);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.AreEqual(0L, after - before, "bytes allocated by 10000 reads");
            }
            finally
            {
                Reset(new IWeatherSource[] { a, b });
            }
        }

        [TestMethod]
        public void TheNullFieldIsClearAndTheHubStartsWithIt()
        {
            WeatherHub.Field = null;
            Assert.AreSame(NullField.Instance, WeatherHub.Field);
            WeatherSample s;
            Assert.IsFalse(NullField.Instance.Sample(3, 1.0, 2.0, 3.0, 4.0, out s), "it covers no body");
            Assert.AreEqual(0f, s.Level01);
            Assert.AreEqual(PrecipType.None, s.Type);
            Assert.AreEqual(WeatherSample.ClearVisibilityM, s.VisibilityM);
            Assert.AreEqual(Vector3.zero, s.WindWorld);
            Assert.AreEqual(0f, s.CloudCover01);
            Assert.AreEqual(0f, s.OpticalDepthVertical);
            Assert.AreEqual(3, s.BodyIndex);
            Assert.AreEqual(FieldReadiness.Ready, s.Readiness);
            Assert.IsTrue(float.IsNaN(s.TemperatureK), "unknown");
            Assert.IsFalse(WeatherHub.Sample(3, 1.0, 2.0, 3.0, 4.0, out s), "nothing knows the place");
            Assert.AreEqual(PrecipBand.None, s.Band);
            var map = new WeatherMap();
            map.Values = new[] { 1f, 2f };
            map.Width = 2;
            map.Height = 1;
            NullField.Instance.GetMap(0, WeatherMapKind.Coverage, 0.0, map);
            Assert.IsTrue(map.Values.All(v => v == 0f));
            Assert.AreEqual(0L, NullField.Instance.Version);
        }

        [TestMethod]
        public void TheTransmissionFallsWithTheOpticalDepthAndRisesWithTheSun()
        {
            Assert.AreEqual(1f, SunTransmission.From(0f, 1f));
            Assert.AreEqual(1f, SunTransmission.From(-2f, 1f));
            Assert.AreEqual(1f, SunTransmission.From(float.NaN, 1f));
            Assert.AreEqual((float)Math.Exp(-1.0), SunTransmission.From(1f, (float)(Math.PI / 2)), 1e-5f, "overhead: air mass 1");
            float previous = 2f;
            for (float tau = 0f; tau <= 20f; tau += 0.1f)
            {
                float t = SunTransmission.From(tau, 0.8f);
                Assert.IsTrue(t <= previous && t >= 0f && t <= 1f, "monotone in tau at " + tau);
                previous = t;
            }
            previous = -1f;
            for (float elevation = 0f; elevation <= (float)(Math.PI / 2); elevation += 0.01f)
            {
                float t = SunTransmission.From(1.5f, elevation);
                Assert.IsTrue(t >= previous, "monotone in the sun's height at " + elevation);
                previous = t;
            }
            Assert.AreEqual(SunTransmission.MaxAirMass, SunTransmission.AirMass(0f));
            Assert.AreEqual(SunTransmission.MaxAirMass, SunTransmission.AirMass(-0.5f), "below the horizon is clamped");
            Assert.IsTrue(SunTransmission.From(100f, 0f) >= 0f);
        }

        [TestMethod]
        public void TheViewRaisesItsVersionOnlyForMovesAboveTheEpsilon()
        {
            var view = new WeatherView();
            Assert.IsFalse(WeatherView.Moved(1f, 1f + WeatherView.VersionEpsilon * 0.5f));
            Assert.IsTrue(WeatherView.Moved(1f, 1f + WeatherView.VersionEpsilon * 2f));
            Assert.IsTrue(WeatherView.Moved(1f, float.NaN));
            Assert.IsFalse(WeatherView.Moved(float.NaN, float.NaN));
            int v = view.Version;
            view.Publish(10, false);
            Assert.AreEqual(v, view.Version);
            Assert.AreEqual(10, view.FrameStamp);
            view.Publish(11, true);
            Assert.AreEqual(v + 1, view.Version);
        }

        [TestMethod]
        public void TheViewIsStaleAfterTwoFramesOrWhenInactive()
        {
            var view = new WeatherView();
            view.Active = true;
            view.FrameStamp = 100;
            Assert.IsFalse(view.IsStale(100));
            Assert.IsFalse(view.IsStale(102));
            Assert.IsTrue(view.IsStale(103));
            view.Active = false;
            Assert.IsTrue(view.IsStale(100));
            view.Active = true;
            view.Level = 0.9f;
            view.SunTransmission = 0.2f;
            view.Clear();
            Assert.IsFalse(view.Active);
            Assert.AreEqual(0f, view.Level);
            Assert.AreEqual(1f, view.SunTransmission);
            Assert.AreEqual(1f, view.AmbientFactor);
            Assert.AreEqual(3, view.FlakeLayers.Length);
        }

        [TestMethod]
        public void TheCloudMapsStateRaisesItsVersionAndNamesTheLayer()
        {
            long v = CloudMapsState.Version;
            CloudMapsState.Bump("Kerbin-weather");
            Assert.AreEqual(v + 1, CloudMapsState.Version);
            Assert.AreEqual("Kerbin-weather", CloudMapsState.LayerName);
            CloudMapsState.Bump(null);
            Assert.AreEqual("", CloudMapsState.LayerName);
        }

        [TestMethod]
        public void AMapIsSampledBilinearlyAndWrapsAtTheDateLine()
        {
            var map = new WeatherMap();
            map.Resize(4, 2);
            for (int i = 0; i < 8; i++) map.Values[i] = i;
            Assert.AreEqual(0f, map.Sample(90.0, -180.0 + 45.0), 1e-5f, "north row, first column centre");
            Assert.AreEqual(1f, map.Sample(90.0, -90.0 + 45.0), 1e-5f);
            // Row 0 holds 0, 1, 2, 3 at the column centres -135, -45, 45, 135; at 179 the last column (3) blends into the first (0).
            Assert.AreEqual(3f * (1f - 0.4889f), map.Sample(60.0, 179.0), 1e-3f);
            Assert.AreEqual(map.Sample(60.0, 179.0), map.Sample(60.0, -181.0), 1e-4f, "the longitude wraps");
            map.PackBytes(0f, 7f);
            Assert.AreEqual(0, map.Bytes[0]);
            Assert.AreEqual(255, map.Bytes[7]);
            map.Resize(4, 2);
            Assert.AreEqual(8, map.Values.Length);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.Resize(0, 2));
        }

        [TestMethod]
        public void AWindowKeepsItsArraysWhileTheCellCountStays()
        {
            var window = new WeatherWindow();
            window.Resize(4, 4, 2);
            float[] first = window.CloudWater;
            window.Resize(2, 4, 4);
            Assert.AreSame(first, window.CloudWater, "same cell count, same arrays");
            window.Resize(8, 8, 8);
            Assert.AreEqual(512, window.CloudWater.Length);
            Assert.AreEqual(window.Count, window.Updraft.Length);
            Assert.AreEqual((1 * 8 + 2) * 8 + 3, window.Index(3, 2, 1));
        }

        [TestMethod]
        public void TheWindLabelNamesItsSource()
        {
            Assert.AreEqual("Ours", WeatherSource.Ours().ToString());
            Assert.AreEqual("Foreign(KWP)", WeatherSource.Foreign("KWP").ToString());
            Assert.AreEqual("ExternalNowcast(WindAPI)", WeatherSource.ExternalNowcast("WindAPI").ToString());
            var report = new ForecastReport();
            Assert.AreEqual(WeatherSourceKind.Ours, report.Wind.Kind);
            Assert.IsTrue(double.IsNaN(report.NearestSystemEtaUt));
            Assert.IsFalse(report.Available);
        }

        [TestMethod]
        public void AnAlertReachesEveryHandlerEvenWhenOneThrows()
        {
            int received = 0;
            Action<ForecastAlert> bad = a => { throw new InvalidOperationException("no"); };
            Action<ForecastAlert> good = a => { received++; };
            WeatherHooks.AlertRaised += bad;
            WeatherHooks.AlertRaised += good;
            try
            {
                WeatherHooks.RaiseAlert(new ForecastAlert { Kind = AlertKind.Approaching, Text = "x" });
                Assert.AreEqual(1, received);
            }
            finally
            {
                WeatherHooks.AlertRaised -= bad;
                WeatherHooks.AlertRaised -= good;
            }
            WeatherHooks.RaiseAlert(new ForecastAlert());
        }

        [TestMethod]
        public void TheForecastGenerationRises()
        {
            long g = WeatherHooks.ForecastGeneration;
            Assert.AreEqual(g + 1, WeatherHooks.BumpForecastGeneration());
            Assert.AreEqual(g + 1, WeatherHooks.ForecastGeneration);
        }

        [TestMethod]
        public void TheSeamHoldsOneViewAndOneSetOfCaps()
        {
            Assert.IsNotNull(WeatherHooks.View);
            Assert.IsNotNull(WeatherHooks.Caps);
            Assert.AreEqual(QualityTier.Auto, WeatherHooks.Caps.Tier);
            Assert.AreEqual(UpscalerKind.None, WeatherHooks.Caps.Upscaler);
            Assert.IsNull(WeatherHooks.CloudOpticalDepth);
        }

        // ---- The rules of the seam's source ----

        private static string WeatherFolder()
        {
            return Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Common", "Weather");
        }

        private static readonly Regex TypeDeclaration = new Regex(
            @"^\s*public\s+(?:sealed\s+|static\s+|abstract\s+)*(?<kind>class|struct|enum|interface)\s+(?<name>\w+)", RegexOptions.Multiline);

        private static List<KeyValuePair<string, string>> PublicTypeDeclarations()
        {
            var found = new List<KeyValuePair<string, string>>();
            foreach (string file in Directory.GetFiles(WeatherFolder(), "*.cs"))
            {
                foreach (Match m in TypeDeclaration.Matches(File.ReadAllText(file))) found.Add(new KeyValuePair<string, string>(Path.GetFileName(file), m.Groups["name"].Value));
            }
            return found;
        }

        [TestMethod]
        public void EveryPublicTypeOfTheSeamHasADocComment()
        {
            var missing = new List<string>();
            foreach (string file in Directory.GetFiles(WeatherFolder(), "*.cs"))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!TypeDeclaration.IsMatch(lines[i])) continue;
                    int j = i - 1;
                    while (j >= 0 && lines[j].TrimStart().StartsWith("[")) j--;
                    if (j < 0 || !lines[j].TrimStart().StartsWith("//")) missing.Add(Path.GetFileName(file) + ": " + lines[i].Trim());
                }
            }
            Assert.AreEqual(0, missing.Count, string.Join("\n", missing));
            Assert.IsTrue(PublicTypeDeclarations().Count >= 30, "types found: " + PublicTypeDeclarations().Count);
        }

        [TestMethod]
        public void TheSeamFilesTheBriefsNameExist()
        {
            foreach (string name in new[] { "IWeatherField.cs", "WeatherSample.cs", "WeatherView.cs", "WeatherHooks.cs", "PrecipScale.cs", "SunTransmission.cs" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(WeatherFolder(), name)), name);
            }
        }

        [TestMethod]
        public void NoPublicMemberOfTheSeamReturnsOrHoldsAUnityObject()
        {
            Assembly common = typeof(WeatherHub).Assembly;
            var offenders = new List<string>();
            foreach (KeyValuePair<string, string> declaration in PublicTypeDeclarations())
            {
                Type type = common.GetType("ReDefinition.Core." + declaration.Value);
                Assert.IsNotNull(type, declaration.Value);
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var types = new List<Type>();
                    var field = member as FieldInfo;
                    var property = member as PropertyInfo;
                    var method = member as MethodInfo;
                    var ev = member as EventInfo;
                    if (field != null) types.Add(field.FieldType);
                    if (property != null) types.Add(property.PropertyType);
                    if (method != null)
                    {
                        types.Add(method.ReturnType);
                        types.AddRange(method.GetParameters().Select(p => p.ParameterType));
                    }
                    if (ev != null) types.Add(ev.EventHandlerType);
                    foreach (Type t in types)
                    {
                        if (IsUnityObject(t)) offenders.Add(type.Name + "." + member.Name + " : " + t.Name);
                    }
                }
            }
            Assert.AreEqual(0, offenders.Count, string.Join("\n", offenders));
        }

        private static bool IsUnityObject(Type t)
        {
            while (t.HasElementType) t = t.GetElementType();
            if (t.IsGenericType)
            {
                foreach (Type argument in t.GetGenericArguments()) if (IsUnityObject(argument)) return true;
            }
            return typeof(UnityEngine.Object).IsAssignableFrom(t);
        }

        [TestMethod]
        public void TheUnityObjectCheckSeesAnObjectWhenThereIsOne()
        {
            Assert.IsTrue(IsUnityObject(typeof(Texture2D)));
            Assert.IsTrue(IsUnityObject(typeof(GameObject[])));
            Assert.IsFalse(IsUnityObject(typeof(Vector3)));
            Assert.IsFalse(IsUnityObject(typeof(Color)));
            Assert.IsFalse(IsUnityObject(typeof(Func<double, double, double, double, float>)));
        }

        [TestMethod]
        public void TheSeamNamesNoReDefinitionAssemblyBeyondCommon()
        {
            foreach (string file in Directory.GetFiles(WeatherFolder(), "*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(Regex.IsMatch(text, @"using ReDefinition\.(Ui|Atmospheres|Planner|Dashboard)"), file);
            }
        }
    }
}
