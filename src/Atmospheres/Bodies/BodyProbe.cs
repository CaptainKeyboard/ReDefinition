using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // Reads what the game says about a body, once, on the main thread, into BodyFacts. Every read is guarded: a member that cannot be
    // read keeps its "unknown" value and a note says so. Members used (all of CelestialBody in the installed Assembly-CSharp): atmosphere,
    // atmosphereDepth, atmospherePressureSeaLevel, hasSolidSurface, ocean, isStar, rotationPeriod, tidallyLocked, Radius, orbit,
    // referenceBody, RotationAxis, GetTemperature. The orientation of RotationAxis and the orbit normal is [VERIFY: the first log line of
    // a body with a tilting add-on shows the angles the probe read].
    internal static class BodyProbe
    {
        // The altitudes (as shares of the atmosphere depth) at which the air temperature is sampled for the target curve.
        private static readonly double[] SampleShares = { 0.0, 0.02, 0.05, 0.1, 0.2, 0.35, 0.5, 0.7, 1.0 };

        internal static BodyFacts Read(CelestialBody body, IEnumerable<string> eveFieldNames, List<string> notes)
        {
            var facts = new BodyFacts();
            if (body == null) return facts;
            facts.Name = body.name ?? "";
            facts.Index = body.flightGlobalsIndex;
            facts.RadiusM = body.Radius;
            facts.DaySeconds = body.rotationPeriod;
            facts.IsStar = body.isStar;
            facts.HasAtmosphere = body.atmosphere;
            facts.AtmosphereDepthM = body.atmosphereDepth;
            facts.SeaLevelPressureKPa = body.atmospherePressureSeaLevel;
            facts.HasSurface = body.hasSolidSurface;
            facts.HasOcean = body.ocean;
            facts.TidallyLocked = body.tidallyLocked;
            Guard(notes, "orbit", () => ReadOrbit(body, facts));
            Guard(notes, "home body", () => facts.HomeOrbitPeriod = HomePeriod());
            if (facts.HasAtmosphere) Guard(notes, "temperature", () => ReadTemperature(body, facts));
            Guard(notes, "axis", () => ReadAxis(body, facts));
            if (eveFieldNames != null) facts.EveFields = MatchingFields(eveFieldNames, facts.Name);
            return facts;
        }

        // The particle fields of a body: those whose name ends in a dash and the body's name ("snow-Laythe" for Laythe).
        internal static List<string> MatchingFields(IEnumerable<string> names, string bodyName)
        {
            var result = new List<string>();
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int dash = name.IndexOf('-');
                if (dash > 0 && dash < name.Length - 1 && string.Equals(name.Substring(dash + 1), bodyName, StringComparison.OrdinalIgnoreCase)) result.Add(name);
            }
            return result;
        }

        private static void ReadOrbit(CelestialBody body, BodyFacts facts)
        {
            if (body.orbit == null || body.referenceBody == null || body.referenceBody == body) return;
            facts.OrbitPeriod = body.orbit.period;
            if (body.referenceBody.isStar) return;
            // A moon: walk up to the planet whose parent is the star.
            facts.IsMoon = true;
            CelestialBody planet = body.referenceBody;
            for (int i = 0; i < 16 && planet.referenceBody != null && planet.referenceBody != planet && !planet.referenceBody.isStar; i++) planet = planet.referenceBody;
            if (planet.orbit != null) facts.ParentOrbitPeriod = planet.orbit.period;
        }

        private static double HomePeriod()
        {
            CelestialBody home = FlightGlobals.fetch == null ? null : FlightGlobals.GetHomeBody();
            return home == null || home.orbit == null ? 0.0 : home.orbit.period;
        }

        private static void ReadTemperature(CelestialBody body, BodyFacts facts)
        {
            double depth = body.atmosphereDepth;
            if (!(depth > 0)) return;
            var xs = new List<float>();
            var ys = new List<float>();
            foreach (double share in SampleShares)
            {
                double altitude = depth * share;
                double kelvin = body.GetTemperature(altitude);
                if (double.IsNaN(kelvin) || double.IsInfinity(kelvin) || kelvin < 0) continue;
                if (xs.Count > 0 && !((float)altitude > xs[xs.Count - 1])) continue;
                xs.Add((float)altitude);
                ys.Add((float)kelvin);
            }
            if (xs.Count == 0) return;
            facts.TemperatureTarget = new Curve(xs.ToArray(), ys.ToArray());
            facts.SeaLevelTemperatureK = ys[0];
        }

        private static void ReadAxis(CelestialBody body, BodyFacts facts)
        {
            Vector3d axis = body.RotationAxis;
            if (axis.sqrMagnitude < 1e-12) return;
            facts.AxisFromReferenceDeg = Vector3d.Angle(axis, Vector3d.up);
            CelestialBody planet = body;
            for (int i = 0; i < 16 && planet.referenceBody != null && planet.referenceBody != planet && !planet.referenceBody.isStar; i++) planet = planet.referenceBody;
            if (planet.orbit == null || planet.referenceBody == null || planet.referenceBody == planet) return;
            Vector3d normal = planet.orbit.GetOrbitNormal().xzy;
            if (normal.sqrMagnitude < 1e-12) return;
            facts.ObliquityDeg = Vector3d.Angle(axis, normal);
        }

        private static void Guard(List<string> notes, string what, Action read)
        {
            try
            {
                read();
            }
            catch (Exception e)
            {
                if (notes != null) notes.Add("the probe could not read " + what + " (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }
    }
}
