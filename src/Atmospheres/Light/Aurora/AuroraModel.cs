using System;

namespace ReDefinition.Atmospheres
{
    // Package D38. The state of the aurora as a pure function of the universal time, the sun's elevation and the body's rules:
    // no engine type, so the unit tests and the weather player use the same numbers the mod does.
    //
    // Activity A(ut) is deterministic value noise of three octaves (periods 6 h, 1.5 h and 12 min), mapped through a soft threshold so
    // that quiet nights are common: A is 0 below the threshold, and about 30 % of the time A is above 0.3. The oval lies at latitude
    // 78 - 12 A degrees with a width of 3 + 6 A degrees; it is gated by the night (smoothstep of the sun's elevation from -3 to -9
    // degrees). A rare strong event (a deterministic function of the UT, owned here: no solar flare exists elsewhere in the plans)
    // raises A to its top and pushes the oval a further 6 degrees towards the equator for about a day.
    //
    // Technique references: SPVE and EVEVolumetricsAurorae put the aurora in a ring around the pole with a coverage map.
    internal struct AuroraFrame
    {
        // Whether anything is drawn this frame.
        public bool Draw;
        // The activity 0 to 1 (the larger of the noise and the strong event), the share of the strong event, the night gate 0 to 1.
        public float Activity, Event, Night;
        // The oval's centre latitude and its full width, in degrees.
        public float RingLatDeg, WidthDeg;
        // The emissive scale of the whole shell: night gate x activity ramp x strength of the body's rules.
        public float Intensity;
    }

    internal static class AuroraModel
    {
        // The periods of the three octaves in seconds, their weights, and the salts that keep them independent.
        internal const double Period1 = 21600.0, Period2 = 5400.0, Period3 = 720.0;
        internal const float Weight1 = 0.55f, Weight2 = 0.30f, Weight3 = 0.15f;

        // The soft threshold: the sum of the octaves below Lo gives no activity, above Hi the full one. Chosen so that the activity is
        // above 0.3 for about 30 % of the time (AuroraModelTests measures it).
        internal const float ThresholdLo = 0.485f, ThresholdHi = 0.80f;

        internal const float RingTopDeg = 78f, RingDropDeg = 12f, WidthBaseDeg = 3f, WidthGainDeg = 6f;

        // The strong event: one slot of 20 days in which it may occur, the share of slots that hold one, its length (36 h) and the extra
        // push of the oval towards the equator.
        internal const double EventSlot = 20.0 * 86400.0, EventLength = 129600.0;
        internal const float EventSlotShare = 0.18f, EventPushDeg = 6f;

        // The sun's elevation (degrees) at which the night gate opens and where it is full.
        internal const float NightStartDeg = -3f, NightFullDeg = -9f;

        // The pure activity 0 to 1 of a moment.
        internal static float Activity(double ut)
        {
            double sum = Weight1 * Noise(ut / Period1, 11u) + Weight2 * Noise(ut / Period2, 23u) + Weight3 * Noise(ut / Period3, 37u);
            return SmoothStep(ThresholdLo, ThresholdHi, (float)sum);
        }

        // The strong event 0 to 1 at a moment: zero almost always, an envelope of about 36 h when a slot holds one.
        internal static float StrongEvent(double ut)
        {
            double slot = Math.Floor(ut / EventSlot);
            long k = (long)slot;
            if (Lattice(k, 91u) > EventSlotShare) return 0f;
            double start = Lattice(k, 92u) * (EventSlot - EventLength);
            double f = (ut - slot * EventSlot - start) / EventLength;
            if (f <= 0.0 || f >= 1.0) return 0f;
            return SmoothStep(0f, 0.15f, (float)f) * (1f - SmoothStep(0.7f, 1f, (float)f));
        }

        // 0 in the day, 1 in the night, smooth between a sun elevation of -3 and -9 degrees.
        internal static float NightGate(float sunElevationDeg)
        {
            return SmoothStep(NightStartDeg, NightFullDeg, sunElevationDeg);
        }

        // The oval's centre latitude for an activity and an event share; never nearer the equator than the body's floor.
        internal static float RingLatitudeDeg(float activity, float strongEvent, float floorDeg)
        {
            return Math.Max(floorDeg, RingTopDeg - RingDropDeg * activity - EventPushDeg * strongEvent);
        }

        // The oval's full width in degrees.
        internal static float WidthDeg(float activity)
        {
            return WidthBaseDeg + WidthGainDeg * activity;
        }

        // How bright the shell is for an activity: nothing on a quiet night, a full ramp by 0.5.
        internal static float ActivityRamp(float activity)
        {
            return SmoothStep(0.02f, 0.5f, activity);
        }

        // The compass heading (0 north, 180 south) that faces the oval from a camera at that latitude: north while the oval lies poleward
        // of the camera, south while it lies equatorward. The weather player and the series probe aim by it.
        internal static float PoseYawDeg(float ringLatDeg, float cameraLatDeg)
        {
            return ringLatDeg >= cameraLatDeg ? 0f : 180f;
        }

        // The whole frame for a body whose rules are given. enabled false (rules or no atmosphere) gives no draw.
        internal static AuroraFrame Compute(double ut, float sunElevationDeg, bool enabled, float minLatDeg, float strength)
        {
            if (!enabled || strength <= 0f) return new AuroraFrame();
            float strongEvent = StrongEvent(ut);
            return FromActivity(Math.Max(Activity(ut), strongEvent), strongEvent, sunElevationDeg, minLatDeg, strength);
        }

        // The frame for a given activity and event share (the model's own arithmetic; the series and the weather player force an
        // activity through it).
        internal static AuroraFrame FromActivity(float activity, float strongEvent, float sunElevationDeg, float minLatDeg, float strength)
        {
            var f = new AuroraFrame();
            if (strength <= 0f) return f;
            f.Night = NightGate(sunElevationDeg);
            f.Event = strongEvent;
            f.Activity = activity;
            f.RingLatDeg = RingLatitudeDeg(f.Activity, f.Event, minLatDeg);
            f.WidthDeg = WidthDeg(f.Activity);
            f.Intensity = f.Night * ActivityRamp(f.Activity) * strength;
            f.Draw = f.Intensity > 0.002f;
            return f;
        }

        // How far the shell fades in with the elevation of the view ray above the horizon: 0 below 3 degrees (the lid and the band on
        // the horizon the earlier layer showed), 1 from 10 degrees. The shader uses the same two numbers.
        internal const float HorizonFadeStartDeg = 3f, HorizonFadeFullDeg = 10f;

        internal static float HorizonFade(float elevationDeg, float startDeg, float fullDeg)
        {
            return SmoothStep(startDeg, fullDeg, elevationDeg);
        }

        // The sun's elevation at a place, in degrees, from the dot product of the direction to the sun with the local up.
        internal static float SunElevationDeg(double dotUpSun)
        {
            double d = Math.Max(-1.0, Math.Min(1.0, dotUpSun));
            return (float)(Math.Asin(d) * 180.0 / Math.PI);
        }

        internal static float SmoothStep(float a, float b, float x)
        {
            if (b == a) return x >= b ? 1f : 0f;
            float t = Math.Max(0f, Math.Min(1f, (x - a) / (b - a)));
            return t * t * (3f - 2f * t);
        }

        // Value noise 0 to 1 of one coordinate: smooth (quintic) interpolation between hashed lattice values.
        internal static float Noise(double x, uint salt)
        {
            double fl = Math.Floor(x);
            long i = (long)fl;
            float t = (float)(x - fl);
            float s = t * t * t * (t * (t * 6f - 15f) + 10f);
            float a = Lattice(i, salt), b = Lattice(i + 1, salt);
            return a + (b - a) * s;
        }

        // The hashed value 0 to 1 at a lattice point.
        internal static float Lattice(long i, uint salt)
        {
            uint h = unchecked((uint)i * 0x9e3779b9u + (uint)(i >> 32) * 0x85ebca6bu + salt * 0xc2b2ae35u);
            h ^= h >> 16;
            h = unchecked(h * 0x7feb352du);
            h ^= h >> 15;
            h = unchecked(h * 0x846ca68bu);
            h ^= h >> 16;
            return (h >> 8) * (1f / 16777216f);
        }
    }
}
