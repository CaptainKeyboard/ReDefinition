using System;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 COVER-MEANING prototype (design note .local/plans/atmospheres/E40-cover-meaning-design.md): the CPU model of the ways to turn the layer's coverage map value c and the shape
    // noise value n into the share s of a column that is cloud.
    //
    //   Current     : s = sat((c + (n - 0.5) A + C - 1) / e + 0.5)   A noise amount, C the layer's coverage constant, e the edge softness. The closed share is a function of A, C, e and
    //                                                                 (through sigma s H) of the column's optical depth, not of c alone.
    //   Meaning     : s = sat((n - (1 - c')) / w)                    c' = c + shift, w the ramp width in noise space; n taken as uniform (NoiseVolumes.Shape ranks its volume).
    //   MeaningRank : the same on u = F(n), F the CDF of the shape value AS THE PASS READS IT (trilinear, warped lookup): a 1D table, so that u is uniform over the places. The ramp
    //                 starts at the cloud boundary, so the columns with s >= sStar (the share that makes tau >= 0.92) are the share c' - w sStar of the places: c' to within w sStar.
    //   AmountShare : MeaningRank with the noise amount A as the share of the density the noise controls: s = (1 - A) [u >= 1 - c'] + A sat(...). The cover is the same for every A.
    //
    // Test code only: nothing in src/ calls it. The src/ side (CloudShapeModel.Share, the shader's NoiseShareAt, FarViewMath.ShapeSample) changes in a later package; this file is that
    // package's reference and its parity oracle.
    internal enum CoverMapping { Current, Meaning, MeaningRank, AmountShare }

    // The CDF of the shape value as the pass reads it, as a table of Knots + 1 quantiles (the value at rank i / Knots), linear between them: what a 33-float array in the shader holds.
    internal sealed class CoverCdf
    {
        internal readonly double[] Quantile;

        internal CoverCdf(double[] quantile)
        {
            Quantile = quantile;
        }

        internal int Knots { get { return Quantile.Length - 1; } }

        // The rank (0 to 1) of a shape value: the inverse of the quantile table.
        internal double Rank(double n)
        {
            int k = Knots;
            if (n <= Quantile[0]) return 0.0;
            if (n >= Quantile[k]) return 1.0;
            int lo = 0, hi = k;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (Quantile[mid] <= n) lo = mid; else hi = mid;
            }
            double span = Quantile[hi] - Quantile[lo];
            return (lo + (span > 1e-12 ? (n - Quantile[lo]) / span : 0.0)) / k;
        }

        // The table of the volume: the shape value read (trilinear) at places spread evenly over one period of the lattice, sorted, sampled at the knots. It belongs to the VOLUME, not to a
        // layer: the lookup's offset and warp only move the place inside the periodic volume. (CloudCoverOracle.ShapeValues reads one horizontal plane near the planet's pole: one slice of the
        // volume, whose histogram is not the volume's.)
        internal static CoverCdf Of(NoiseVolumeSet set, NoiseLayerParams prm, int knots, int samples, int seed)
        {
            var rng = new Random(seed);
            double period = NoiseModel.ShapePeriodScales * prm.ScaleM;
            var sorted = new double[samples];
            for (int i = 0; i < samples; i++)
                sorted[i] = CloudShapeModel.ShapeAt(set, ref prm, rng.NextDouble() * period, rng.NextDouble() * period, rng.NextDouble() * period);
            Array.Sort(sorted);
            var q = new double[knots + 1];
            for (int i = 0; i <= knots; i++) q[i] = CloudCoverOracle.Percentile(sorted, i / (double)knots);
            return new CoverCdf(q);
        }
    }

    internal struct CoverNumbers
    {
        internal double Sigma, Edge, Amount, Coverage, Shift;
        // The rank table of the noise (MeaningRank, AmountShare); null: n is taken as uniform.
        internal CoverCdf Cdf;

        // The numbers of a layer as the pass uploads them (EffectiveCoverage includes the shift of the tuning keys).
        internal static CoverNumbers Of(CloudLayerDef layer)
        {
            return new CoverNumbers
            {
                Sigma = layer.EffectiveSigmaPerM,
                Edge = layer.EffectiveEdgeSoftness,
                Amount = layer.EffectiveNoiseAmount,
                Coverage = layer.EffectiveCoverage,
                Shift = 0.0
            };
        }
    }

    internal static class CoverMeaningModel
    {
        internal static double Sat(double x)
        {
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // The share of the column before erosion, for the noise value n and the map value c.
        internal static double BaseShare(CoverMapping mapping, double n, double c, CoverNumbers k)
        {
            double edge = Math.Max(k.Edge, 1e-4);
            double cc = Math.Max(0.0, Math.Min(1.0, c + k.Shift));
            switch (mapping)
            {
                case CoverMapping.Current:
                    return Sat((c + (n - 0.5) * k.Amount + k.Coverage - 1.0) / edge + 0.5);
                case CoverMapping.Meaning:
                    return Sat((n - 1.0 + cc) / edge);
                case CoverMapping.MeaningRank:
                    return Sat(((k.Cdf == null ? n : k.Cdf.Rank(n)) - 1.0 + cc) / edge);
                default:
                {
                    double u = k.Cdf == null ? n : k.Cdf.Rank(n);
                    double a = Math.Max(0.0, Math.Min(1.0, k.Amount));
                    double step = u >= 1.0 - cc && cc > 0.0 ? 1.0 : 0.0;
                    return (1.0 - a) * step + a * Sat((u - 1.0 + cc) / edge);
                }
            }
        }

        // CloudShapeModel.Share with the base share replaced (the lookup, the detail, the erosion and the hardness are the product's).
        internal static double Share(CoverMapping mapping, NoiseVolumeSet set, ref NoiseLayerParams prm, double px, double py, double pz, double c, CoverNumbers k, double erosion,
                                     bool fine, double footM)
        {
            double qx, qy, qz;
            CloudShapeModel.LookupPoint(ref prm, px, py, pz, out qx, out qy, out qz);
            double n = CloudShapeModel.ShapeAt(set, ref prm, qx, qy, qz);
            double s = BaseShare(mapping, n, c, k);
            if (s <= 0.0 || erosion <= 0.0 || !fine) return s;
            if (s >= 1.0 && !(prm.DensityRemap > 0.0)) return s;
            double lod = CloudShapeModel.DetailLod(ref prm, footM);
            double d = set.DetailMean;
            double r = Math.Sqrt(px * px + py * py + pz * pz);
            if (lod < 1.0)
            {
                d = CloudShapeModel.DetailAt(set, ref prm, qx, qy, qz, px / r, py / r, pz / r);
                if (lod > 0.0) d += (set.DetailMean - d) * lod;
            }
            if (!prm.NoFineDetail)
            {
                double lod2 = CloudShapeModel.DetailLod(ref prm, footM, NoiseModel.FineDetailFrequency);
                double d2 = set.DetailMean;
                if (lod2 < 1.0)
                {
                    d2 = CloudShapeModel.DetailAt(set, ref prm, qx, qy, qz, px / r, py / r, pz / r, NoiseModel.FineDetailFrequency);
                    if (lod2 > 0.0) d2 += (set.DetailMean - d2) * lod2;
                }
                d += (d2 - d) * NoiseModel.FineDetailWeight;
            }
            double eroded = CloudShapeModel.Erode(s, d, erosion);
            if (prm.DensityRemap > 0.0) eroded *= CloudShapeModel.InteriorThinning(prm.DensityRemap, erosion, d);
            return eroded;
        }

        // The optical depth of a vertical column through the layer at the horizontal place (hx, hy) (metres from the noise site's origin on the tangent plane; the point is put on the
        // sphere at each altitude so the altitude is exact), for a constant type value; eroded: with the detail erosion (the product) or the base share alone.
        internal static double ColumnTau(CoverMapping mapping, CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, CoverNumbers k, double hx, double hy,
                                         double c, double typeValue, bool eroded, int steps = 48)
        {
            double R = 600000.0;
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            if (!(et > eb)) return 0.0;
            int ia, ib;
            double f;
            table.Neighbours(typeValue, out ia, out ib, out f);
            float b0, t0, e0, b1, t1, e1;
            table.Info(ia, out b0, out t0, out e0);
            table.Info(ib, out b1, out t1, out e1);
            double erosion = eroded ? e0 + (e1 - e0) * f : 0.0;
            double span = (et - eb) * layer.ThicknessM, dz = span / steps, tau = 0.0;
            for (int q = 0; q < steps; q++)
            {
                double share = eb + (et - eb) * (q + 0.5) / steps;
                double alt = layer.InnerAltitudeM + share * layer.ThicknessM;
                double rr = R + alt, zz = Math.Sqrt(Math.Max(1.0, rr * rr - hx * hx - hy * hy));
                double s = Share(mapping, set, ref prm, hx, hy, zz, c, k, erosion, eroded, dz);
                if (s <= 0.0) continue;
                tau += k.Sigma * s * table.Profile(typeValue, share) * dz;
            }
            return tau;
        }

        // The place of the noise of a site (m): irrational multiples, so that no two sites share a phase of the noise's period (250 km and 130 km steps, as the oracle's tests use them, are
        // multiples of the 8 km period of a 500 m layer: only four distinct phases).
        internal static void SiteOffset(int site, out double x, out double y)
        {
            x = site * 75487.76662;
            y = site * 56984.02909;
        }

        // Moves the noise lattice to a site: the three offsets (a place on the planet cuts the periodic volume at its own height as well as its own longitude and latitude: a patch at one
        // radius sees one slice of the volume, so sites that only moved sideways would all see the same slice).
        internal static void Place(ref NoiseLayerParams prm, int site)
        {
            // The sites are a Kronecker sequence over one period of the periodic volume (the plastic-number constants), so that n sites cover the volume's three phases evenly: a patch of the
            // planet sees ONE slice of the volume, and a volume has only a handful of independent slices per period; random offsets leave the mean of 60 sites 0.05 to 0.10 off.
            double period = NoiseModel.ShapePeriodScales * prm.ScaleM;
            prm.OffsetX += Frac(site * 0.8191725134 + 0.1) * period;
            prm.OffsetY += Frac(site * 0.6710436067 + 0.2) * period;
            prm.OffsetZ += Frac(site * 0.5497004779 + 0.3) * period;
        }

        private static double Frac(double x)
        {
            return x - Math.Floor(x);
        }

        // The closed share of a site: the share of a grid of columns (cells x cells, spacing spacingM) with tau >= tauClosed. The site moves the noise lattice (Place).
        internal static double SiteClosedShare(CoverMapping mapping, CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, CoverNumbers k, double c,
                                               double typeValue, int site, int cells, double spacingM, bool eroded, double tauClosed = 0.92)
        {
            Place(ref prm, site);
            int hit = 0;
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                    if (ColumnTau(mapping, layer, table, set, prm, k, (i - cells / 2) * spacingM, (j - cells / 2) * spacingM, c, typeValue, eroded) >= tauClosed) hit++;
            return hit / (double)(cells * cells);
        }

        // The frame (ground view, as CloudCoverOracle.Render) with the mapping chosen: the share of the pixels above the horizon whose tau is at least tauClosed.
        internal static double FrameClosedShare(CoverMapping mapping, CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, CoverNumbers k, double c,
                                                double typeValue, CloudCoverOracle.Frame frame, double tauClosed = 0.92, double elevFromDeg = -90.0, double elevToDeg = 90.0)
        {
            CloudCoverOracle.March march = CloudCoverOracle.March.Medium;
            int w = frame.Width, h = frame.Height;
            double R = frame.BodyRadiusM;
            double ox = 0.0, oy = 0.0, oz = R + frame.HeightM;
            double yaw = frame.YawDeg * Math.PI / 180.0, pitch = frame.PitchDeg * Math.PI / 180.0;
            double fx = Math.Cos(pitch) * Math.Cos(yaw), fy = Math.Cos(pitch) * Math.Sin(yaw), fz = Math.Sin(pitch);
            double rx = Math.Sin(yaw), ry = -Math.Cos(yaw), rz = 0.0;
            double ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;
            double half = Math.Tan(frame.FovVDeg * 0.5 * Math.PI / 180.0), aspect = w / (double)h;
            double dip = Math.Acos(R / (R + Math.Max(0.0, frame.HeightM)));
            float tightInner, tightOuter;
            layer.TightAltitudes(out tightInner, out tightOuter);
            int ia, ib;
            double f;
            table.Neighbours(typeValue, out ia, out ib, out f);
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            float b0, t0, e0, b1, t1, e1;
            table.Info(ia, out b0, out t0, out e0);
            table.Info(ib, out b1, out t1, out e1);
            double erosion = e0 + (e1 - e0) * f;
            int n = 0, hit = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double sx = ((i + 0.5) / w * 2.0 - 1.0) * half * aspect, sy = (1.0 - (j + 0.5) / h * 2.0) * half;
                    double dx = fx + sx * rx + sy * ux, dy = fy + sx * ry + sy * uy, dz = fz + sx * rz + sy * uz;
                    double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    dx /= len;
                    dy /= len;
                    dz /= len;
                    if (!(Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz))) > -dip)) continue;
                    double elev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz))) * 180.0 / Math.PI;
                    if (elev < elevFromDeg || elev >= elevToDeg) continue;
                    n++;
                    double tMax = 1e9, g0, g1;
                    if (LayerGeometry.HitSphere(ox, oy, oz, dx, dy, dz, R, out g0, out g1) && g0 > 0.0) tMax = g0;
                    LayerSpan a, b;
                    int count = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, R, tightInner, tightOuter, tMax, out a, out b);
                    double tau = 0.0;
                    for (int s = 0; s < count; s++)
                    {
                        LayerSpan span = s == 0 ? a : b;
                        int steps = (int)Math.Max(1.0, Math.Min(march.MaxSteps, Math.Ceiling(span.Length / march.MinStepM)));
                        double dt = span.Length / steps;
                        for (int q = 0; q < steps; q++)
                        {
                            double t = span.T0 + dt * (q + march.Jitter);
                            double x = ox + dx * t, y = oy + dy * t, z = oz + dz * t;
                            double share = (LayerGeometry.AltitudeOf(x, y, z, R) - layer.InnerAltitudeM) / layer.ThicknessM;
                            if (share < eb || share > et || !(et > eb)) continue;
                            double sh = Share(mapping, set, ref prm, x, y, z, c, k, erosion, true, dt);
                            if (sh <= 0.0) continue;
                            tau += k.Sigma * sh * table.Profile(typeValue, share) * dt;
                        }
                    }
                    if (tau >= tauClosed) hit++;
                }
            return n == 0 ? 0.0 : hit / (double)n;
        }
    }
}
