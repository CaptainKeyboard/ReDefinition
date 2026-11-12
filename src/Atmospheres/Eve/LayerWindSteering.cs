using System;
using AtmoSim.Core;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The E43 switches: the settings windShear, layerWindSteering (LayerSteeringMode, in AtmospheresSettings.Enums.cs), verticalMotion and anvilTilt.
    // A probe or a test pins a value through the Override field of the same name (null = the setting decides); Couple is the consistency rule
    // between the layers of the window (thinned deck beneath a tall cell, anvil above the deck) and has no setting.
    internal static class LayerWindOptions
    {
        internal static LayerSteeringMode? SteeringOverride;
        internal static bool? WindShearOverride, VerticalMotionOverride, AnvilTiltOverride;
        internal static bool Couple = true;
        // The marker test of the probe (E43Probe): a known rate in rad/s that every steered layer is pinned to; null = the shared rate decides.
        internal static double? MarkerRateRadS;

        internal static LayerSteeringMode Steering
        {
            get { return SteeringOverride ?? AtmospheresSettings.LayerWindSteering; }
        }

        internal static bool WindShear
        {
            get { return WindShearOverride ?? AtmospheresSettings.WindShear; }
        }

        internal static bool VerticalMotion
        {
            get { return VerticalMotionOverride ?? AtmospheresSettings.VerticalMotion; }
        }

        internal static bool AnvilTilt
        {
            get { return AnvilTiltOverride ?? AtmospheresSettings.AnvilTilt; }
        }

        // Whether any switch asks for the layered window.
        internal static bool AnyOn
        {
            get { return WindShear || VerticalMotion || AnvilTilt || Couple; }
        }

        // The options of the layered window columns and layer maps in the core.
        internal static LayerOptions Layers()
        {
            return new LayerOptions { Shear = WindShear, Vertical = VerticalMotion, Tilt = AnvilTilt, Couple = Couple, AmbientUpdraft = true };
        }
    }

    // The state of one layer's steering: the rate and the upward speed applied last, and the time of the last step.
    internal sealed class LayerSteerState
    {
        internal double RateRadS;
        internal double LastRealTime = double.NaN;
        internal double WindNorthMs, TargetRadS;
        internal bool Converged;
        internal double WindEastMs, PatternMs, TotalMs, HeightM, LatDeg;
        internal bool Started;
        internal bool BaseUpKnown;
        internal float BaseUp, UpApplied;
        internal double UpdraftMs;
    }

    internal static class LayerWindSteering
    {
        // The most the rate of the layer's turn may change per real second (rad/s per s): 0 to a drift of 20 m/s at 45 degrees (4.7e-5 rad/s) in about 4 minutes.
        internal const double MaxAccelRadS2 = 2.0e-7;
        // The steering step: every quarter of a real second (the paint interval of the painter).
        internal const double UpdateIntervalS = 0.25;
        // The largest rate (rad/s) steering asks for: 20 m/s at 60 degrees on a body of the size of Kerbin is 5.6e-5.
        internal const double MaxRateRadS = 8.0e-5;
        // upwardsCloudSpeed is the layer's own value times (1 + gain x updraft / reference), limited to this change per real second (share of the own value).
        internal const float UpdraftReferenceMs = 3f, UpdraftGain = 1f, UpMaxChangePerS = 0.1f;

        // The rate (rad/s, positive eastward) the layer turns against the field's patterns: THE rate of the core (HeightWind.LayerRate), the same function the volume window and
        // the layer maps use, so under cloudSource Both a layer of EVE and the window's deck of the same height move at one speed. windShear off = no drift against the patterns.
        internal static double SharedRate(BodyDerived d, BodyParams p, double heightM, bool shear)
        {
            if (!shear) return 0.0;
            double r = HeightWind.LayerRate(d, p, heightM);
            return r > MaxRateRadS ? MaxRateRadS : (r < -MaxRateRadS ? -MaxRateRadS : r);
        }

        // The eastward speed (m/s) of a layer at a latitude for a rate (rad/s): the pattern speed there plus the rate on the radius of that parallel.
        internal static double LayerSpeedMs(double patternMs, double rateRadS, double latRad, double radiusM)
        {
            return patternMs + rateRadS * radiusM * Math.Max(0.2, Math.Cos(latRad));
        }

        // One step of the rate toward its target with the limit on its change.
        internal static double StepRate(double current, double target, double dtS, double maxAccel)
        {
            double d = target - current, m = maxAccel * dtS;
            return d > m ? current + m : (d < -m ? current - m : target);
        }

        // Revolutions per second of EVE's drift for an angular rate against the body (sign: Tuning e43.rotationSign, +1 = east; [VERIFY] in the game).
        internal static double RevPerSecond(double rateRadS, float sign)
        {
            return sign * rateRadS / (2.0 * Math.PI);
        }

        // The height of a layer's middle above the surface in metres.
        internal static double LayerMidHeightM(float innerRadiusM, float outerRadiusM, double bodyRadiusM)
        {
            return 0.5 * (innerRadiusM + outerRadiusM) - bodyRadiusM;
        }

        // The new upwardsCloudSpeed: the layer's own value raised by the updraft, limited in its change.
        internal static float UpTarget(float baseUp, double updraftMs)
        {
            double u = updraftMs < 0.0 ? 0.0 : updraftMs;
            double share = u / UpdraftReferenceMs;
            if (share > 1.0) share = 1.0;
            return baseUp * (1f + UpdraftGain * (float)share);
        }

        internal static float StepUp(float current, float target, float baseUp, float dtS)
        {
            float m = Math.Abs(baseUp) * UpMaxChangePerS * dtS;
            if (m <= 0f) return target;
            float d = target - current;
            return d > m ? current + m : (d < -m ? current - m : target);
        }

        // The angle (degrees) between the drift of a layer (it turns about the polar axis, so it has an eastward component only) and the wind vector of
        // its height: the drift direction error. The northward wind component is an error of the model by construction.
        internal static double AngleErrorDeg(double driftEastMs, double windEastMs, double windNorthMs)
        {
            double a = Math.Atan2(0.0, driftEastMs) - Math.Atan2(windNorthMs, windEastMs);
            a = Math.Abs(Math.IEEERemainder(a, 2.0 * Math.PI));
            return a * 180.0 / Math.PI;
        }

        // Steers one layer (option A) at the place of the viewer. Called by the cloud module for every bound layer whose drift it froze.
        internal static void Update(EveLayer h, LayerSteerState st, CelestialBody body, double ut, double realNow)
        {
            if (LayerWindOptions.Steering != LayerSteeringMode.MainPeriod)
            {
                // Off and MapsOnly: the layer gets its frozen drift and its own upward speed back
                if (st.Started) Release(h, st, ut);
                return;
            }
            if (!h.DriftFrozen || body == null) return;
            // the reflection and the field sample cost a few allocations: the steering runs at UpdateIntervalS, the rate limit counts the real time between its steps
            if (!double.IsNaN(st.LastRealTime) && realNow - st.LastRealTime < UpdateIntervalS) return;
            GeneratorField gf = GeneratorModule.Current;
            GeneratorState s = gf != null ? gf.StateOf(body.flightGlobalsIndex) : null;
            if (s == null || s.Broken || !s.Parameters.HeightWind) return;
            double dt = double.IsNaN(st.LastRealTime) ? 0.0 : realNow - st.LastRealTime;
            st.LastRealTime = realNow;
            if (dt > 5.0) dt = 5.0;
            if (dt < 0.0) dt = 0.0;

            Vessel v = GameGlobals.ActiveVessel;
            double latDeg = v != null && v.mainBody == body ? v.latitude : 0.0, lonDeg = v != null && v.mainBody == body ? v.longitude : 0.0;
            double height = LayerMidHeightM(h.InnerRadiusM, h.OuterRadiusM, body.Radius);
            if (height < 100.0) return;
            WeatherSample sample;
            if (!WeatherHub.Field.Sample(body.flightGlobalsIndex, latDeg, lonDeg, height, ut, out sample)) return;
            double latRad = latDeg * Math.PI / 180.0;
            double pattern = HeightWind.PatternDriftMs(s.Generator.D, s.Parameters, Math.Abs(latRad));
            // the rate of the shared definition (not the wind at the viewer): window, layer maps and this layer turn at the same speed; windShear off = no drift against the patterns
            double target = SharedRate(s.Generator.D, s.Parameters, height, LayerWindOptions.WindShear);
            // the probe's marker test pins a known rate (both signs) to measure the rotation of the rendered layer; it bypasses the slew limit and is never set outside a probe
            double? marker = LayerWindOptions.MarkerRateRadS;
            if (marker.HasValue) target = marker.Value;
            st.TargetRadS = target;
            if (!st.Started)
            {
                double rev0;
                st.RateRadS = EveLayerAccess.TryGetDriftRev(h, out rev0) ? rev0 * 2.0 * Math.PI * Tuning.Get("e43.rotationSign", 1f) : 0.0;
                st.Started = true;
            }
            st.RateRadS = marker.HasValue ? target : StepRate(st.RateRadS, target, dt, MaxAccelRadS2);
            st.Converged = Math.Abs(st.RateRadS - target) < 1.0e-8;
            double rev = RevPerSecond(st.RateRadS, Tuning.Get("e43.rotationSign", 1f));
            EveLayerAccess.SetDriftRev(h, ut, rev);
            // the field's wind of this height at the viewer: kept for the comparison rows (the layer does not follow it away from the reference latitude)
            st.WindEastMs = sample.WindWorld.x;
            st.WindNorthMs = sample.WindWorld.y;
            st.PatternMs = pattern;
            st.TotalMs = LayerSpeedMs(pattern, st.RateRadS, latRad, s.Parameters.RadiusM);
            st.HeightM = height;
            st.LatDeg = latDeg;

            if (LayerWindOptions.VerticalMotion && EveLayerAccess.UpwardsAvailable)
            {
                if (!st.BaseUpKnown)
                {
                    float own;
                    if (EveLayerAccess.TryGetUpwards(h, out own)) { st.BaseUp = own; st.UpApplied = own; st.BaseUpKnown = true; }
                }
                if (st.BaseUpKnown)
                {
                    st.UpdraftMs = sample.WindWorld.z;
                    st.UpApplied = StepUp(st.UpApplied, UpTarget(st.BaseUp, st.UpdraftMs), st.BaseUp, (float)dt);
                    EveLayerAccess.SetUpwards(h, st.UpApplied);
                }
            }
        }

        // Gives the layer its own values back (the module calls it when the painter lets the layer go).
        internal static void Release(EveLayer h, LayerSteerState st, double ut)
        {
            if (st == null) return;
            if (st.Started && h.DriftFrozen) EveLayerAccess.SetDriftRev(h, ut, 0.0);
            if (st.BaseUpKnown) EveLayerAccess.SetUpwards(h, st.BaseUp);
            st.Started = false;
            st.BaseUpKnown = false;
        }
    }
}
