using System;

namespace ReDefinition.Atmospheres
{
    // Every look and rate constant of the camera frost. The renderer reads them through Tuning.Get once a frame (ReadTuning in
    // CameraFrostRenderer); the state machine and the shape maths take this struct, so that they run without the game.
    //
    // Keys, default and tuning range for E40 (the ranges are the proposed tuning ranges, not enforced):
    //   frost.buildS 45 (15-120)        seconds a full exposure takes to build under steady weather at no wind
    //   frost.clearS 30 (10-90)         seconds a full exposure takes to clear when sheltered (1/30 s)
    //   frost.maxGrowth 0.35 (0.2-0.35) reach of the rim frost at exposure 1, as a share of the half-diagonal (hard cap 0.35)
    //   frost.growthPow 0.7 (0.4-1.2)   exposure to growth curve
    //   frost.gain 1.25 (0.8-2)         intensity to exposure cap gain
    //   frost.windRefMs 20 (8-40)       airflow at which the wind share of the build rate is full
    //   frost.coldWidthK 10 (4-25)      kelvin below the body's frost limit over which the glass goes from clear to frosting
    //   frost.cabinK 293 (285-300)      cabin air temperature behind IVA windows
    //   frost.ivaInsulation 0.7 (0.3-0.95) share of the way the pane is pulled to the cabin temperature
    //   frost.ivaLagS 120 (30-400)      seconds the pane takes to follow its target temperature
    //   frost.evaLagS 10 (1-60)         the same for a visor
    //   frost.flakeMax 40 (0-40)        most stuck flakes
    //   frost.flakePxMin 3 (2-6)  frost.flakePxMax 15 (8-30)   flake diameter in pixels at 1080 lines
    //   frost.flakeLifeMin 8 (4-15)  frost.flakeLifeMax 20 (10-40)   seconds a flake takes to melt
    //   frost.flakeGust 0.8 (0.6-0.95)  gust phase a flake sticks at (rising edge)
    //   frost.flakePerGust 6 (1-12)     flakes stuck by one peak at level 1
    //   frost.dropMax 24 (0-24)         most drops on the glass
    //   frost.dropRate 4 (0-12)         drops per second at full wetness
    //   frost.dropPx 6 (3-14) frost.dropPxMax 16 (8-30)  drop diameter in pixels at 1080 lines
    //   frost.dropSpeed 0.05 (0.02-0.15) mean slide speed in picture heights per second
    //   frost.dropTrail 0.10 (0.03-0.25) longest wet trail in picture heights
    //   frost.skipBelow 0.02 (0.005-0.05) exposure under which the pass is skipped when nothing else is on the glass
    //   frost.jag 0.10 (0.04-0.2)       raggedness of the frost edge, in half-diagonals
    //   frost.centre 0.40 (0.3-0.5)     radius (share of the half-diagonal) inside which there is never frost
    //   frost.opacity 0.88 (0.5-1)      frost opacity at exposure 1
    //   frost.atlasScale 1.6 (0.8-3)    tiles of the atlas per picture height
    //   frost.refraction 0.012 (0-0.04) uv offset per unit of relief normal
    //   frost.blur 2.2 (0-5)            scatter blur radius in texels of the half-resolution copy
    //   frost.whiten 0.45 (0-1)         share the frost is pulled towards its own scatter colour
    //   frost.sunShare 0.7 (0-1)  frost.ambient 0.25 (0-1)   light shares of the frost colour
    //   frost.tintR 0.86  frost.tintG 0.93  frost.tintB 1.0   colour of the scattered light
    //   frost.dropGain 1 (0-2)          strength of the drop lens
    //   frost.flipY -1                  -1 decides by the platform (FrostPass.ResolveFlipY), 0 and 1 force it [VERIFY in the game]
    //   frost.quadAlpha 0.8 (0.3-1)     opacity of the quad fallback
    //   frost.quadBase 0.15 (0-0.4) frost.quadLight 0.7 (0.3-1)   colour of the quad fallback in scene units: base + light share
    //   frost.edgeSoft 0.05 (0.01-0.15) softness of the frost edge, in half-diagonals
    //   frost.thickMin 0.62 (0.3-1)     opacity share of the thinnest frost (exposure 0)
    //   frost.edgeBias 0.02 (-0.05-0.05) shift of the ragged edge, in half-diagonals (positive reaches further in)
    //   frost.flakeAlpha 0.9 frost.dropAlpha 0.85 frost.trailAlpha 0.35 (0.1-1)   opacity of flakes, drop heads and wet trails
    //   frost.flakeLens 0.8 (0-2)       strength of the lens in a flake
    //   frost.scatterMix 1.4 (0.5-3)    how fast the frost goes from the sharp picture to the scattered one
    //   frost.milk 0.5 (0-1)            milkiness of a flake
    //   frost.lumFrost 1.15 frost.lumFlake 1.3 (0.6-2)   gain on the scattered luminance in frost and in flakes
    //   frost.floorLight 0.01 (0-0.05)  light the frost keeps in a black picture, times the light factor
    //   frost.warmRangeK 6 (2-15)       kelvin above the frost limit over which the glass melts the frost at full speed
    //   frost.windBase 0.55 (0-1)       share of the build rate that does not depend on the air over the glass
    //   frost.rampBase 0.6 (0-1)        share of the exposure cap that does not depend on the time in weather
    //   frost.easedClear 0.333 (0.1-1)  share of the clear rate while weather is still falling but weaker than the exposure
    //   frost.shelterM 30 (0-200)       how far above the head an EVA camera looks for a roof; 0 switches the roof test off
    //   frost.ivaMask 1                 0 draws the IVA with the quad fallback only
    internal struct FrostTuning
    {
        internal float BuildS, ClearS, MaxGrowth, GrowthPow, Gain, WindRefMs, ColdWidthK, CabinK, IvaInsulation, IvaLagS, EvaLagS;
        internal int FlakeMax, DropMax;
        internal float FlakePxMin, FlakePxMax, FlakeLifeMin, FlakeLifeMax, FlakeGust, FlakePerGust;
        internal float DropRate, DropPx, DropPxMax, DropSpeed, DropTrail;
        internal float SkipBelow, Jag, Centre, Opacity, AtlasScale, Refraction, Blur, Whiten, SunShare, Ambient, DropGain, QuadAlpha;
        internal float TintR, TintG, TintB;
        internal bool FlipY;
        // The shape and the layers of the look: edge softness, thinnest frost, edge bias, the opacity weights of flakes, drop heads and
        // the wet trail, the flake lens, the scatter mix, the milkiness of a flake, the luminance gains of frost and flake scatter, the
        // floor light, and the quad fallback's colour (base + light share).
        internal float EdgeSoft, ThickMin, EdgeBias, FlakeAlpha, DropAlpha, TrailAlpha, FlakeLens, ScatterMix, Milk, LumFrost, LumFlake, FloorLight;
        internal float QuadBase, QuadLight;
        // The state machine's rates: the kelvin over which warm glass goes from frozen to melting, the share of the build rate that does
        // not depend on the wind and on the time in weather, the share of the clear rate while weather is still falling, and the reach of
        // the shelter ray in metres (0 switches it off).
        internal float WarmRangeK, WindBase, RampBase, EasedClear, ShelterM;

        internal static FrostTuning Defaults()
        {
            return new FrostTuning
            {
                EdgeSoft = 0.05f, ThickMin = 0.62f, EdgeBias = 0.02f, FlakeAlpha = 0.9f, DropAlpha = 0.85f, TrailAlpha = 0.35f, FlakeLens = 0.8f,
                ScatterMix = 1.4f, Milk = 0.5f, LumFrost = 1.15f, LumFlake = 1.3f, FloorLight = 0.01f, QuadBase = 0.15f, QuadLight = 0.7f,
                WarmRangeK = 6f, WindBase = 0.55f, RampBase = 0.6f, EasedClear = 1f / 3f, ShelterM = 30f,
                BuildS = 45f, ClearS = 30f, MaxGrowth = FrostShape.MaxGrowth, GrowthPow = 0.7f, Gain = 1.25f, WindRefMs = 20f,
                ColdWidthK = 10f, CabinK = 293f, IvaInsulation = 0.7f, IvaLagS = 120f, EvaLagS = 10f,
                FlakeMax = CameraFrost.MaxFlakes, DropMax = CameraFrost.MaxDrops,
                FlakePxMin = 3f, FlakePxMax = 15f, FlakeLifeMin = 8f, FlakeLifeMax = 20f, FlakeGust = 0.8f, FlakePerGust = 6f,
                DropRate = 4f, DropPx = 6f, DropPxMax = 16f, DropSpeed = 0.05f, DropTrail = 0.10f,
                SkipBelow = 0.02f, Jag = 0.10f, Centre = 0.40f, Opacity = 0.88f, AtlasScale = 1.6f, Refraction = 0.012f, Blur = 2.2f,
                Whiten = 0.45f, SunShare = 0.7f, Ambient = 0.25f, DropGain = 1f, QuadAlpha = 0.8f,
                TintR = 0.86f, TintG = 0.93f, TintB = 1f, FlipY = false
            };
        }

        // The tuning with every value held inside the range the maths is built for (a bad file never breaks the pass).
        internal FrostTuning Clamped()
        {
            FrostTuning t = this;
            t.BuildS = Math.Max(1f, t.BuildS);
            t.ClearS = Math.Max(1f, t.ClearS);
            t.MaxGrowth = Math.Min(Math.Max(t.MaxGrowth, 0f), FrostShape.MaxGrowth);
            t.GrowthPow = Math.Min(Math.Max(t.GrowthPow, 0.2f), 3f);
            t.ColdWidthK = Math.Max(0.5f, t.ColdWidthK);
            t.IvaInsulation = Math.Min(Math.Max(t.IvaInsulation, 0f), 1f);
            t.IvaLagS = Math.Max(0.1f, t.IvaLagS);
            t.EvaLagS = Math.Max(0.1f, t.EvaLagS);
            t.FlakeMax = Math.Min(Math.Max(t.FlakeMax, 0), CameraFrost.MaxFlakes);
            t.DropMax = Math.Min(Math.Max(t.DropMax, 0), CameraFrost.MaxDrops);
            t.FlakeLifeMin = Math.Max(0.5f, t.FlakeLifeMin);
            t.FlakeLifeMax = Math.Max(t.FlakeLifeMin, t.FlakeLifeMax);
            t.FlakePxMax = Math.Max(t.FlakePxMin, t.FlakePxMax);
            t.DropPxMax = Math.Max(t.DropPx, t.DropPxMax);
            t.Centre = Math.Min(Math.Max(t.Centre, 0.2f), 0.6f);
            t.SkipBelow = Math.Min(Math.Max(t.SkipBelow, 0.001f), 0.2f);
            t.EdgeSoft = Math.Max(t.EdgeSoft, 0.005f);
            t.ThickMin = Math.Min(Math.Max(t.ThickMin, 0.1f), 1f);
            t.EdgeBias = Math.Min(Math.Max(t.EdgeBias, -0.1f), 0.1f);
            t.WarmRangeK = Math.Max(t.WarmRangeK, 0.5f);
            t.WindBase = Math.Min(Math.Max(t.WindBase, 0f), 1f);
            t.RampBase = Math.Min(Math.Max(t.RampBase, 0f), 1f);
            t.EasedClear = Math.Min(Math.Max(t.EasedClear, 0.05f), 1f);
            t.ShelterM = Math.Min(Math.Max(t.ShelterM, 0f), 200f);
            return t;
        }
    }
}
