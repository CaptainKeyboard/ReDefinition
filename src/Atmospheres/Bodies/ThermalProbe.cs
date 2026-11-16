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
            var session = new Session(body);
            session.Run(double.MaxValue);
            note = session.Note;
            return session.Result;
        }

        // The same table taken in slices: each Run call asks the game for samples until its time budget is spent (always at least one), so that a rebuild of the weather spreads the
        // probe over frames. Main thread, like Read. Done says that Result (null on a failure, then Note says why) is final.
        internal sealed class Session
        {
            private readonly CelestialBody body;
            private Vector3d axis, e1, e2;
            private ThermalSamples samples;
            private int index, total;

            internal Session(CelestialBody body)
            {
                this.body = body;
                Note = "";
            }

            internal bool Done { get; private set; }
            internal ThermalSamples Result { get; private set; }
            internal string Note { get; private set; }
            // The calls of Run so far.
            internal int Slices { get; private set; }

            // Takes samples for up to budgetMs; true where the table is final.
            internal bool Run(double budgetMs)
            {
                if (Done) return true;
                Slices++;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (samples == null && !Begin()) return true;
                    int nl = samples.LatitudeDeg.Length, na = samples.AltitudeM.Length;
                    do
                    {
                        int l = index % nl, a = index / nl % na, o = index / (nl * na);
                        double lon = samples.LongitudeDeg[o] * Math.PI / 180.0;
                        double lat = samples.LatitudeDeg[l] * Math.PI / 180.0;
                        Vector3d up = (axis * Math.Sin(lat) + (e1 * Math.Cos(lon) + e2 * Math.Sin(lon)) * Math.Cos(lat)).normalized;
                        double offset, emissive, albedo;
                        body.GetAtmoThermalStats(false, Planetarium.fetch.Sun, e1, Vector3d.Dot(e1, up), up, samples.AltitudeM[a], out offset, out emissive, out albedo);
                        double kelvin = body.GetFullTemperature(samples.AltitudeM[a], offset);
                        if (double.IsNaN(kelvin) || double.IsInfinity(kelvin) || kelvin < 0.0 || kelvin > 5000.0)
                        {
                            Note = "the game gave " + kelvin + " K at latitude " + samples.LatitudeDeg[l] + ", altitude " + samples.AltitudeM[a];
                            return Finish(null);
                        }
                        samples.Kelvin[index] = kelvin;
                        index++;
                        if (index == total) return Finish(samples);
                    }
                    while ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < budgetMs);
                    return false;
                }
                catch (Exception e)
                {
                    Note = "the thermal probe failed (" + e.GetType().Name + ": " + e.Message + ")";
                    return Finish(null);
                }
            }

            private bool Finish(ThermalSamples result)
            {
                Result = result;
                Done = true;
                return true;
            }

            private bool Begin()
            {
                if (body == null || !body.atmosphere || body.bodyTransform == null || Planetarium.fetch == null || Planetarium.fetch.Sun == null)
                {
                    Note = "no scene to ask the game's thermal code in";
                    return Finish(null) && false;
                }
                axis = ((Vector3d)body.bodyTransform.up).normalized;
                e1 = Vector3d.Cross(axis, Vector3d.right);
                if (e1.sqrMagnitude < 1e-6) e1 = Vector3d.Cross(axis, Vector3d.forward);
                e1 = e1.normalized;
                e2 = Vector3d.Cross(axis, e1).normalized;
                samples = new ThermalSamples();
                total = samples.LatitudeDeg.Length * samples.AltitudeM.Length * samples.LongitudeDeg.Length;
                samples.Kelvin = new double[total];
                return true;
            }
        }
    }
}
