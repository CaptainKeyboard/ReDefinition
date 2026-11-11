using System;

namespace ReDefinition.Atmospheres
{
    // Samples the air temperature of a body the way the game's thermal code computes it: GetFullTemperature(altitude, offset) with the
    // offset of GetAtmoThermalStats (never GetFullTemperature(Vector3d), whose sun vector is unnormalised: RECON P1). The call takes the
    // geometry as arguments (the body's axis comes from its transform, the point and the sun from the vectors passed), so a point on the
    // body at a chosen latitude and sun hour angle needs no time travel. The sun stands in the equatorial plane (the game's bodies have no
    // tilt; the sun's declination is zero), the four longitudes are four hour angles of one day. Main thread, inside a scene that has a
    // Planetarium. [VERIFY in the first series: the table of the shipped bodies against tests/data/recon/dumps/run2-temperature-*.json.]
    internal static class ThermalProbe
    {
        // The table, or null where the game could not be asked or gave a value that is not finite.
        internal static ThermalSamples Read(CelestialBody body, out string note)
        {
            note = "";
            try
            {
                if (body == null || !body.atmosphere || body.bodyTransform == null || Planetarium.fetch == null || Planetarium.fetch.Sun == null)
                {
                    note = "no scene to ask the game's thermal code in";
                    return null;
                }
                Vector3d axis = ((Vector3d)body.bodyTransform.up).normalized;
                Vector3d e1 = Vector3d.Cross(axis, Vector3d.right);
                if (e1.sqrMagnitude < 1e-6) e1 = Vector3d.Cross(axis, Vector3d.forward);
                e1 = e1.normalized;
                Vector3d e2 = Vector3d.Cross(axis, e1).normalized;

                var s = new ThermalSamples();
                int nl = s.LatitudeDeg.Length, na = s.AltitudeM.Length, no = s.LongitudeDeg.Length;
                s.Kelvin = new double[no * na * nl];
                for (int o = 0; o < no; o++)
                {
                    double lon = s.LongitudeDeg[o] * Math.PI / 180.0;
                    for (int a = 0; a < na; a++)
                    {
                        for (int l = 0; l < nl; l++)
                        {
                            double lat = s.LatitudeDeg[l] * Math.PI / 180.0;
                            Vector3d up = (axis * Math.Sin(lat) + (e1 * Math.Cos(lon) + e2 * Math.Sin(lon)) * Math.Cos(lat)).normalized;
                            double offset, emissive, albedo;
                            body.GetAtmoThermalStats(false, Planetarium.fetch.Sun, e1, Vector3d.Dot(e1, up), up, s.AltitudeM[a], out offset, out emissive, out albedo);
                            double kelvin = body.GetFullTemperature(s.AltitudeM[a], offset);
                            if (double.IsNaN(kelvin) || double.IsInfinity(kelvin) || kelvin < 0.0 || kelvin > 5000.0)
                            {
                                note = "the game gave " + kelvin + " K at latitude " + s.LatitudeDeg[l] + ", altitude " + s.AltitudeM[a];
                                return null;
                            }
                            s.Kelvin[(o * na + a) * nl + l] = kelvin;
                        }
                    }
                }
                return s;
            }
            catch (Exception e)
            {
                note = "the thermal probe failed (" + e.GetType().Name + ": " + e.Message + ")";
                return null;
            }
        }
    }
}
