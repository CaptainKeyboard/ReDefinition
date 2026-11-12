using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // The probe of package B12 inside the integrated series K-W3a (the section "Forecast sites"): the two branches of the forecast
    // module that only the running game can answer. The pad site (SpaceCenter.Instance in the flight scene, the unit of
    // SpaceCenter.Latitude/Longitude, the altitude above sea level) and the vessel site (vessel.altitude against the altitude the live
    // callers read with body.GetAltitude(position)). The engine itself is proven offline against the real GeneratorField.
    [AtmospheresModule(986)]
    internal sealed class B12Probe : SeriesProbe
    {
        protected override string SeriesName
        {
            get { return "K-W3a"; }
        }

        protected override double MaxSeconds
        {
            get { return 6 * 60; }
        }

        // The altitude above sea level that the vessel site must carry: the terrain under the vessel plus the height above it (a height the
        // game has not measured, negative or NaN, counts as zero).
        internal static double VesselAltitudeReference(double terrainAsl, double heightFromTerrain)
        {
            double h = heightFromTerrain < 0.0 || double.IsNaN(heightFromTerrain) ? 0.0 : heightFromTerrain;
            return terrainAsl + h;
        }

        protected override IEnumerator<ProbeWait> Scenario()
        {
            yield return ProbeWait.Until(() => HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null,
                330, "the flight");
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                Fail("B12: no flight to measure in");
                yield break;
            }
            // Let the first frames of the flight pass so the Space Centre object and the vessel are settled.
            yield return ProbeWait.Seconds(5);

            ForecastSite pad;
            bool havePad = ForecastModule.TryPadSite(out pad);
            Emit("B12.pad_site_present", havePad);
            if (havePad)
            {
                SpaceCenter centre = SpaceCenter.Instance;
                CelestialBody body = centre.cb;
                Vector3d at = centre.SpaceCenterTransform.position;
                double latError = Math.Abs(pad.LatDeg - body.GetLatitude(at));
                double lonError = Math.Abs(pad.LonDeg - body.GetLongitude(at));
                if (lonError > 180.0) lonError = 360.0 - lonError;
                Emit("B12.pad_lat_error_deg", latError);
                Emit("B12.pad_lon_error_deg", lonError);
                Emit("B12.pad_altitude_asl_m", pad.AltAgl);
                Emit("B12.pad_altitude_error_m", Math.Abs(pad.AltAgl - body.GetAltitude(at)));
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            ForecastSite site;
            bool haveVessel = ForecastModule.TryVesselSite(vessel, out site);
            Emit("B12.vessel_site_present", haveVessel);
            if (haveVessel)
            {
                // K-W3a: the error was 600075 m, the body's radius plus the altitude, with body.GetAltitude(vessel.GetWorldPos3D()) as the
                // reference: that call returned minus the radius, so the position it was given was the body's centre (the vessel's CoMD is
                // not a world position this early in the flight). The reference is now the terrain altitude under the vessel plus its height
                // above the terrain, and the other readings are logged beside it.
                double terrain = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude, true);
                Emit("B12.vessel_altitude_error_m", Math.Abs(site.AltAgl - VesselAltitudeReference(terrain, vessel.heightFromTerrain)));
                Emit("B12.vessel_altitude_site_m", site.AltAgl);
                Emit("B12.vessel_altitude_terrain_m", terrain);
                Emit("B12.vessel_height_from_terrain_m", vessel.heightFromTerrain);
                Emit("B12.vessel_get_altitude_comd_m", vessel.mainBody.GetAltitude(vessel.GetWorldPos3D()));
                Emit("B12.vessel_get_altitude_transform_m", vessel.mainBody.GetAltitude(vessel.vesselTransform.position));
                Emit("B12.vessel_site_body_ok", site.BodyIndex == vessel.mainBody.flightGlobalsIndex);
            }
        }
    }
}
