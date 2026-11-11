using System;

namespace ReDefinition.Atmospheres
{
    // What the sea ice looks like at one point of the sea: whether there is ice, how opaque it is, how thick, and whether the point is
    // in a lead (a crack of open water between two floes).
    internal struct IceState
    {
        // True where there is ice (a floe); false for open water, also in a lead.
        internal bool Ice;
        // True where the point lies in the narrow band of open water along the border of a floe.
        internal bool Lead;
        // 0 to 1: thin ice is nearly clear, thick ice opaque (the thickness maps to the look).
        internal float Opacity;
        // Metres; 0 where there is no ice.
        internal float ThicknessM;
        // The fraction the cell test used at this point (the field's, with the shore and edge structure added), 0 to 1.
        internal float LocalFraction;
    }

    // The inputs of IceMask for one body: from the ICE node (floe size, lead fraction, permanent) and the body (radius, seed), plus the
    // look constants. Everything the shader needs as a uniform is here.
    internal sealed class IceParams
    {
        // The body's radius in metres.
        internal float RadiusM = 600000f;
        // Floe size in km and the share of open water between the floes at a fraction of 0 (it shrinks as the fraction rises).
        internal float FloeSizeKm = 3f;
        internal float LeadFraction = 0.08f;
        // True where the sea is always frozen (fraction 1, no clock).
        internal bool Permanent;
        // The seed of the body's pattern.
        internal uint Seed = 12345u;
        // The thickness in metres of a permanent sea whose field has none.
        internal float PermanentThicknessM = 2f;
        // How much the shore decides where the ice is (0 to 0.5: at 0.5 the coast alone could fill or empty a cell completely).
        internal float ShoreGain = 0.35f;
        // How much the low-frequency noise ragged the edge of the pack (0 to 0.3), more away from the shore.
        internal float EdgeNoise = 0.2f;

        // The cells of the floe pattern along one radius.
        internal float Frequency
        {
            get { return RadiusM / Math.Max(1f, FloeSizeKm * 1000f); }
        }
    }

    // The sub-grid structure of the sea ice, pure (no engine type): the field gives the ice FRACTION and THICKNESS at a coarse scale;
    // this draws floes and leads inside it so that the mean over a region is the field's fraction.
    //
    // A floe is a Worley cell of the pattern (a drifting 3D domain: the direction on the sphere times the cells per radius, plus the
    // drift offset). Each cell has one number in [0, 1) that is uniform over cells; the cell is ice where that number is below the local
    // fraction, so the share of ice cells is the local fraction (exactly in the mean). The local fraction is the field's with two
    // additions that have about zero mean over the bilinear support of a field sample (ShoreGrid's 9 x 9 box; the clamps and the box average make it approximate near coasts, IceOrbitPicture.CoastCoverage measures it): the shore term (cells near the coast freeze first, the coast
    // term minus its local mean, scaled by 2 min(f, 1 - f) so that nothing changes at 0 and 1) and the low-frequency noise that ragged
    // the pack edge (also scaled by 2 min(f, 1 - f), more away from the shore). Leads are the band of the Worley border (F2 - F1 below
    // a width) in an ice cell; the cell test is raised by the area the band takes (ice cells are chosen with p = local / (1 - band
    // share)), so the leads do not lower the mean either.
    //
    // No clock and no cold curve: the field decides when and where it freezes (Stefan growth, mixed-layer lag, melt); this class adds
    // only the shape below the field's texel. The shader repeats Evaluate (ReDefinitionAtmospheresIceOverlay.shader, function IceEval).
    internal static class IceMask
    {
        // The area share of the band {F2 - F1 < w} for the jitter of IceHash, measured over 25000 random points
        // (IceMaskTests.TheLeadBandTakesTheShareItShould checks it): the widths, and the shares they take. WidthFor inverts it.
        internal static readonly float[] BandWidths = { 0f, 0.01f, 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.3f, 0.4f, 0.5f, 0.8f };
        internal static readonly float[] BandShares = { 0f, 0.0371f, 0.0724f, 0.176f, 0.329f, 0.4588f, 0.57f, 0.7435f, 0.8638f, 0.936f, 1f };
        // The largest band share the mask asks for.
        internal const float MaxBandShare = 0.9f;

        // The width (cell units) of the band that takes that share of the cell area, by linear interpolation of the table.
        internal static float WidthFor(float share)
        {
            if (!(share > 0f)) return 0f;
            if (share >= BandShares[BandShares.Length - 1]) return BandWidths[BandWidths.Length - 1];
            for (int i = 1; i < BandShares.Length; i++)
            {
                if (share > BandShares[i]) continue;
                float t = (share - BandShares[i - 1]) / (BandShares[i] - BandShares[i - 1]);
                return BandWidths[i - 1] + (BandWidths[i] - BandWidths[i - 1]) * t;
            }
            return BandWidths[BandWidths.Length - 1];
        }

        // The thickness at which ice starts to hide the water, and the thickness of fully opaque ice, in metres.
        internal const float ThinM = 0.02f;
        internal const float ThickM = 0.30f;
        // The opacity of the thinnest ice: a sheen, not a mirror of the water. Known limit: the orbit shell hides the planet's sun glint
        // only where its alpha is opaque; thin ice (this floor) lets part of the glint through, and the planet's colour map is untouched
        // (a glint hook would be a change to the scaled-space shader).
        internal const float ThinOpacity = 0.15f;
        // How much the thickness varies between the floes of one field value (0 to 1).
        internal const float ThicknessSpread = 0.25f;
        // The scale (cells) of the low-frequency edge noise relative to the floe cells, and its offset.
        internal const float EdgeNoiseScale = 0.125f;

        internal static readonly IceState None = new IceState();

        // The ice at a point. (dx, dy, dz) is the unit direction from the body's centre in the sphere's frame; fieldFraction and
        // thicknessM are the field's at that place; shore and shoreMean are the coast term at the point and its mean over the support
        // of the field's texel (ShoreGrid); (ox, oy, oz) is the drift offset in cell units. The point must be on the sea: the caller
        // passes a sea test.
        internal static IceState Evaluate(IceParams p, float dx, float dy, float dz, float fieldFraction, float thicknessM, float shore, float shoreMean,
            float ox, float oy, float oz)
        {
            float f = p.Permanent ? 1f : Clamp01(Finite(fieldFraction));
            if (f <= 0f) return None;
            float th = Finite(thicknessM);
            if (th <= 0f && p.Permanent) th = p.PermanentThicknessM;
            float freq = p.Frequency;
            float px = dx * freq + ox, py = dy * freq + oy, pz = dz * freq + oz;

            float local = f;
            if (f < 1f)
            {
                float edge = 2f * Math.Min(f, 1f - f);
                float noise = IceHash.ValueNoise(px * EdgeNoiseScale + 7.3f, py * EdgeNoiseScale + 1.9f, pz * EdgeNoiseScale + 4.1f, p.Seed ^ 0xA5A5A5A5u);
                local = f + p.ShoreGain * edge * (Clamp01(shore) - Clamp01(shoreMean))
                        + p.EdgeNoise * edge * (0.35f + 0.65f * (1f - Clamp01(shore))) * noise;
                local = Clamp01(local);
            }

            float band = Math.Min(MaxBandShare, Math.Max(0f, p.LeadFraction) * (1f - f));
            float width = WidthFor(band);
            float cellChance = Math.Min(1f, local / Math.Max(1e-3f, 1f - band));
            IceHash.Worley w = IceHash.WorleyAt(px, py, pz, p.Seed);
            var state = new IceState { LocalFraction = local };
            if (w.CellValue >= cellChance) return state;
            if (w.F2 - w.F1 < width)
            {
                state.Lead = true;
                return state;
            }
            state.Ice = true;
            float spread = cellChance > 0f ? 1f - 2f * w.CellValue / cellChance : 0f;
            state.ThicknessM = Math.Max(0.001f, th * (1f + ThicknessSpread * spread));
            state.Opacity = OpacityOf(state.ThicknessM);
            return state;
        }

        // How opaque ice of that thickness is: ThinOpacity at ThinM and below, 1 from ThickM up, smooth between.
        internal static float OpacityOf(float thicknessM)
        {
            float t = Clamp01((thicknessM - ThinM) / (ThickM - ThinM));
            t = t * t * (3f - 2f * t);
            return ThinOpacity + (1f - ThinOpacity) * t;
        }

        // The colour of ice of that thickness: the body's ice colour for thick ice, a darker, bluer water-through-ice colour for thin ice.
        internal static void ColourOf(float thicknessM, float r, float g, float b, float snow01, out float outR, out float outG, out float outB)
        {
            float t = Clamp01((thicknessM - ThinM) / (ThickM * 2f - ThinM));
            float tr = 0.30f, tg = 0.46f, tb = 0.62f;
            float cr = tr + (r - tr) * t, cg = tg + (g - tg) * t, cb = tb + (b - tb) * t;
            float s = Clamp01(snow01);
            outR = cr + (0.96f - cr) * s;
            outG = cg + (0.97f - cg) * s;
            outB = cb + (0.99f - cb) * s;
        }

        // The ice test on its own (the coverage the rows count): true where there is a floe.
        internal static bool IsIce(IceParams p, float dx, float dy, float dz, float fieldFraction, float shore, float shoreMean, float ox, float oy, float oz)
        {
            return Evaluate(p, dx, dy, dz, fieldFraction, 1f, shore, shoreMean, ox, oy, oz).Ice;
        }

        private static float Finite(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
