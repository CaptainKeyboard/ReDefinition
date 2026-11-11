using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The wind providers the forecast may ask. WeatherHub keeps its provider list private, so a package that registers a provider with
    // the hub also adds it here (the wind bridges of D31); the forecast's wind then follows the same windSource order as the live wind.
    internal static class ForecastWindProviders
    {
        private static readonly object Gate = new object();
        private static IWeatherSource[] list = new IWeatherSource[0];
        private static long version;

        internal static long Version { get { return Interlocked.Read(ref version); } }

        internal static IWeatherSource[] Snapshot()
        {
            return Volatile.Read(ref list);
        }

        internal static void Add(IWeatherSource source)
        {
            if (source == null) throw new ArgumentNullException("source");
            lock (Gate)
            {
                IWeatherSource[] old = list;
                if (Array.IndexOf(old, source) >= 0) return;
                var next = new IWeatherSource[old.Length + 1];
                Array.Copy(old, next, old.Length);
                next[old.Length] = source;
                Volatile.Write(ref list, next);
                Interlocked.Increment(ref version);
            }
        }

        internal static void Remove(IWeatherSource source)
        {
            lock (Gate)
            {
                IWeatherSource[] old = list;
                int at = Array.IndexOf(old, source);
                if (at < 0) return;
                var next = new IWeatherSource[old.Length - 1];
                Array.Copy(old, 0, next, 0, at);
                Array.Copy(old, at + 1, next, at, old.Length - at - 1);
                Volatile.Write(ref list, next);
                Interlocked.Increment(ref version);
            }
        }
    }

    // The game side of the forecast service: the field of the hub, the universal time, and the context of a body built from the body's
    // climate and the player's settings. A context is rebuilt only when one of its inputs changed.
    internal sealed class KspForecastEnvironment : IForecastEnvironment
    {
        private sealed class Cached
        {
            internal BodyClimate Climate;
            internal double Horizon;
            internal float Uncertainty;
            internal WindSourceMode WindSource;
            internal long Providers;
            internal float LeadHours, AlertWind, LaunchWind, LaunchVisibility, LaunchLead;
            internal LevelChoice AlertLevel, LaunchLevel;
            internal bool AlertsOn;
            internal ForecastContext Context;
        }

        private readonly Dictionary<int, Cached> cache = new Dictionary<int, Cached>();

        public IWeatherField Field
        {
            get { return WeatherHub.Field; }
        }

        public double NowUt
        {
            // NaN while the game has no clock (the main menu, a scene that is loading): the service then serves nothing.
            get { return Planetarium.fetch != null ? Planetarium.GetUniversalTime() : double.NaN; }
        }

        public bool Enabled
        {
            get { return AtmospheresSettings.WeatherEffects && AtmospheresSettings.ForecastEnabled; }
        }

        public ForecastContext ContextFor(int bodyIndex)
        {
            List<CelestialBody> all = GameGlobals.Bodies;
            if (all == null || bodyIndex < 0 || bodyIndex >= all.Count) return null;
            BodyClimate climate = Bodies.Get(all[bodyIndex]);
            if (climate == null || !climate.Status.WeatherRuns) return null;
            double horizon = Math.Min(climate.Forecast.HorizonHours, AtmospheresSettings.ForecastHorizonHours);
            float uncertainty = AtmospheresSettings.ForecastUncertainty;
            WindSourceMode wind = AtmospheresSettings.WindSource;
            long providers = ForecastWindProviders.Version;
            float lead = AtmospheresSettings.AlertLeadHours, alertWind = AtmospheresSettings.AlertWindMs;
            float launchWind = AtmospheresSettings.LaunchMaxWindMs, launchVis = AtmospheresSettings.LaunchMinVisibilityM;
            float launchLead = AtmospheresSettings.LaunchLeadHours;
            LevelChoice alertLevel = AtmospheresSettings.AlertMinLevel, launchLevel = AtmospheresSettings.LaunchMaxLevel;
            bool alertsOn = AtmospheresSettings.AlertsEnabled;
            Cached c;
            if (cache.TryGetValue(bodyIndex, out c) && ReferenceEquals(c.Climate, climate) && c.Horizon == horizon && c.Uncertainty == uncertainty
                && c.WindSource == wind && c.Providers == providers && c.LeadHours == lead && c.AlertWind == alertWind && c.LaunchWind == launchWind
                && c.LaunchVisibility == launchVis && c.LaunchLead == launchLead && c.AlertLevel == alertLevel && c.LaunchLevel == launchLevel
                && c.AlertsOn == alertsOn)
            {
                return c.Context;
            }
            var rules = new ForecastRules(climate.Forecast.StepSeconds, horizon);
            var alerts = new AlertRules(lead, AtmospheresSettings.BandOf(alertLevel), alertWind, AtmospheresSettings.BandOf(launchLevel), launchWind,
                launchVis, launchLead);
            var context = new ForecastContext(rules, climate.Radius, ForecastContext.DefaultMapRangeKm, ForecastContext.DefaultTrackStepSeconds,
                uncertainty, wind, ForecastWindProviders.Snapshot(), alerts, alertsOn);
            cache[bodyIndex] = new Cached
            {
                Climate = climate, Horizon = horizon, Uncertainty = uncertainty, WindSource = wind, Providers = providers, LeadHours = lead,
                AlertWind = alertWind, LaunchWind = launchWind, LaunchVisibility = launchVis, LaunchLead = launchLead, AlertLevel = alertLevel,
                LaunchLevel = launchLevel, AlertsOn = alertsOn, Context = context
            };
            return context;
        }
    }

    // The forecast of the atmospheres: assigns the seam (WeatherHooks.Forecast and BodyTracks) once the field is Ready and takes it back
    // when the field is not (the line of the flight panel and the forecast window read the seam: null means "unavailable"), owns the
    // request queue, the slow tick and the alerts through ForecastService, and raises WeatherHooks.AlertRaised. Delivery of an alert
    // (a message, a warp stop, Kerbal Alarm Clock) is the forecast window's. The module's own cost on the main thread is a readiness
    // check and one unit of main-thread work per frame.
    [AtmospheresModule(70)]
    internal sealed class ForecastModule : IAtmospheresModule
    {
        private ForecastService service;
        private Func<ForecastSite, ForecastReport> forecastSeam;
        private Func<int, ForecastTrack[]> tracksSeam;
        private bool assigned;
        // Whether the module made its own service around the game (the tests give it one); only then it follows the pad and the vessel.
        private bool followsGame;
        private float sitesClock = float.MaxValue;
        private bool havePad;
        private ForecastSite padSite;

        // The site of a place for the field: the altitude is metres above sea level, what every live caller of IWeatherField.Sample passes
        // (ForecastSite.AltAgl keeps its A01 name and carries that altitude).
        internal static ForecastSite SiteAt(int bodyIndex, double latDeg, double lonDeg, double altitudeAsl, string name)
        {
            return new ForecastSite { BodyIndex = bodyIndex, LatDeg = latDeg, LonDeg = lonDeg, AltAgl = altitudeAsl, Name = name };
        }
        private const float AlertSitesSeconds = 2f;

        // The running service (null before Init); the forecast window and the alert delivery read details through it.
        internal static ForecastService Service { get; private set; }

        // The registry creates the module with this constructor.
        public ForecastModule()
        {
        }

        // A module around a given service (the tests run the seam logic without the game).
        internal ForecastModule(ForecastService service)
        {
            this.service = service;
        }

        public void Init()
        {
            if (service == null)
            {
                service = new ForecastService(new KspForecastEnvironment(), null, WeatherHooks.RaiseAlert, AtmospheresLog.WarnOnce);
                followsGame = true;
            }
            forecastSeam = service.ForecastOf;
            tracksSeam = service.BodyTracks;
            Service = service;
        }

        public void OnBodyChanged(CelestialBody oldBody, CelestialBody newBody)
        {
        }

        public void Tick(float dt)
        {
            if (service == null) return;
            service.Tick(dt);
            if (service.Available && !assigned)
            {
                WeatherHooks.Forecast = forecastSeam;
                WeatherHooks.BodyTracks = tracksSeam;
                assigned = true;
            }
            else if (!service.Available && assigned)
            {
                Release();
            }
            if (followsGame && service.Available)
            {
                sitesClock += dt;
                if (sitesClock >= AlertSitesSeconds)
                {
                    sitesClock = 0f;
                    UpdateAlertSites();
                }
            }
        }

        // The pad and the active vessel, as the setting alertSites says, are the sites the service keeps alerts for.
        private void UpdateAlertSites()
        {
            AlertSiteSpec spec = AlertSiteSpec.Parse(AtmospheresSettings.AlertSites);
            var sites = new List<ForecastSite>();
            var launch = new List<bool>();
            if (spec.Ksc)
            {
                ForecastSite fresh;
                if (TryPadSite(out fresh))
                {
                    padSite = fresh;
                    havePad = true;
                }
                if (havePad)
                {
                    sites.Add(padSite);
                    launch.Add(true);
                }
            }
            Vessel vessel = spec.Vessel && HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
            ForecastSite vesselSite;
            if (TryVesselSite(vessel, out vesselSite))
            {
                sites.Add(vesselSite);
                launch.Add(vessel.situation == Vessel.Situations.PRELAUNCH);
            }
            service.SetAutoWatched(sites, launch);
        }

        // The launch pad as a forecast site. [VERIFY K-W3a row B12 (B12Probe): SpaceCenter.Instance may be null in the flight scene (the
        // pad is then known only after a visit to the Space Centre in this session, kept in havePad); SpaceCenter.Latitude/Longitude are
        // expected in degrees and equal to the latitude/longitude of SpaceCenterTransform; the terrain altitude of the pad is about 70 m
        // above sea level.] False where there is no Space Centre.
        internal static bool TryPadSite(out ForecastSite site)
        {
            site = default(ForecastSite);
            SpaceCenter centre = SpaceCenter.Instance;
            if (centre == null || centre.cb == null) return false;
            // The altitude convention of IWeatherField.Sample: metres above sea level (CONTRACT.md, "Altitude argument").
            double altitudeAsl = centre.cb.TerrainAltitude(centre.Latitude, centre.Longitude, true);
            site = SiteAt(centre.cb.flightGlobalsIndex, centre.Latitude, centre.Longitude, altitudeAsl, "Space Centre");
            return true;
        }

        // The active vessel as a forecast site while it is in an atmosphere and on the ground, on the water or flying below its depth.
        // [VERIFY K-W3a row B12 (B12Probe): vessel.altitude is the altitude above sea level, the quantity the live callers read with
        // body.GetAltitude(position).]
        internal static bool TryVesselSite(Vessel vessel, out ForecastSite site)
        {
            site = default(ForecastSite);
            if (vessel == null || vessel.mainBody == null || !vessel.mainBody.atmosphere || vessel.altitude >= vessel.mainBody.atmosphereDepth) return false;
            if (vessel.situation != Vessel.Situations.LANDED && vessel.situation != Vessel.Situations.SPLASHED
                && vessel.situation != Vessel.Situations.PRELAUNCH && vessel.situation != Vessel.Situations.FLYING) return false;
            site = SiteAt(vessel.mainBody.flightGlobalsIndex, vessel.latitude, vessel.longitude, vessel.altitude, vessel.vesselName);
            return true;
        }

        public void Shutdown()
        {
            Release();
            if (ReferenceEquals(Service, service)) Service = null;
            service = null;
        }

        private void Release()
        {
            // Whoever assigned last wins: the seam is taken back only if it is still ours.
            if (WeatherHooks.Forecast == forecastSeam) WeatherHooks.Forecast = null;
            if (WeatherHooks.BodyTracks == tracksSeam) WeatherHooks.BodyTracks = null;
            assigned = false;
        }
    }
}
