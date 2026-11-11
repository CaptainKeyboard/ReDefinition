using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

// The fixtures of the two foreign APIs, in the namespaces and shapes the mods ship them: WindAPI (Zondulus, CC0 1.0) and
// ModularFlightIntegrator (sarbian, MIT). Plain classes: the bridge reaches them by name and member only.
namespace WindAPI
{
    public interface IWindProvider
    {
        string ProviderID { get; }
        Vector3 GetWind(CelestialBody body, Part part, Vector3 position);
    }

    public class WindManager
    {
        public static WindManager Instance { get; set; }
        public readonly List<IWindProvider> Providers = new List<IWindProvider>();
        public Vector3 Other = new Vector3(0, 0, 0);

        public void RegisterProvider(IWindProvider provider)
        {
            if (!Providers.Contains(provider)) Providers.Add(provider);
        }

        public void DeregisterProvider(IWindProvider provider)
        {
            Providers.Remove(provider);
        }

        public Vector3 GetWindAtLocation(CelestialBody body, Part part, Vector3 position, IWindProvider ignoreProvider = null)
        {
            Vector3 sum = Other;
            foreach (IWindProvider p in Providers)
            {
                if (ReferenceEquals(p, ignoreProvider)) continue;
                sum += p.GetWind(body, part, position);
            }
            return sum;
        }
    }
}

namespace ModularFI
{
    public class ModularFlightIntegrator
    {
        public delegate void voidPartDelegate(ModularFlightIntegrator fi, Part part);

        public static voidPartDelegate Held;
        public static int BaseCalls;

        public static bool RegisterUpdateAerodynamicsOverride(voidPartDelegate function)
        {
            if (Held != null) return false;
            Held = function;
            return true;
        }

        public void BaseFIUpdateAerodynamics(Part part)
        {
            BaseCalls++;
        }
    }
}

namespace ReDefinition.Tests.Atmospheres.D31
{
    [TestClass]
    public class SolarVisibilityBridgeTests
    {
        // --- Solar dimming ---

        [TestMethod]
        public void TheTransmissionAtThreeSkyStatesMatchesTheCoverageAndTheOneDefinition()
        {
            double elevation = 60.0 * Math.PI / 180.0;
            // clear, broken, overcast: the vertical optical depth and the cover that goes with it
            double[] tau = { 0.0, 1.0, 6.0 };
            double[] cover = { 0.0, 0.5, 1.0 };
            double previous = 2.0;
            for (int i = 0; i < 3; i++)
            {
                double t = SolarMath.Transmission(tau[i], elevation);
                Assert.AreEqual(Math.Exp(-tau[i] / Math.Sin(elevation)), t, 1e-5, "the sky state " + i + " (cover " + cover[i] + ")");
                Assert.IsTrue(t < previous, "the transmission falls as the visible cover rises");
                previous = t;
            }
            Assert.AreEqual(1.0, SolarMath.Transmission(0.0, elevation));
        }

        [TestMethod]
        public void ASunTransmissionQueryCostsUnderFiftyMicroseconds()
        {
            double sum = 0;
            for (int i = 0; i < 1000; i++) sum += SolarMath.Factor(SolarMath.Transmission(i * 0.01, 0.5), 1.0, 1.0);
            var watch = Stopwatch.StartNew();
            const int n = 100000;
            for (int i = 0; i < n; i++) sum += SolarMath.Factor(SolarMath.Transmission((i % 600) * 0.01, 0.3 + (i % 50) * 0.02), 1.0, 1.0);
            watch.Stop();
            double us = watch.Elapsed.TotalMilliseconds * 1000.0 / n;
            Console.WriteLine("D31 sun transmission query: " + us.ToString("0.000") + " us");
            Assert.IsTrue(us < 50.0, "a query took " + us + " us");
            Assert.IsFalse(double.IsNaN(sum));
        }

        [TestMethod]
        public void TheFactorFollowsTheTransmissionTheStormLossTheBodyAndTheStrength()
        {
            Assert.AreEqual(1.0, SolarMath.Factor(1.0, 1.0, 1.0));
            Assert.AreEqual(1.0 - SolarMath.StormSolarLoss, SolarMath.Factor(0.0, 1.0, 1.0), 1e-12, "a sky that lets nothing through leaves 15 %");
            Assert.AreEqual(1.0 - 0.5 * SolarMath.StormSolarLoss, SolarMath.Factor(0.5, 1.0, 1.0), 1e-12);
            Assert.AreEqual(1.0, SolarMath.Factor(0.0, 1.0, 0.0), 1e-12, "strength 0 is off");
            Assert.AreEqual(1.0 - 0.5 * SolarMath.StormSolarLoss, SolarMath.Factor(0.0, 1.0, 0.5), 1e-12);
            Assert.AreEqual(0.0, SolarMath.Factor(0.0, 5.0, 1.0), "the loss never goes below none left");
            Assert.AreEqual(1.0, SolarMath.Factor(double.NaN, 1.0, 1.0));
        }

        [TestMethod]
        public void TheFactorIsSmoothedOverTwoSeconds()
        {
            double f = 1.0;
            for (int i = 0; i < 100; i++) f = SolarMath.Smooth(f, 0.4, 0.02);
            double expected = 0.4 + 0.6 * Math.Exp(-2.0 / SolarMath.SmoothingSeconds);
            Assert.AreEqual(expected, f, 1e-6);
            Assert.AreEqual(0.7, SolarMath.Smooth(0.7, 0.2, 0.0));
        }

        [TestMethod]
        public void TheScaledCurveIsACopyThatMultipliesEveryValueAndLeavesTheOriginal()
        {
            // The FloatCurve itself needs the engine; the scaling is on its keys.
            var original = new[] { new Keyframe(0f, 1f, 0f, 0f), new Keyframe(100f, 0.8f, -0.002f, -0.004f) };
            Keyframe[] copy = (Keyframe[])original.Clone();
            Keyframe[] scaled = SolarDimming.ScaleKeys(copy, 0.5f);
            Assert.AreEqual(0.5f, scaled[0].value, 1e-6f);
            Assert.AreEqual(0.4f, scaled[1].value, 1e-6f);
            Assert.AreEqual(-0.001f, scaled[1].inTangent, 1e-9f);
            Assert.AreEqual(-0.002f, scaled[1].outTangent, 1e-9f);
            Assert.AreEqual(1f, original[0].value, "the panel's own keys are untouched");
            Assert.AreEqual(0.8f, original[1].value);
        }

        // --- Visibility ---

        [TestMethod]
        public void TheVisibilityLineReadsInMetresBelowAKilometre()
        {
            Assert.AreEqual("visibility 400 m", VisibilityExport.Format(400f));
            Assert.AreEqual("visibility 60 m", VisibilityExport.Format(62f));
            Assert.AreEqual("visibility 1.2 km", VisibilityExport.Format(1234f));
            Assert.AreEqual("visibility 100 km", VisibilityExport.Format(100000f));
            Assert.AreEqual("", VisibilityExport.Format(float.NaN));
        }

        [TestMethod]
        public void TheExportReadsTheViewWhileItIsFreshAndTheSampleOtherwise()
        {
            var view = new WeatherView { Active = true, VisibilityM = 350f };
            view.Publish(100, true);
            Assert.AreEqual(350f, VisibilityExport.Read(view, 101, true, 900f));
            Assert.AreEqual(900f, VisibilityExport.Read(view, 200, true, 900f), "a stale view: the sample");
            Assert.IsTrue(float.IsNaN(VisibilityExport.Read(view, 200, false, 0f)));
        }

        // --- WindAPI bridge ---

        [TestMethod]
        public void TheWindApiBridgeRegistersAProviderAndReadsTheOthersWithoutItsOwn()
        {
            var manager = new WindAPI.WindManager { Other = new Vector3(0, 0, 2) };
            WindAPI.WindManager.Instance = manager;
            var bridge = new WindApiBridge();
            Assert.IsTrue(bridge.Connect(), bridge.Status);
            Assert.AreEqual("WindAPI", bridge.Source.Name);
            Assert.AreEqual(50, bridge.Source.Priority);
            Assert.AreEqual(0, manager.Providers.Count);

            WindApiCallbacks.OurWind = p => new Vector3(1, 2, 3);
            bridge.Register();
            Assert.AreEqual(1, manager.Providers.Count);
            Assert.AreEqual("ReDefinition", manager.Providers[0].ProviderID);
            Assert.AreEqual(new Vector3(1, 2, 3), manager.Providers[0].GetWind(null, null, Vector3.zero), "FAR gets our wind through the provider");

            // The sum WindAPI gives FAR is ours plus the others; the bridge's read leaves ours out.
            Vector3 sum = manager.GetWindAtLocation(null, null, Vector3.zero);
            Assert.AreEqual(new Vector3(1, 2, 5), sum);
            Assert.AreEqual(new Vector3(0, 0, 2), bridge.OthersAt(null, Vector3.zero));

            bridge.Deregister();
            Assert.AreEqual(0, manager.Providers.Count);
            WindApiCallbacks.OurWind = null;
            WindAPI.WindManager.Instance = null;
            Assert.AreEqual(Vector3.zero, bridge.OthersAt(null, Vector3.zero), "no manager yet: calm");
        }

        // --- The ModularFlightIntegrator override ---

        [TestMethod]
        public void TheOverrideIsRegisteredOnceAndCallsTheBaseWhenThereIsNoVessel()
        {
            ModularFI.ModularFlightIntegrator.Held = null;
            Assert.IsTrue(WindHook.IntegratorPresent);
            Assert.IsTrue(WindHook.Register(), WindHook.Status);
            Assert.IsNotNull(ModularFI.ModularFlightIntegrator.Held);
            int before = ModularFI.ModularFlightIntegrator.BaseCalls;
            ModularFI.ModularFlightIntegrator.Held(new ModularFI.ModularFlightIntegrator(), null);
            Assert.AreEqual(before + 1, ModularFI.ModularFlightIntegrator.BaseCalls, "the base update still runs");
            Assert.IsTrue(WindHook.Register(), "a second call is a no-op");
        }

        // --- The assumptions ---

        [TestMethod]
        public void TheStockNamesTheWindAndTheSolarDimmingRelyOnAreThere()
        {
            Assert.AreEqual(ForeignState.Verified, AtmospheresAssumptions.Find("atmo-wind-aero").State, AtmospheresAssumptions.Find("atmo-wind-aero").Problem);
            Assert.AreEqual(ForeignState.Verified, AtmospheresAssumptions.Find("atmo-solar-panel").State, AtmospheresAssumptions.Find("atmo-solar-panel").Problem);
            Assert.IsNotNull(AtmospheresAssumptions.Find("atmo-wind-mfi"));
            Assert.AreNotEqual(ForeignState.Broken, AtmospheresAssumptions.Find("atmo-wind-mfi").State);
        }
    }
}
