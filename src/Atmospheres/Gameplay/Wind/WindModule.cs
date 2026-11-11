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
            get { return WindService.Plan.Status + (WindHook.Disabled ? " The wind hook is switched off: " + WindHook.Status + "." : ""); }
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
            if (!on)
            {
                // Off costs one reference comparison: the plan object is made once.
                if (!ReferenceEquals(WindService.Plan, OffPlan))
                {
                    SetPlan(OffPlan);
                    SyncSources(false);
                    announced = false;
                    keyValid = false;
                }
                return;
            }
            if (!AtmospheresSettings.WindForces)
            {
                // No wind forces: nothing pushes a vehicle, and the pictures still follow the foreign wind that is applied (the source
                // stays in the weather seam).
                if (!ReferenceEquals(WindService.Plan, OffPlan))
                {
                    SetPlan(OffPlan);
                    announced = false;
                    keyValid = false;
                }
                if (!scanned) Scan();
                SyncSources(true);
                return;
            }
            if (!scanned) Scan();
            WindSourceMode mode = AtmospheresSettings.WindSource;
            WindInjection injection = AtmospheresSettings.WindInjection;
            ForeignWinds known = ForeignOverride ?? foreign;
            bool integrator = WindHook.IntegratorPresent;

            // The plan is worked out again only when one of its inputs changed: the settings, the mods found, the integrator and the slot.
            if (!keyValid || mode != keyMode || injection != keyInjection || !ReferenceEquals(known, keyForeign) || integrator != keyIntegrator
                || WindHook.Registered != keyRegistered || windApi.Connected != keyBridge)
            {
                // The route the settings ask for; the override is registered only where the plan wants it, so that a mod that holds the
                // slot keeps it and the wind then goes in as a force.
                bool mfi = WindHook.Registered;
                bool bridge = windApi.Connected;
                WindPlan wish = WindPlanner.Decide(mode, known, injection, integrator, true, bridge);
                if (wish.Route == WindRoute.Aero && !WindHook.Registered)
                {
                    mfi = WindHook.Register();
                    if (!mfi) AtmospheresLog.WarnOnce("wind: the ModularFlightIntegrator override was not registered (" + WindHook.Status + "); the wind goes in as a force.");
                }
                WindPlan plan = WindPlanner.Decide(mode, known, injection, mfi, true, bridge);
                if (plan.Route == WindRoute.Far)
                {
                    // The plan's route Far is served by our provider in WindAPI (registered below, once its manager exists).
                    WindApiCallbacks.OurWind = PartWind;
                    WindService.OthersWindMs = OthersWind;
                }
                keyBridge = bridge;
                keyValid = true;
                keyMode = mode;
                keyInjection = injection;
                keyForeign = known;
                keyIntegrator = integrator;
                keyRegistered = WindHook.Registered;
                SetPlan(plan);
                announced = false;
            }
            // The manager of WindAPI is made for each flight: our provider goes in again whenever a new one appears.
            if (WindService.Plan.Route == WindRoute.Far && windApi.NeedsRegistration && windApi.Register()) windApiRegistered = true;
            SyncSources(true);
            Announce();
            LogReadout(dt);
        }

        // The state the plan was last worked out from.
        private bool keyBridge;
        private bool keyValid;
        private WindSourceMode keyMode;
        private WindInjection keyInjection;
        private ForeignWinds keyForeign;
        private bool keyIntegrator, keyRegistered;
        private float readoutTimer;

        private static readonly WindPlan OffPlan = new WindPlan { Route = WindRoute.Off, Status = WindStatus.Text(WindSourceMode.Off, null) };

        private double OthersWind(CelestialBody body, Vector3 worldPosition)
        {
            return windApi.Connected ? windApi.OthersAt(body, worldPosition).magnitude : 0.0;
        }

        // The wind readout of the log: one line every 30 seconds of a flight while a wind acts on the active vessel.
        private void LogReadout(float dt)
        {
            readoutTimer += dt;
            if (readoutTimer < 30f) return;
            readoutTimer = 0f;
            Vessel v = GameGlobals.ActiveVessel;
            if (v == null || !HighLogic.LoadedSceneIsFlight || !WindService.Plan.AppliesAnything) return;
            Vec3 w = WindService.StateOf(v).Applied;
            if (w.Length < 0.1) return;
            AtmospheresLog.Say("wind at " + v.vesselName + ": " + w.Length.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s ("
                               + WindService.Plan.OwnerLine() + ")");
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
            // The source object outlives a disconnect, so that a source already registered can still be taken out.
            IWeatherSource held = windApi.Source;
            IWeatherSource source = windApi.Connected ? held : null;
            WindSourceMode mode = AtmospheresSettings.WindSource;
            bool wantHub = active && source != null && (mode == WindSourceMode.Auto || mode == WindSourceMode.Foreign);
            if (wantHub && !sourceInHub)
            {
                WeatherHub.Register(source);
                sourceInHub = true;
            }
            else if (!wantHub && sourceInHub)
            {
                WeatherHub.Unregister(held);
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
                ForecastWindProviders.Remove(held);
                sourceInForecast = false;
            }
        }

        // The wind our WindAPI provider returns for a part. FAR does not fill part.dragScalar [VERIFY with FAR installed], so on this route the drag area behind the
        // acceleration clamp stays 0 and the clamp is inert; the slew limit, the speed cap and the ramp hold.
        private static Vector3 PartWind(Part part)
        {
            Vessel v = part != null ? part.vessel : null;
            if (v == null || !WindService.Eligible(v) || WindService.Plan.Route != WindRoute.Far) return Vector3.zero;
            Vec3 w = WindService.WindFor(v);
            WindService.StateOf(v).AddPart(part.dragScalar, part.dynamicPressurekPa);
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
            // A hook that shut itself off after failures gets a new chance in the next scene.
            WindHook.ResetFailures();
        }
    }
}
