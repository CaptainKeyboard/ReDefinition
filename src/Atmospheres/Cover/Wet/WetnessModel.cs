using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The kinds of surface that hold water differently (EVE's wetness tracks scenery, terrain and craft apart for the same reason).
    internal enum WetClass
    {
        // Bare ground: a surface layer that soaks water up and gives it back slowly, and lets standing water seep away.
        Terrain = 0,
        // Rock, concrete, buildings: a thin film, nothing seeps, water runs off.
        Scenery = 1,
        // Vessel skin: a film that runs off within minutes.
        Craft = 2
    }

    // What holds and sheds water for a class. Capacities in mm of water, rates per hour.
    internal struct WetClassParams
    {
        // Water the surface layer holds before it stands (saturation of the wetness).
        public float StoreMm;
        // Infiltration of standing water, mm/h.
        public float SeepMmH;
        // Share of standing water that runs off per hour (a slope or an edge); a capped puddle also runs over.
        public float RunoffPerH;
        // Share of the potential evaporation the stored (not standing) water gives at saturation; it falls to a quarter of it when dry
        // (the second stage of drying: water has to come up from the pores).
        public float StoredEvapAtFull;
    }

    // What the weather does to a surface over one interval. A plain struct of numbers.
    internal struct WetInput
    {
        // Liquid water reaching the surface, mm/h (rain, the liquid part of sleet, melting snow).
        public float WaterMmH;
        public float TemperatureK;
        // The temperature below which water on this body is frozen (273.15 on a body with water).
        public float FreezeK;
        public float WindMs;
        // Relative humidity 0 to 1 of the air at the surface.
        public float Humidity01;
        // The sun's direct light on the ground, 0 (night) to 1 (high sun).
        public float Sun01;
    }

    // The water on a surface: what the layer holds and what stands on it, both in mm.
    internal struct WetState
    {
        public float StoredMm, StandingMm;
    }

    // The wetness of a surface as a pure function of the weather it saw (package T08, items 1 and 3). The state is two stores: the water
    // the surface layer holds (it darkens and glosses the ground; saturates at the class's store) and the water that stands on it (it
    // makes puddles). Rain fills the layer first and then the puddles; puddles seep away, run off and evaporate; the layer dries after
    // the puddles are gone. The potential evaporation is a Penman-type rate of temperature, humidity, wind and sun, so cold, damp, calm
    // and dark weather dries slowly and warm, dry, windy and sunny weather dries fast.
    //
    // A long interval is cut into steps of at most MaxStepS, the rates are constant inside a step; so a jump of 6 hours and the same
    // 6 hours in frames agree (the test compares them within 5 %), and the model needs no per-frame history: the catch-up replays the
    // field's rain over the lookback (WetnessCatchUp).
    internal static class WetnessModel
    {
        // Parameters of the classes; the terrain's store and seepage come from the tuning.
        internal static WetClassParams ParamsOf(WetClass kind, ref WetnessTuning t)
        {
            switch (kind)
            {
                case WetClass.Scenery:
                    return new WetClassParams { StoreMm = 0.3f, SeepMmH = 0f, RunoffPerH = 4f, StoredEvapAtFull = 1f };
                case WetClass.Craft:
                    return new WetClassParams { StoreMm = 0.15f, SeepMmH = 0f, RunoffPerH = 20f, StoredEvapAtFull = 1f };
                default:
                    return new WetClassParams { StoreMm = t.SoilCapMm, SeepMmH = t.SoilKsatMmH, RunoffPerH = 0.05f, StoredEvapAtFull = 1f };
            }
        }

        // The saturation vapour pressure of water in kPa (Magnus form) at a temperature in kelvin.
        internal static float SaturationKPa(float temperatureK)
        {
            float c = temperatureK - 273.15f;
            return 0.6108f * (float)Math.Exp(17.27f * c / (c + 237.3f));
        }

        // The potential evaporation in mm/h: vapour-pressure deficit times (1 + 0.54 wind) times a radiation factor (0.35 in the dark,
        // 1 in full sun). Water frozen on the ground sublimates at a sixth of the rate.
        internal static float Evaporation(ref WetInput w, ref WetnessTuning t)
        {
            float tk = float.IsNaN(w.TemperatureK) ? 288f : w.TemperatureK;
            float deficit = SaturationKPa(tk) * (1f - Clamp01(w.Humidity01));
            float wind = Math.Min(Math.Max(w.WindMs, 0f), 15f);
            float rate = t.EvapK * deficit * (1f + 0.54f * wind) * (0.35f + 0.65f * Clamp01(w.Sun01));
            if (!float.IsNaN(w.TemperatureK) && w.TemperatureK < w.FreezeK) rate *= 0.17f;
            return rate;
        }

        // One interval of the weather on a surface class. dtS of 0 or less changes nothing.
        internal static void Step(ref WetState s, ref WetInput w, WetClass kind, ref WetnessTuning t, float dtS)
        {
            if (!(dtS > 0f)) return;
            WetClassParams p = ParamsOf(kind, ref t);
            int steps = Math.Max(1, (int)Math.Ceiling(dtS / t.MaxStepS));
            float hS = dtS / steps;
            float hH = hS / 3600f;
            float evap = Evaporation(ref w, ref t);
            bool frozen = !float.IsNaN(w.TemperatureK) && w.TemperatureK < w.FreezeK;
            float seep = frozen ? 0f : p.SeepMmH;
            float water = Math.Max(w.WaterMmH, 0f);
            for (int i = 0; i < steps; i++)
            {
                float intake = water * hH;
                float free = Math.Max(p.StoreMm - s.StoredMm, 0f);
                float toLayer = Math.Min(intake, free);
                s.StoredMm += toLayer;
                s.StandingMm += intake - toLayer;
                // Standing water leaves by seepage and run-off; a puddle over its cap runs over.
                float out1 = Math.Min(s.StandingMm, seep * hH);
                s.StandingMm -= out1;
                s.StandingMm -= s.StandingMm * Math.Min(1f, p.RunoffPerH * hH);
                if (s.StandingMm > t.PuddleCapMm) s.StandingMm = t.PuddleCapMm;
                // Seepage feeds the layer below the puddle up to its store.
                s.StoredMm = Math.Min(p.StoreMm, s.StoredMm + out1 * 0.5f);
                // Evaporation takes the standing water first, then the layer at a rate that falls with its dryness.
                float demand = evap * hH;
                float fromPuddle = Math.Min(s.StandingMm, demand);
                s.StandingMm -= fromPuddle;
                float rest = demand - fromPuddle;
                if (rest > 0f && s.StoredMm > 0f)
                {
                    float sat = Clamp01(s.StoredMm / Math.Max(p.StoreMm, 1e-4f));
                    s.StoredMm = Math.Max(0f, s.StoredMm - rest * p.StoredEvapAtFull * (0.25f + 0.75f * sat));
                }
                if (s.StoredMm < 1e-6f) s.StoredMm = 0f;
                if (s.StandingMm < 1e-6f) s.StandingMm = 0f;
            }
        }

        // The wetness 0 to 1 of the surface layer (saturation).
        internal static float Saturation(ref WetState s, WetClass kind, ref WetnessTuning t)
        {
            WetClassParams p = ParamsOf(kind, ref t);
            return Clamp01(s.StoredMm / Math.Max(p.StoreMm, 1e-4f));
        }

        // The time in seconds the layer needs to dry from a stored amount (no standing water) at a constant evaporation: the closed
        // form of dM/dt = -E (0.25 + 0.75 M / S), the reference of the drying test. Infinity where nothing evaporates.
        internal static double DryTimeS(double storedMm, double storeMm, double evapMmH)
        {
            if (storedMm <= 0.0) return 0.0;
            if (!(evapMmH > 0.0)) return double.PositiveInfinity;
            double a = 0.25, b = 0.75 / storeMm;
            return Math.Log(1.0 + b * storedMm / a) / (evapMmH * b) * 3600.0;
        }

        // The liquid water reaching the ground, mm/h, from a weather sample's level and type. rateMmPerH is the body's rate for the level
        // (PrecipRate.FromLevel). Rain and methane (liquid) count in full, the liquid part of sleet in full, freezing rain in full (it
        // freezes on the ground: the look handles that), snow only as far as the ground melts it (a ramp over 2.5 K above freezing).
        internal static float Intake(PrecipType type, float rainFraction, float sleetFraction, float snowFraction, float rateMmPerH, float temperatureK, float freezeK)
        {
            if (!(rateMmPerH > 0f)) return 0f;
            switch (type)
            {
                case PrecipType.Rain:
                case PrecipType.FreezingRain:
                case PrecipType.Methane:
                    return rateMmPerH;
                case PrecipType.Sleet:
                    return rateMmPerH;
                case PrecipType.Snow:
                {
                    float melt = float.IsNaN(temperatureK) ? 0f : Smooth(freezeK, freezeK + 2.5f, temperatureK);
                    return rateMmPerH * melt;
                }
                default:
                    return 0f;
            }
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        internal static float Clamp01(float v)
        {
            return float.IsNaN(v) ? 0f : Math.Min(Math.Max(v, 0f), 1f);
        }
    }

    // The rain rate of a precipitation level on a body: the inverse of PrecipScale.LevelFromRate (piecewise linear between the rates at
    // which the bands start).
    internal static class PrecipRate
    {
        internal static float FromLevel(float level01, float[] bandRatesMmPerH)
        {
            if (!(level01 > 0f) || bandRatesMmPerH == null || bandRatesMmPerH.Length < 5) return 0f;
            float[] r = bandRatesMmPerH;
            float[] starts = { PrecipScale.FlurriesFrom, PrecipScale.LightFrom, PrecipScale.ModerateFrom, PrecipScale.HeavyFrom, PrecipScale.BlizzardFrom };
            if (level01 < starts[0]) return r[0] * (level01 / starts[0]);
            for (int i = 0; i < 4; i++)
                if (level01 < starts[i + 1]) return Lerp(r[i], r[i + 1], (level01 - starts[i]) / (starts[i + 1] - starts[i]));
            float top = r.Length >= 6 ? r[5] : r[4] + (r[4] - r[3]);
            return Lerp(r[4], top, Math.Min(1f, (level01 - starts[4]) / (1f - starts[4])));
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }
}
