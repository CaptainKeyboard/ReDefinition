using System;

namespace ReDefinition.Atmospheres
{
    // The ray-side law of the pack (package T18): the optical depth, the transmission of a light and the opacity of the cloud for a ray that starts anywhere,
    // read from the pack alone. The consumer's HLSL mirrors this file (float instead of double); the numbers below are the contract. Frame: the body's centre
    // at the origin, x towards longitude 0, y towards longitude 90 east, z north (the map frame of CloudShadowField).
    //
    // For each layer: the ray's chord through the layer's shell (LayerGeometry.ShellSpans, the same spans the cloud shadow walks), split into at most MaxTaps
    // taps of at most TapSpacingM; at each tap the pack's tau_v at the tap's place, times the length of the tap's path that lies between the column's base and
    // top (the pack holds both) over the cloud's thickness there: a column's density is tau_v over the cloud's own thickness, taken as uniform across it (the
    // type table's profile is in tau_v, not in the path). The cloud is not spread over the shell: a shell several times thicker than the cloud (the height
    // variation and the type table's heights widen it) would average the clouds of a band as wide as it is thick, in every direction, into each shadow.
    // The light's transmission is exp(-sum of strength x tau), the cloud shadow's direct term (CloudShadowMath.DirectTransmission).
    internal static class RtCloudRayMath
    {
        // The most taps one chord gets, and the length one tap stands for, m.
        internal const int MaxTaps = 8;
        internal const double TapSpacingM = 400.0;

        // The optical depth of one layer along the ray o + t d for t from 0 to tMax, before the layer's strength.
        internal static double LayerTau(RtCloudPack pack, int layer, double ox, double oy, double oz, double dx, double dy, double dz, double tMax)
        {
            return LayerTau(pack, layer, ox, oy, oz, dx, dy, dz, tMax, MaxTaps);
        }

        // The share of a tap's path that lies between a column's base and top: the path climbs from fraction f0 to f1 of the shell, the cloud occupies [baseFraction,
        // topFraction]. A level path (no climb) is inside or out.
        internal static double BandShare(double f0, double f1, double baseFraction, double topFraction)
        {
            double lo = Math.Min(f0, f1), hi = Math.Max(f0, f1), width = hi - lo;
            if (width < 1e-6) return lo >= baseFraction && lo <= topFraction ? 1.0 : 0.0;
            double overlap = Math.Min(hi, topFraction) - Math.Max(lo, baseFraction);
            return overlap <= 0.0 ? 0.0 : overlap / width;
        }

        // The smallest cloud thickness a column is taken to have, as a fraction of the shell: a column whose base and top the filter has pressed together does
        // not get an unbounded density (its share of the path shrinks with it).
        internal const double MinBandFraction = 0.002;

        internal static double LayerTau(RtCloudPack pack, int layer, double ox, double oy, double oz, double dx, double dy, double dz, double tMax, int maxTaps)
        {
            RtCloudLayerEntry e = pack.Entries[layer];
            double thickness = e.ThicknessM;
            if (!(thickness > 0.0)) return 0.0;
            LayerSpan a, b;
            int count = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, pack.BodyRadiusM, e.InnerRadiusM - pack.BodyRadiusM,
                                                 e.OuterRadiusM - pack.BodyRadiusM, tMax, out a, out b);
            double tau = 0.0;
            for (int s = 0; s < count; s++)
            {
                LayerSpan span = s == 0 ? a : b;
                int taps = (int)Math.Max(1.0, Math.Min(maxTaps, Math.Ceiling(span.Length / TapSpacingM)));
                double dt = span.Length / taps;
                for (int k = 0; k < taps; k++)
                {
                    double t0 = span.T0 + dt * k, t = t0 + 0.5 * dt, t1 = t0 + dt;
                    double lat, lon, columnTau, baseFraction, topFraction;
                    LayerGeometry.LatLon(ox + dx * t, oy + dy * t, oz + dz * t, out lat, out lon);
                    pack.Column(layer, lat, lon - e.MapShiftDeg, out columnTau, out baseFraction, out topFraction);
                    if (!(columnTau > 0.0)) continue;                    double f0 = (Radius(ox + dx * t0, oy + dy * t0, oz + dz * t0) - e.InnerRadiusM) / thickness;
                    double f1 = (Radius(ox + dx * t1, oy + dy * t1, oz + dz * t1) - e.InnerRadiusM) / thickness;
                    double inside = BandShare(f0, f1, baseFraction, topFraction);
                    if (inside <= 0.0) continue;
                    // The column's depth over the cloud's own thickness is its density; the path inside the band times it is the tap's depth.
                    tau += columnTau * dt * inside / (Math.Max(MinBandFraction, topFraction - baseFraction) * thickness);
                }
            }
            return tau;
        }

        private static double Radius(double x, double y, double z)
        {
            return Math.Sqrt(x * x + y * y + z * z);
        }

        // All layers, each scaled by its shadow strength (withStrength) or not.
        internal static double PathTau(RtCloudPack pack, double ox, double oy, double oz, double dx, double dy, double dz, double tMax, bool withStrength)
        {
            double tau = 0.0;
            for (int i = 0; i < pack.LayerCount; i++)
            {
                float strength = pack.Entries[i].ShadowStrength;
                if (withStrength && strength <= 0f) continue;
                tau += LayerTau(pack, i, ox, oy, oz, dx, dy, dz, tMax) * (withStrength ? strength : 1f);
            }
            return tau;
        }

        // Where a light's ray from the receiver ends: at the body's sea-level sphere for a sun below the receiver's horizon, else never.
        internal static double LightRayEnd(RtCloudPack pack, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            if (!pack.HasSurface) return double.MaxValue;
            double t0, t1;
            if (LayerGeometry.HitSphere(ox, oy, oz, dx, dy, dz, pack.BodyRadiusM, out t0, out t1) && t0 > 0.0) return t0;
            return double.MaxValue;
        }

        // The share of a light's direct light that reaches the receiver through the pack's layers (the cloud shadow factor of a point for the sun or a moon).
        internal static double LightTransmission(RtCloudPack pack, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            return CloudShadowMath.DirectTransmission(PathTau(pack, ox, oy, oz, dx, dy, dz, LightRayEnd(pack, ox, oy, oz, dx, dy, dz), true));
        }

        // The opacity of the cloud along a ray that leaves the scene (a ray in a reflection, a ray for the sky's occlusion): 1 - exp(-tau), the layers'
        // shadow strength not applied (it is the strength of the shadow, not of the picture), up to tMax (double.MaxValue: to infinity).
        internal static double ViewOpacity(RtCloudPack pack, double ox, double oy, double oz, double dx, double dy, double dz, double tMax)
        {
            double tau = PathTau(pack, ox, oy, oz, dx, dy, dz, tMax, false);
            return tau <= 0.0 ? 0.0 : 1.0 - Math.Exp(-tau);
        }
    }
}
