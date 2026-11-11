using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Wind that acts on vehicles (package D31): decides, from windSource, windInjection and the mods that are loaded, the one way the wind
    // reaches the aerodynamics (WindPlanner), registers the ModularFlightIntegrator override where it is wanted, connects the WindAPI
    // bridge, puts the foreign wind source into the weather seam and the forecast, and says once per flight who owns the wind. The
    // per-tick work is in WindService, WindHook and WindForceModule. With weatherEffects or windForces off the module does nothing and no
    // hook is registered.
    [AtmospheresModule(62)]
    internal sealed class WindModule : IAtmospheresModule
    {
        private ForeignWinds foreign = ForeignWinds.None;
        private bool scanned;
        private readonly WindApiBridge windApi = new WindApiBridge();
        private bool windApiRegistered, sourceInHub, sourceInForecast;
        private string planKey = "";
        private bool announced;
        private bool eventsHooked;

        // A stand-in for the scan of the loaded assemblies, set by the series probe to put a foreign wind mod in or out of the picture
        // (the row "numeric A/B per mode with a foreign mod stub"). Null in the game.
        internal static ForeignWinds ForeignOverride;

        // The plan in force, for the settings window and the probe.
        internal static WindPlan Current
        {
            get { return WindService.Plan; }
        }

        // The sentence for the settings window.
        internal static string StatusText
        {
            get { return WindService.Plan.Status; }
        }

        public void Init()
        {
            HookEvents();
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            bool on = AtmospheresSettings.Enabled && AtmospheresSettings.WeatherEffects;
            if (!on || !AtmospheresSettings.WindForces)
            {
                SetPlan(new WindPlan { Route = WindRoute.Off, Status = WindStatus.Text(WindSourceMode.Off, null) });
                SyncSources(false);
                announced = false;
                return;
            }
            if (!scanned) Scan();
            WindSourceMode mode = AtmospheresSettings.WindSource;
            WindInjection injection = AtmospheresSettings.WindInjection;

            // The route the settings ask for; the override is registered only where the plan wants it, so that a mod that holds the slot
            // keeps it and the wind then goes in as a force.
            bool mfi = WindHook.Registered;
            ForeignWinds known = ForeignOverride ?? foreign;
            WindPlan wish = WindPlanner.Decide(mode, known, injection, WindHook.IntegratorPresent, true);
            if (wish.Route == WindRoute.Aero && !WindHook.Registered)
            {
                mfi = WindHook.Register();
                if (!mfi) AtmospheresLog.WarnOnce("wind: the ModularFlightIntegrator override was not registered (" + WindHook.Status + "); the wind goes in as a force.");
            }
            WindPlan plan = WindPlanner.Decide(mode, known, injection, mfi, true);
            if (windApi.Connected)
            {
                // The plan's route Far is served by our provider in WindAPI.
                if (plan.Route == WindRoute.Far && !windApiRegistered)
                {
                    WindApiCallbacks.OurWind = PartWind;
                    windApi.Register();
                    windApiRegistered = true;
                }
            }
            string key = mode + "/" + injection + "/" + plan.Route + "/" + (mfi ? "m" : "-") + "/" + known.Names();
            if (key != planKey)
            {
                planKey = key;
                SetPlan(plan);
                announced = false;
            }
            SyncSources(true);
            Announce();
        }

        public void Shutdown()
        {
            SyncSources(false);
            if (windApiRegistered)
            {
                try
                {
                    windApi.Deregister();
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("wind-bridge", "the WindAPI provider could not be removed", e);
                }
                windApiRegistered = false;
            }
            SetPlan(new WindPlan { Route = WindRoute.Off });
            WindService.ForgetAll();
            UnhookEvents();
        }

        private void Scan()
        {
            scanned = true;
            var names = new List<string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    names.Add(a.GetName().Name);
                }
                catch (Exception)
                {
                    // An assembly whose name cannot be read is not a wind mod.
                }
            }
            foreign = ForeignWindDetector.Detect(names);
            if (foreign.WindApiPresent && windApi.Connect())
            {
                AtmospheresLog.Say("wind: WindAPI bridge connected (experimental)");
            }
            else if (foreign.WindApiPresent)
            {
                CompatibilityLog.Warn("wind-windapi", "Atmospheres: WindAPI is installed but the bridge stays off: " + windApi.Status);
            }
            AtmospheresLog.Say("wind: other wind mods found: " + foreign.Names());
        }

        private void SetPlan(WindPlan plan)
        {
            WindService.Plan = plan;
        }

        // The wind source of the bridge is in the weather seam in Auto and Foreign (the pictures then follow the wind that is applied), and
        // in the forecast in every mode but Off, where the forecast's own windSource order keeps it apart.
        private void SyncSources(bool active)
        {
            IWeatherSource source = windApi.Connected ? windApi.Source : null;
            WindSourceMode mode = AtmospheresSettings.WindSource;
            bool wantHub = active && source != null && (mode == WindSourceMode.Auto || mode == WindSourceMode.Foreign);
            if (wantHub && !sourceInHub)
            {
                WeatherHub.Register(source);
                sourceInHub = true;
            }
            else if (!wantHub && sourceInHub)
            {
                WeatherHub.Unregister(source);
                sourceInHub = false;
            }
            bool wantForecast = source != null && active;
            if (wantForecast && !sourceInForecast)
            {
                ForecastWindProviders.Add(source);
                sourceInForecast = true;
            }
            else if (!wantForecast && sourceInForecast)
            {
                ForecastWindProviders.Remove(source);
                sourceInForecast = false;
            }
        }

        // The wind our WindAPI provider returns for a part.
        private static Vector3 PartWind(Part part)
        {
            Vessel v = part != null ? part.vessel : null;
            if (v == null || !WindService.Eligible(v) || WindService.Plan.Route != WindRoute.Far) return Vector3.zero;
            Vec3 w = WindService.WindFor(v);
            return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
        }

        // One line in the log per flight, and one screen message where a foreign wind owner is found.
        private void Announce()
        {
            if (!GameGlobals.Present || !HighLogic.LoadedSceneIsFlight || GameGlobals.ActiveVessel == null)
            {
                announced = false;
                return;
            }
            if (announced) return;
            announced = true;
            WindPlan plan = WindService.Plan;
            AtmospheresLog.Say(plan.OwnerLine() + ". " + plan.Status + (string.IsNullOrEmpty(plan.InjectionStatus) ? "" : " " + plan.InjectionStatus));
            if (!string.IsNullOrEmpty(plan.Warning)) AtmospheresLog.WarnOnce("wind: " + plan.Warning);
            if (plan.ForeignOwner != null)
            {
                try
                {
                    ScreenMessages.PostScreenMessage(plan.Status, 6f, ScreenMessageStyle.UPPER_CENTER);
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("wind-message", "the wind owner message could not be shown", e);
                }
            }
        }

        private void HookEvents()
        {
            if (eventsHooked) return;
            try
            {
                GameEvents.onVesselGoOffRails.Add(OnGoOffRails);
                GameEvents.onVesselDestroy.Add(OnVesselDestroy);
                GameEvents.onGameSceneLoadRequested.Add(OnSceneLoad);
                eventsHooked = true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("wind-events", "the wind could not subscribe to the vessel events", e);
            }
        }

        private void UnhookEvents()
        {
            if (!eventsHooked) return;
            try
            {
                GameEvents.onVesselGoOffRails.Remove(OnGoOffRails);
                GameEvents.onVesselDestroy.Remove(OnVesselDestroy);
                GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoad);
            }
            catch (Exception)
            {
                // The events are gone with the game.
            }
            eventsHooked = false;
        }

        private static void OnGoOffRails(Vessel v)
        {
            WindService.MarkUnpacked(v);
        }

        private static void OnVesselDestroy(Vessel v)
        {
            WindService.Forget(v);
        }

        private static void OnSceneLoad(GameScenes scene)
        {
            WindService.ForgetAll();
        }
    }
}
