using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // The game side of the alert delivery: stock screen messages, the stock message app and the stock time warp. No sounds or icons of our
    // own; the message app uses its own icons and colours.
    internal sealed class KspAlertSink : IAlertSink
    {
        public void ScreenMessage(string text, float seconds)
        {
            ScreenMessages.PostScreenMessage(text, seconds, ScreenMessageStyle.UPPER_CENTER);
        }

        // MessageSystem.Instance.AddMessage(new MessageSystem.Message(title, text, colour, icon)) [VERIFY in the running game: decompiled
        // from Assembly-CSharp 1.12; the instance is absent in the main menu].
        public void MessageApp(string title, string text, AlertKind kind)
        {
            MessageSystem system = MessageSystem.Instance;
            if (system == null) return;
            MessageSystemButton.MessageButtonColor color;
            MessageSystemButton.ButtonIcons icon;
            switch (kind)
            {
                case AlertKind.Worsening:
                    color = MessageSystemButton.MessageButtonColor.RED;
                    icon = MessageSystemButton.ButtonIcons.ALERT;
                    break;
                case AlertKind.Arrived:
                    color = MessageSystemButton.MessageButtonColor.ORANGE;
                    icon = MessageSystemButton.ButtonIcons.ALERT;
                    break;
                case AlertKind.Approaching:
                    color = MessageSystemButton.MessageButtonColor.YELLOW;
                    icon = MessageSystemButton.ButtonIcons.DEADLINE;
                    break;
                case AlertKind.Clearing:
                    color = MessageSystemButton.MessageButtonColor.GREEN;
                    icon = MessageSystemButton.ButtonIcons.COMPLETE;
                    break;
                default:
                    color = MessageSystemButton.MessageButtonColor.BLUE;
                    icon = MessageSystemButton.ButtonIcons.MESSAGE;
                    break;
            }
            system.AddMessage(new MessageSystem.Message(title, text, color, icon));
        }

        // TimeWarp.SetRate(0, true) [VERIFY: decompiled; instant, with the stock "warp stopped" screen message].
        public void StopWarp()
        {
            if (TimeWarp.fetch == null || TimeWarp.CurrentRateIndex <= 0) return;
            TimeWarp.SetRate(0, true);
        }

        public bool WarpRunning
        {
            get { return TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0; }
        }

        public double WarpRate
        {
            get { return TimeWarp.fetch != null ? TimeWarp.CurrentRate : 1.0; }
        }

        // The active vessel's place is the site when it is on the same body within about 0.2 degrees.
        public bool IsActiveVesselSite(ForecastSite site)
        {
            Vessel vessel = HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.flightGlobalsIndex != site.BodyIndex) return false;
            return Math.Abs(vessel.latitude - site.LatDeg) < 0.2 && Math.Abs(ForecastMath.WrapLon(vessel.longitude - site.LonDeg)) < 0.2;
        }
    }

    // Where the player can ask for a forecast.
    internal enum SiteChoice
    {
        Ksc = 0,
        LaunchPad = 1,
        Runway = 2,
        Vessel = 3,
        Pinned = 4
    }

    // A latitude and longitude the player pinned, on a body.
    internal sealed class PinnedSite
    {
        internal bool Set;
        internal int BodyIndex;
        internal double LatDeg;
        internal double LonDeg;
    }

    // The sites of the game: the Space Centre, the pad, the runway, the active vessel and a pinned place, as forecast sites. Reads no
    // FlightGlobals outside the flight (GameGlobals); where the Space Centre is not loaded (the flight scene) the last place seen stands.
    internal static class ForecastSites
    {
        internal static readonly PinnedSite Pinned = new PinnedSite();
        private static bool haveKsc;
        private static int kscBody;
        private static double kscLat, kscLon, kscAlt;

        internal static string NameOf(SiteChoice choice)
        {
            switch (choice)
            {
                case SiteChoice.Ksc: return ForecastStrings.SiteKsc;
                case SiteChoice.LaunchPad: return ForecastStrings.SiteLaunchPad;
                case SiteChoice.Runway: return ForecastStrings.SiteRunway;
                case SiteChoice.Vessel: return ForecastStrings.SiteVessel;
                default: return ForecastStrings.SitePinned;
            }
        }

        // The Space Centre's place, remembered while it is loaded.
        private static bool TryKsc(out ForecastSite site)
        {
            site = new ForecastSite { Name = ForecastStrings.SiteKsc };
            SpaceCenter centre = SpaceCenter.Instance;
            if (centre != null && centre.cb != null)
            {
                haveKsc = true;
                kscBody = centre.cb.flightGlobalsIndex;
                kscLat = centre.Latitude;
                kscLon = centre.Longitude;
                // Metres above sea level, the altitude convention of IWeatherField.Sample (CONTRACT.md, "Altitude argument").
                kscAlt = centre.cb.TerrainAltitude(kscLat, kscLon, true);
            }
            if (!haveKsc) return false;
            site.BodyIndex = kscBody;
            site.LatDeg = kscLat;
            site.LonDeg = kscLon;
            site.AltAgl = kscAlt;
            return true;
        }

        // A launch site's first spawn point [VERIFY: LaunchSite.spawnPoints and GetSpawnPointLatLonAlt, decompiled from 1.12].
        private static bool TryLaunchSite(string name, string display, out ForecastSite site)
        {
            site = new ForecastSite { Name = display };
            try
            {
                if (PSystemSetup.Instance == null) return false;
                LaunchSite launch = PSystemSetup.Instance.GetLaunchSite(name);
                if (launch == null || launch.spawnPoints == null || launch.spawnPoints.Length == 0 || launch.Body == null) return false;
                double lat, lon, alt;
                launch.spawnPoints[0].GetSpawnPointLatLonAlt(out lat, out lon, out alt);
                // Outside the Space Centre's scene a spawn point whose place is not set has no transform to read it from.
                if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
                site.BodyIndex = launch.Body.flightGlobalsIndex;
                site.LatDeg = lat;
                site.LonDeg = lon;
                // Terrain altitude above sea level, as the forecast module gives the pad.
                site.AltAgl = launch.Body.TerrainAltitude(lat, lon, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The forecast site of a choice; false where the game cannot say (no vessel, nothing pinned).
        internal static bool TryLocate(SiteChoice choice, out ForecastSite site)
        {
            site = new ForecastSite { Name = NameOf(choice) };
            switch (choice)
            {
                case SiteChoice.Ksc:
                    return TryKsc(out site);
                case SiteChoice.LaunchPad:
                    return TryLaunchSite("LaunchPad", ForecastStrings.SiteLaunchPad, out site) || TryKsc(out site);
                case SiteChoice.Runway:
                    return TryLaunchSite("Runway", ForecastStrings.SiteRunway, out site) || TryKsc(out site);
                case SiteChoice.Vessel:
                {
                    Vessel vessel = HighLogic.LoadedSceneIsFlight ? GameGlobals.ActiveVessel : null;
                    if (vessel == null || vessel.mainBody == null) return false;
                    site = new ForecastSite
                    {
                        BodyIndex = vessel.mainBody.flightGlobalsIndex, LatDeg = vessel.latitude, LonDeg = vessel.longitude, AltAgl = vessel.altitude, Name = vessel.vesselName
                    };
                    return true;
                }
                default:
                    if (!Pinned.Set) return false;
                    site = new ForecastSite { BodyIndex = Pinned.BodyIndex, LatDeg = Pinned.LatDeg, LonDeg = Pinned.LonDeg, Name = ForecastStrings.SitePinned };
                    return true;
            }
        }

        // Whether a choice is a launch site (the launch conditions apply).
        internal static bool IsLaunchSite(SiteChoice choice)
        {
            return choice == SiteChoice.Ksc || choice == SiteChoice.LaunchPad || choice == SiteChoice.Runway;
        }

        // The bodies the interface can show: those where weather runs.
        internal static List<CelestialBody> WeatherBodies()
        {
            var list = new List<CelestialBody>();
            List<CelestialBody> all = GameGlobals.Bodies;
            if (all == null) return list;
            for (int i = 0; i < all.Count; i++)
            {
                BodyClimate climate = Bodies.Get(all[i]);
                if (climate != null && climate.Status.WeatherRuns) list.Add(all[i]);
            }
            return list;
        }

        internal static string DateText(double ut)
        {
            return KSPUtil.PrintDateCompact(ut, true);
        }
    }

    // The map camera's view of a body: world position, scaled space, screen. A place on the far side of the body or behind the camera does
    // not project. The overlay is screen space (a canvas), so nothing of it enters the scene's depth, motion vectors or the upscaler's
    // history [VERIFY: PlanetariumCamera.Camera and ScaledSpace.LocalToScaledSpace, decompiled from 1.12].
    internal sealed class KspMapProjector : IMapProjector
    {
        public bool Project(int bodyIndex, double latDeg, double lonDeg, out float x, out float y)
        {
            x = y = 0f;
            List<CelestialBody> bodies = GameGlobals.Bodies;
            if (bodies == null || bodyIndex < 0 || bodyIndex >= bodies.Count) return false;
            CelestialBody body = bodies[bodyIndex];
            Camera camera = PlanetariumCamera.Camera;
            if (body == null || camera == null || body.scaledBody == null) return false;
            Vector3d world = body.GetWorldSurfacePosition(latDeg, lonDeg, 0.0);
            Vector3d scaled = ScaledSpace.LocalToScaledSpace(world);
            Vector3 centre = body.scaledBody.transform.position;
            Vector3 cam = camera.transform.position;
            double radius = body.Radius * ScaledSpace.InverseScaleFactor;
            if (!OverlayBuilder.FacesCamera(scaled.x - centre.x, scaled.y - centre.y, scaled.z - centre.z, cam.x - centre.x, cam.y - centre.y, cam.z - centre.z,
                    radius * 0.999)) return false;
            Vector3 screen = camera.WorldToScreenPoint((Vector3)scaled);
            if (screen.z <= 0f) return false;
            x = screen.x;
            y = screen.y;
            return true;
        }
    }
}
