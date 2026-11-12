using System;

namespace ReDefinition.Atmospheres
{
    // Every constant of the surface wetness (package T08). The renderer reads them through Tuning.Get once a frame (WetSurfacesRenderer
    // .ReadTuning); the model and the look take this struct, so that they run without the game.
    //
    // The values are real-world quantities, not fits to a picture:
    //   * evaporation is a Penman-type potential rate: vapour-pressure deficit times (1 + 0.54 wind) times a radiation factor, with the
    //     coefficient chosen so that 15 C, 60 % humidity, 3 m/s and full sun give 0.12 mm/h (the band of a clear autumn day over wet
    //     bare ground is 0.05 to 0.3 mm/h);
    //   * the surface layer of bare ground holds about 2 mm before water stands on it, loam takes 1 mm/h from a puddle (Ksat of a loam is
    //     0.5 to 2 mm/h), a puddle is a few millimetres deep;
    //   * wet bare ground reflects 0.5 to 0.8 of what it reflects dry (wet sand and soil darken by a third to a half); the central value
    //     0.62 is the one in use.
    //
    // Keys, default and range for tuning (the ranges are the plausible real-world spread, not enforced):
    //   wet.wetDiffuse 0.62 (0.5-0.8)    diffuse luminance of fully wet ground against dry ground
    //   wet.wetSmooth 0.45 (0.2-0.7)     smoothness added at full wetness (G-buffer smoothness is 0 to 1)
    //   wet.wetPow 0.6 (0.4-1)           saturation to wetness curve (a little water already darkens)
    //   wet.puddleMax 0.35 (0.2-0.8)     largest share of the ground a puddle field covers (a soaked bare ground has a fifth to a half)
    //   wet.puddleFullMm 6 (3-12)        standing water at which the puddle share is largest
    //   wet.puddleDarken 0.8 (0.6-1)     extra diffuse factor in a puddle
    //   wet.puddleSmooth 0.95 (0.8-1)    smoothness in a puddle
    //   wet.frozenDarken 0.35 (0-1)      share of the wet darkening that stays where the water is frozen (glaze darkens little)
    //   wet.snowDamp 0.85 (0-1)          how much snow cover takes off the darkening and the puddles
    //   wet.evapK 0.067 (0.03-0.15)      coefficient of the potential evaporation, mm/h per kPa of deficit
    //   wet.soilCapMm 2 (0.5-4)          water the surface layer of terrain holds before water stands on it
    //   wet.soilKsatMmH 1 (0.1-5)        infiltration of standing water into the ground, mm/h
    //   wet.puddleCapMm 8 (4-15)         standing water beyond this runs off
    //   wet.maxStepS 120 (30-600)        longest step of the integration (a longer interval is cut into steps of this length)
    //   wet.lookbackH 96 (24-240)        how far back a catch-up reads the field's rain history
    //   wet.historyStepH 0.5 (0.1-2)     step of the history samples
    //   wet.rippleScale 1 (0-2)          strength of the rain rings on puddles
    //   wet.skipBelow 0.01 (0.002-0.05)  wetness under which the pass records nothing
    internal struct WetnessTuning
    {
        public float WetDiffuse, WetSmooth, WetPow, PuddleMax, PuddleFullMm, PuddleDarken, PuddleSmooth, FrozenDarken, SnowDamp;
        public float EvapK, SoilCapMm, SoilKsatMmH, PuddleCapMm, MaxStepS, LookbackH, HistoryStepH, RippleScale, SkipBelow;

        internal static WetnessTuning Defaults()
        {
            return new WetnessTuning
            {
                WetDiffuse = 0.62f,
                WetSmooth = 0.45f,
                WetPow = 0.6f,
                PuddleMax = 0.35f,
                PuddleFullMm = 6f,
                PuddleDarken = 0.8f,
                PuddleSmooth = 0.95f,
                FrozenDarken = 0.35f,
                SnowDamp = 0.85f,
                EvapK = 0.067f,
                SoilCapMm = 2f,
                SoilKsatMmH = 1f,
                PuddleCapMm = 8f,
                MaxStepS = 120f,
                LookbackH = 96f,
                HistoryStepH = 0.5f,
                RippleScale = 1f,
                SkipBelow = 0.01f
            };
        }

        // The tuning with every value brought into a range the maths can work with.
        internal WetnessTuning Clamped()
        {
            WetnessTuning t = this;
            t.WetDiffuse = Clamp(t.WetDiffuse, 0.2f, 1f);
            t.WetSmooth = Clamp(t.WetSmooth, 0f, 1f);
            t.WetPow = Clamp(t.WetPow, 0.2f, 2f);
            t.PuddleMax = Clamp(t.PuddleMax, 0f, 1f);
            t.PuddleFullMm = Clamp(t.PuddleFullMm, 0.5f, 50f);
            t.PuddleDarken = Clamp(t.PuddleDarken, 0.3f, 1f);
            t.PuddleSmooth = Clamp(t.PuddleSmooth, 0f, 1f);
            t.FrozenDarken = Clamp(t.FrozenDarken, 0f, 1f);
            t.SnowDamp = Clamp(t.SnowDamp, 0f, 1f);
            t.EvapK = Clamp(t.EvapK, 0.001f, 1f);
            t.SoilCapMm = Clamp(t.SoilCapMm, 0.05f, 20f);
            t.SoilKsatMmH = Clamp(t.SoilKsatMmH, 0f, 50f);
            t.PuddleCapMm = Clamp(t.PuddleCapMm, 0.5f, 100f);
            t.MaxStepS = Clamp(t.MaxStepS, 1f, 900f);
            t.LookbackH = Clamp(t.LookbackH, 1f, 720f);
            t.HistoryStepH = Clamp(t.HistoryStepH, 0.02f, 6f);
            t.RippleScale = Clamp(t.RippleScale, 0f, 4f);
            t.SkipBelow = Clamp(t.SkipBelow, 0f, 0.5f);
            return t;
        }

        internal static float Clamp(float v, float lo, float hi)
        {
            return float.IsNaN(v) ? lo : Math.Min(Math.Max(v, lo), hi);
        }
    }
}
