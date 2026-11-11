using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // The forecast interface's part of the module loop: delivers the alerts the forecast raises, keeps the Kerbal Alarm Clock alarms in line
    // with the forecast, and makes the map overlay's object. The engine and the publication of the seam are ForecastModule's; the window,
    // the overlay and the launcher are their own objects. Cost outside an event: a queue check and a clock.
    [AtmospheresModule(75)]
    internal sealed class ForecastUiModule : IAtmospheresModule
    {
        private const float KacSeconds = 2f;
        private AlertDelivery delivery;
        private KacBridge kac;
        private float kacClock = KacSeconds;
        private bool subscribed;
        private readonly Action<ForecastAlert> receive;
        private readonly ForecastKey key = new ForecastKey();
        private ForecastSite guardSite;
        private double guardEta = double.NaN;

        // The running delivery and bridge (the probe and the window read them); null before Init.
        internal static AlertDelivery Delivery { get; private set; }
        internal static KacBridge Kac { get; private set; }

        public ForecastUiModule()
        {
            receive = a =>
            {
                if (delivery != null) delivery.Receive(a);
            };
        }

        public void Init()
        {
            if (delivery == null)
            {
                delivery = new AlertDelivery(new KspAlertSink(), ForecastUiSettings.Delivery, () =>
                {
                    ForecastService service = ForecastModule.Service;
                    return service != null && service.Available;
                });
                kac = new KacBridge(new KacReflection(AtmospheresLog.Say));
            }
            Delivery = delivery;
            Kac = kac;
            if (!subscribed)
            {
                WeatherHooks.AlertRaised += receive;
                subscribed = true;
            }
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (delivery == null || Planetarium.fetch == null) return;
            double now = Planetarium.GetUniversalTime();
            delivery.Flush(now);
            if (HighLogic.UISkin != null) key.Poll(ForecastWindow.Instance.Toggle);
            GuardWarp(now);
            EnsureOverlay();
            kacClock += dt;
            if (kacClock >= KacSeconds)
            {
                kacClock = 0f;
                RefreshWarpGuard();
                SyncKac(now);
            }
        }

        // The active vessel's storm, as the engine's last run has it: refreshed with the alarms (an arrival time is absolute, so it stays
        // right between refreshes), read every frame by the warp guard.
        private void RefreshWarpGuard()
        {
            guardEta = double.NaN;
            ForecastService service = ForecastModule.Service;
            if (!ForecastUiSettings.Delivery().WarpStop || service == null || !service.Available) return;
            ForecastSite site;
            if (!ForecastSites.TryLocate(SiteChoice.Vessel, out site)) return;
            ForecastResult detail = service.DetailOf(site);
            if (detail == null || detail.ActiveAtStart) return;
            guardSite = site;
            guardEta = detail.ThresholdEtaUt;
        }

        // Stops a running warp before it reaches the time the Approaching alert is stamped at (R3 of the D30 review).
        private void GuardWarp(double now)
        {
            if (double.IsNaN(guardEta)) return;
            delivery.GuardWarp(now, guardSite, guardEta, AtmospheresSettings.AlertLeadHours);
        }

        // The overlay's object exists once the scenes that show it are reached and KSP's skin is there.
        private static void EnsureOverlay()
        {
            if (MapOverlay.Instance != null || HighLogic.UISkin == null) return;
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION || HighLogic.LoadedSceneIsFlight) MapOverlay.Ensure();
        }

        // The sites the player's alert setting follows, and each one's storm ETA from the engine's last run.
        private void SyncKac(double now)
        {
            ForecastService service = ForecastModule.Service;
            bool on = ForecastUiSettings.Kac && service != null && service.Available;
            var sites = new List<ForecastSite>();
            var keys = new List<ulong>();
            var etas = new List<double>();
            if (on)
            {
                AlertSiteSpec spec = AlertSiteSpec.Parse(AtmospheresSettings.AlertSites);
                var choices = new List<SiteChoice>();
                if (spec.Ksc) choices.Add(SiteChoice.Ksc);
                if (spec.Vessel) choices.Add(SiteChoice.Vessel);
                for (int i = 0; i < choices.Count; i++)
                {
                    ForecastSite site;
                    if (!ForecastSites.TryLocate(choices[i], out site)) continue;
                    ForecastResult detail = service.DetailOf(site);
                    double eta = detail != null && !detail.ActiveAtStart ? detail.ThresholdEtaUt : double.NaN;
                    sites.Add(site);
                    keys.Add(KacBridge.PlaceKey((int)choices[i], site.BodyIndex));
                    etas.Add(eta);
                }
            }
            kac.Sync(on, WeatherHooks.ForecastGeneration, now, AtmospheresSettings.AlertLeadHours, ForecastUiSettings.KacActionSetting, sites, keys, etas);
        }

        public void Shutdown()
        {
            if (subscribed)
            {
                WeatherHooks.AlertRaised -= receive;
                subscribed = false;
            }
            if (ReferenceEquals(Delivery, delivery)) Delivery = null;
            if (ReferenceEquals(Kac, kac)) Kac = null;
        }
    }
}
