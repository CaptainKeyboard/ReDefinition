using System;

namespace ReDefinition.Atmospheres
{
    // The CPU twin of what the layer pass draws of ONE layer over a camera frame (E40 Phase O step 1, the oracle K-T2d start 50 asked for: "a model that reflects the shader per
    // type is needed before the model can be a test oracle"). CloudCoverModel.ClosedShare asks about a single column with a uniform shape value; the pass integrates a ray through
    // the layer's shell (a long way at a low elevation), reads the shape volume with the untiling warp, erodes the soft edge with the detail, and steps with the product's march.
    // This marches the same rays with the same functions (CloudShapeModel.Share, LayerTypeTable.Profile, LayerGeometry.ShellSpans) and reports the optical depth of every pixel.
    // Not mirrored: the height variation (T13), the jitter (samples at the middle of the step), the light, the far view's mean, the culling. A constant map and type, or a function
    // of the place, drive it.
    internal static class CloudCoverOracle
    {
        // The march of the product at Medium (LayerSwitches.StepQuality 1): at most MaxSteps samples in a span of at least MinStepM each.
        internal struct March
        {
            internal int MaxSteps;
            internal double MinStepM, Jitter;

            internal static March Medium { get { return new March { MaxSteps = 48, MinStepM = 25.0, Jitter = 0.5 }; } }
        }

        // The camera frame: height above the sea, pitch of the view axis above the horizon, vertical field of view, the aspect and the resolution, and the yaw (the direction the
        // view axis points in the local horizontal plane; it only moves the view over the noise).
        internal struct Frame
        {
            internal double HeightM, PitchDeg, FovVDeg, YawDeg, BodyRadiusM;
            internal int Width, Height;

            // The ground view of K-T2d: 264 m above the sea, 18 degrees up, 60 degrees field of view, 16:9, on Kerbin (600 km).
            internal static Frame Ground(int width = 96, int height = 54, double yawDeg = 0.0)
            {
                return new Frame { HeightM = 264.0, PitchDeg = 18.0, FovVDeg = 60.0, YawDeg = yawDeg, BodyRadiusM = 600000.0, Width = width, Height = height };
            }
        }

        // One pixel: its elevation above the local horizontal (degrees), the optical depth, and whether it lies above the horizon.
        internal sealed class Pixels
        {
            internal double[] ElevationDeg, Tau;
            internal bool[] AboveHorizon;
            internal int Width, Height;

            // The share of the pixels above the horizon whose optical depth is at least tau.
            internal double ShareAtLeast(double tau)
            {
                int n = 0, hit = 0;
                for (int i = 0; i < Tau.Length; i++)
                {
                    if (!AboveHorizon[i]) continue;
                    n++;
                    if (Tau[i] >= tau) hit++;
                }
                return n == 0 ? 0.0 : hit / (double)n;
            }

            // The share of the pixels above the horizon that lie in an elevation band and have at least that optical depth (n counts the band's pixels).
            internal double ShareAtLeast(double tau, double elevationFromDeg, double elevationToDeg, out int n)
            {
                n = 0;
                int hit = 0;
                for (int i = 0; i < Tau.Length; i++)
                {
                    if (!AboveHorizon[i] || ElevationDeg[i] < elevationFromDeg || ElevationDeg[i] >= elevationToDeg) continue;
                    n++;
                    if (Tau[i] >= tau) hit++;
                }
                return n == 0 ? 0.0 : hit / (double)n;
            }
        }

        // The map and the type value at a place (planet-centred metres): a constant, or a function.
        internal delegate void MapAt(double x, double y, double z, out double coverage, out double type);

        internal static MapAt Constant(double coverage, double type)
        {
            return delegate (double x, double y, double z, out double c, out double t) { c = coverage; t = type; };
        }

        // The optical depth of the layer for every pixel of the frame. coverage is the layer's threshold as the pass gets it (EffectiveCoverage). noise: the volumes and the layer's
        // numbers; null: the plain threshold without shape noise (T01's function).
        internal static Pixels Render(CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, Frame frame, MapAt map, double coverage, March march)
        {
            int w = frame.Width, h = frame.Height;
            var px = new Pixels { Width = w, Height = h, ElevationDeg = new double[w * h], Tau = new double[w * h], AboveHorizon = new bool[w * h] };
            double R = frame.BodyRadiusM;
            double ox = 0.0, oy = 0.0, oz = R + frame.HeightM;
            double yaw = frame.YawDeg * Math.PI / 180.0, pitch = frame.PitchDeg * Math.PI / 180.0;
            // The local frame at the camera: up = z, the view axis points along (cos yaw, sin yaw) in the horizontal plane, pitched up.
            double fx = Math.Cos(pitch) * Math.Cos(yaw), fy = Math.Cos(pitch) * Math.Sin(yaw), fz = Math.Sin(pitch);
            double rx = Math.Sin(yaw), ry = -Math.Cos(yaw), rz = 0.0;
            double ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;   // up of the camera = f x r
            double half = Math.Tan(frame.FovVDeg * 0.5 * Math.PI / 180.0), aspect = w / (double)h;
            double dip = Math.Acos(R / (R + Math.Max(0.0, frame.HeightM)));
            float tightInner, tightOuter;
            layer.TightAltitudes(out tightInner, out tightOuter);
            double sigma = layer.EffectiveSigmaPerM, edge = layer.EffectiveEdgeSoftness, amount = layer.EffectiveNoiseAmount;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double sx = ((i + 0.5) / w * 2.0 - 1.0) * half * aspect, sy = (1.0 - (j + 0.5) / h * 2.0) * half;
                    double dx = fx + sx * rx + sy * ux, dy = fy + sx * ry + sy * uy, dz = fz + sx * rz + sy * uz;
                    double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    dx /= len;
                    dy /= len;
                    dz /= len;
                    int k = j * w + i;
                    px.ElevationDeg[k] = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz))) * 180.0 / Math.PI;
                    px.AboveHorizon[k] = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz))) > -dip;
                    // A ray that points below the horizon meets the ground sphere; the pass stops there.
                    double tMax = 1e9, g0, g1;
                    if (LayerGeometry.HitSphere(ox, oy, oz, dx, dy, dz, R, out g0, out g1) && g0 > 0.0) tMax = g0;
                    LayerSpan a, b;
                    int count = LayerGeometry.ShellSpans(ox, oy, oz, dx, dy, dz, R, tightInner, tightOuter, tMax, out a, out b);
                    double tau = 0.0;
                    for (int s = 0; s < count; s++)
                    {
                        LayerSpan span = s == 0 ? a : b;
                        double spanLen = span.Length;
                        double stepLen = Math.Max(march.MinStepM, 1e-3);
                        int n = (int)Math.Max(1.0, Math.Min(march.MaxSteps, Math.Ceiling(spanLen / stepLen)));
                        double dt = spanLen / n;
                        for (int q = 0; q < n; q++)
                        {
                            double t = span.T0 + dt * (q + march.Jitter);
                            double x = ox + dx * t, y = oy + dy * t, z = oz + dz * t;
                            double alt = LayerGeometry.AltitudeOf(x, y, z, R);
                            double share = (alt - layer.InnerAltitudeM) / layer.ThicknessM;
                            if (share < 0.0 || share > 1.0) continue;
                            double cov, typeValue;
                            map(x, y, z, out cov, out typeValue);
                            int ia, ib;
                            double f;
                            table.Neighbours(typeValue, out ia, out ib, out f);
                            double eb, et;
                            table.InterpolatedHeights(typeValue, out eb, out et);
                            if (share < eb || share > et || !(et > eb)) continue;
                            float b0, t0, e0, b1, t1, e1;
                            table.Info(ia, out b0, out t0, out e0);
                            table.Info(ib, out b1, out t1, out e1);
                            double erosion = e0 + (e1 - e0) * f;
                            double s0 = CloudShapeModel.Share(set, ref prm, x, y, z, cov, coverage, edge, amount, erosion, true, dt);
                            if (s0 <= 0.0) continue;
                            tau += sigma * s0 * table.Profile(typeValue, share) * dt;
                        }
                    }
                    px.Tau[k] = tau;
                }
            return px;
        }

        // The optical depth of the layer straight up (pitch 90 degrees) from a camera at a height above the sea, one ray at the site's place of the noise: what the zenith of a frame holds
        // in the aid picture of the pass (aid mode 1). The same march as Render (one pixel, so the rays of the frame do not matter).
        internal static double ZenithTau(CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, double heightM, double bodyRadiusM, MapAt map, double coverage, March march)
        {
            var frame = new Frame { HeightM = heightM, PitchDeg = 90.0, FovVDeg = 1.0, YawDeg = 0.0, BodyRadiusM = bodyRadiusM, Width = 1, Height = 1 };
            return Render(layer, table, set, prm, frame, map, coverage, march).Tau[0];
        }

        // The percentiles (0 to 1 fractions) of the shape value as the pass reads it: the volume looked up at random places through the untiling warp, trilinear.
        internal static double[] ShapeValues(NoiseVolumeSet set, NoiseLayerParams prm, int count, int seed)
        {
            var rng = new Random(seed);
            var values = new double[count];
            double span = NoiseModel.ShapePeriodScales * prm.ScaleM * 4.0;
            for (int i = 0; i < count; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 100.0 * span, y = (rng.NextDouble() - 0.5) * 100.0 * span, z = 600000.0 + (rng.NextDouble() - 0.5) * 20000.0;
                double qx, qy, qz;
                CloudShapeModel.LookupPoint(ref prm, x, y, z, out qx, out qy, out qz);
                values[i] = CloudShapeModel.ShapeAt(set, ref prm, qx, qy, qz);
            }
            Array.Sort(values);
            return values;
        }

        internal static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return double.NaN;
            double x = p * (sorted.Length - 1);
            int i = (int)Math.Floor(x);
            int j = Math.Min(i + 1, sorted.Length - 1);
            return sorted[i] + (sorted[j] - sorted[i]) * (x - i);
        }
    }
}
