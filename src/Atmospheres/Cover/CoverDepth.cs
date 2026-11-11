using System;
using static ReDefinition.Atmospheres.Mathx;

namespace ReDefinition.Atmospheres
{
    // The cover depth at a point from the weather field's surface layer (metres) and the layer's mask. The field is the one source: this
    // is not a second cold. Pure numbers. The depth 0 to 1 is the field's depth over the layer's full depth, times the mask; the noise
    // (SnowNoise) only roughens the edge of the cover below the resolution of the field's 256 x 128 map, so that its cells do not show.
    internal static class CoverDepth
    {
        // The width of the smooth edge of the latitude and altitude mask, in degrees and in metres.
        internal const double MaskEdgeDegrees = 1.5;
        internal const double MaskEdgeMetres = 150.0;

        // The slope range over which the cover thins out below MaxSlopeDeg, in degrees.
        internal const double SlopeEdgeDegrees = 12.0;

        // The depth values of the R16 map: 0 is bare, 65535 is the layer's full depth.
        internal const int Quanta = 65535;

        // The mask 0 to 1 at a place: inside the layer's latitude and altitude range, not too steep, not water where the layer says so.
        // slopeDeg is the angle of the ground from the horizontal (0 flat); isWater is true over the sea.
        internal static double Mask(CoverDef def, double latDeg, double altM, double slopeDeg, bool isWater)
        {
            if (def == null || double.IsNaN(latDeg) || double.IsNaN(altM)) return 0;
            if (isWater && def.NoWater) return 0;
            // A bound at the pole is no bound: the edge is only drawn where the layer stops inside the globe.
            double lat = 1;
            if (def.MinLatDeg > -90.0) lat *= SmoothStep(def.MinLatDeg - MaskEdgeDegrees, def.MinLatDeg + MaskEdgeDegrees, latDeg);
            if (def.MaxLatDeg < 90.0) lat *= 1 - SmoothStep(def.MaxLatDeg - MaskEdgeDegrees, def.MaxLatDeg + MaskEdgeDegrees, latDeg);
            if (lat <= 0) return 0;
            double alt = 1;
            if (!double.IsInfinity(def.MinAltM)) alt *= SmoothStep(def.MinAltM - MaskEdgeMetres, def.MinAltM + MaskEdgeMetres, altM);
            if (!double.IsInfinity(def.MaxAltM)) alt *= 1 - SmoothStep(def.MaxAltM - MaskEdgeMetres, def.MaxAltM + MaskEdgeMetres, altM);
            if (alt <= 0) return 0;
            double slope = double.IsNaN(slopeDeg) ? 0 : 1 - SmoothStep(def.MaxSlopeDeg - SlopeEdgeDegrees, def.MaxSlopeDeg, Math.Abs(slopeDeg));
            return lat * alt * slope;
        }

        // The base depth 0 to 1 from the field's value in metres (before the mask and the noise). A value that is not a number is bare.
        internal static double Base01(CoverDef def, double fieldDepthM)
        {
            if (def == null || double.IsNaN(fieldDepthM) || fieldDepthM <= 0) return 0;
            return Clamp01(fieldDepthM / def.MaxDepthM);
        }

        // The depth 0 to 1 that a vertex or a texel shows: the base depth times the mask, its edge eroded by the noise (-1 to 1) by up to
        // edgeNoise of the full depth. With edgeNoise 0 the depth is exactly the field's depth times the mask times the strength. A full
        // depth stays full (the erosion takes a share t of the depth and stretches the rest back).
        internal static double Depth01(CoverDef def, double fieldDepthM, double mask, double noise, double edgeNoise, double strength)
        {
            double b = Base01(def, fieldDepthM) * Clamp01(mask);
            if (b <= 0) return 0;
            double t = Clamp01(edgeNoise) * (1 - Clamp(noise, -1, 1)) * 0.5;
            double eroded = t <= 0 ? b : Clamp01((b - t) / (1 - t));
            return Clamp01(eroded * Clamp01(strength));
        }

        // The depth as the R16 value of the map.
        internal static ushort Quantise(double depth01)
        {
            return (ushort)Math.Round(Clamp01(depth01) * Quanta);
        }

        internal static double Dequantise(ushort value)
        {
            return value / (double)Quanta;
        }

        // The slope in degrees of a surface whose normal has the vertical component up (the dot product with the local up, -1 to 1).
        internal static double SlopeDegrees(double normalDotUp)
        {
            return Math.Acos(Clamp(normalDotUp, -1, 1)) * 180.0 / Math.PI;
        }
    }
}
