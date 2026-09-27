using System.Collections.Generic;
using System;

namespace ReDefinition.Engineer
{
    // The situation the engineer's numbers are for: KSP's delta-v app's own
    // (DeltaVGlobals.DeltaVAppValues), a body and sea level, an altitude or
    // vacuum. KSP's staging stack follows it. KSP's delta-v app reads it only as
    // it opens (DeltaVAppSituation.Awake, decompiled): while it stands open it
    // keeps showing its own, and a change made in it writes all of its own back.
    internal static class EngineerSituation
    {
        private static DeltaVAppValues AppValues()
        {
            return DeltaVGlobals.DeltaVAppValues;
        }

        internal static CelestialBody Body()
        {
            DeltaVAppValues values = AppValues();
            CelestialBody body = values != null ? values.body : null;
            return body != null ? body : FlightGlobals.GetHomeBody();
        }

        internal static string BodyName()
        {
            CelestialBody body = Body();
            return body != null ? body.displayName.LocalizeRemoveGender() : "";
        }

        internal static bool HasAtmosphere()
        {
            CelestialBody body = Body();
            return body != null && body.atmosphere;
        }

        internal static DeltaVSituationOptions Situation()
        {
            DeltaVAppValues values = AppValues();
            if (values == null || !HasAtmosphere()) return DeltaVSituationOptions.Vaccum;
            return values.situation;
        }

        internal static double Altitude()
        {
            DeltaVAppValues values = AppValues();
            return values != null ? values.altitude : 0.0;
        }

        // The top of the body's atmosphere, in metres.
        internal static double MaxAltitude()
        {
            CelestialBody body = Body();
            return body != null && body.atmosphere ? body.atmosphereDepth : 0.0;
        }

        // The list of bodies, the star, each planet and its moons.
        internal static void PickBody()
        {
            PlannerBodies bodies = PlannerBodies.Read();
            List<CelestialBody> all = FlightGlobals.Bodies;
            if (all == null) return;
            BodyPicker.Show(typeof(EngineerSituation), bodies, all.IndexOf(Body()), SetBody);
        }

        private static void SetBody(int index)
        {
            List<CelestialBody> all = FlightGlobals.Bodies;
            DeltaVAppValues values = AppValues();
            if (all == null || values == null || index < 0 || index >= all.Count) return;
            CelestialBody body = all[index];
            values.body = body;
            if (!body.atmosphere) values.situation = DeltaVSituationOptions.Vaccum;
            values.altitude = Math.Min(values.altitude, body.atmosphere ? body.atmosphereDepth : 0.0);
            Recalculate();
        }

        internal static void SetSituation(DeltaVSituationOptions situation)
        {
            DeltaVAppValues values = AppValues();
            if (values == null || values.situation == situation) return;
            values.situation = situation;
            Recalculate();
        }

        internal static void SetAltitude(double metres)
        {
            DeltaVAppValues values = AppValues();
            if (values == null) return;
            double clamped = Math.Max(0.0, Math.Min(metres, MaxAltitude()));
            if (Math.Abs(values.altitude - clamped) < 1.0) return;
            values.altitude = clamped;
            Recalculate();
        }

        // As KSP's delta-v app does after a change of situation.
        private static void Recalculate()
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship != null && ship.vesselDeltaV != null) ship.vesselDeltaV.SetCalcsDirty(true);
        }
    }
}
