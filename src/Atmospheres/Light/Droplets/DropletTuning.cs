using System;

namespace ReDefinition.Atmospheres
{
    // Every constant of the droplets on the windows (package T08, item 2). The renderer reads them through Tuning.Get once a frame; the
    // model and the pass take this struct, so that they run without the game.
    //
    //   drop.fillS 25 (8-90)           seconds to a fully wet pane in steady rain at level 1 and no airflow
    //   drop.dryStillS 1500 (300-3600) seconds a wet pane needs to dry in still air (a film of drops on glass dries in minutes to an
    //                                  hour; EVE's dryingSpeed of about 12 s is the airflow case, not the still one)
    //   drop.blowRefMs 50 (20-120)     airflow at which the blow-off is as fast as blowS
    //   drop.blowS 12 (4-60)           seconds a pane needs to blow dry at blowRefMs (the blow-off grows with the square of the airflow)
    //   drop.catchRefMs 80 (30-200)    airflow at which a moving pane collects twice the rain of a still one
    //   drop.snowMelt 0.35 (0-1)       share of snowfall that melts into drops on a warm pane
    //   drop.slideG 0.045 (0.01-0.15)  speed of a drop under gravity, picture heights per second
    //   drop.slideAir 0.55 (0-1.5)     speed of a drop in an airflow of blowRefMs, picture heights per second
    //   drop.lagS 0.6 (0.1-3)          seconds the sliding direction takes to follow the airflow
    //   drop.coverMax 0.27 (0.1-0.28)  share of the glass the drops cover at full wetness (the layers cover 0.28 at most, DESIGN_COVER)
    //   drop.rim 0.45 (0-0.9)          darkening of the rim of a drop
    //   drop.trailAlpha 0.6 (0-1)      opacity of the wet trails
    //   drop.refraction 0.9 (0-2)      strength of the lens in a drop
    //   drop.skipBelow 0.02 (0.005-0.1) wetness under which the pass is skipped
    //   drop.quadAlpha 0.55 (0.2-1)    opacity of the fallback quad
    //   drop.sizePx 7 (3-16)           mean radius of a large drop in pixels at 1080 lines
    internal struct DropletTuning
    {
        public float FillS, DryStillS, BlowRefMs, BlowS, CatchRefMs, SnowMelt, SlideG, SlideAir, LagS, CoverMax, Refraction, SkipBelow, QuadAlpha, SizePx;
        public float Rim, TrailAlpha;

        internal static DropletTuning Defaults()
        {
            return new DropletTuning
            {
                FillS = 25f,
                DryStillS = 1500f,
                BlowRefMs = 50f,
                BlowS = 12f,
                CatchRefMs = 80f,
                SnowMelt = 0.35f,
                SlideG = 0.045f,
                SlideAir = 0.55f,
                LagS = 0.6f,
                CoverMax = 0.27f,
                Refraction = 0.9f,
                SkipBelow = 0.02f,
                QuadAlpha = 0.55f,
                SizePx = 7f,
                Rim = 0.45f,
                TrailAlpha = 0.6f
            };
        }

        internal DropletTuning Clamped()
        {
            DropletTuning t = this;
            t.FillS = C(t.FillS, 1f, 600f);
            t.DryStillS = C(t.DryStillS, 10f, 36000f);
            t.BlowRefMs = C(t.BlowRefMs, 5f, 400f);
            t.BlowS = C(t.BlowS, 0.5f, 600f);
            t.CatchRefMs = C(t.CatchRefMs, 5f, 1000f);
            t.SnowMelt = C(t.SnowMelt, 0f, 1f);
            t.SlideG = C(t.SlideG, 0f, 0.5f);
            t.SlideAir = C(t.SlideAir, 0f, 3f);
            t.LagS = C(t.LagS, 0.05f, 10f);
            t.CoverMax = C(t.CoverMax, 0.02f, 0.8f);
            t.Refraction = C(t.Refraction, 0f, 3f);
            t.SkipBelow = C(t.SkipBelow, 0f, 0.5f);
            t.QuadAlpha = C(t.QuadAlpha, 0f, 1f);
            t.SizePx = C(t.SizePx, 1f, 40f);
            t.Rim = C(t.Rim, 0f, 0.9f);
            t.TrailAlpha = C(t.TrailAlpha, 0f, 1f);
            return t;
        }

        private static float C(float v, float lo, float hi)
        {
            return float.IsNaN(v) ? lo : Math.Min(Math.Max(v, lo), hi);
        }
    }
}
