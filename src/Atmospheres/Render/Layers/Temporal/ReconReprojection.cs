using System;

namespace ReDefinition.Atmospheres
{
    // A camera of the reconstruction in the planet frame: position (relative to the planet's centre) and the unit basis, with the tangents of
    // the half field of view. Plain doubles so that the tests can run it.
    internal struct ReconCamera
    {
        public double Px, Py, Pz, Fx, Fy, Fz, Rx, Ry, Rz, Ux, Uy, Uz, TanX, TanY;
    }

    // The reprojection of the temporal reconstruction as a CPU model (the shader ReDefinitionAtmospheresReconstruct.shader does the same per
    // pixel): where in the previous frame the cloud that is now seen at a pixel was.
    internal static class ReconReprojection
    {
        // The previous uv (0..1) of the cloud at the uv of the current camera. The cloud is taken on the sphere of radius shellR around the
        // planet's centre (fallbackM along the ray when the ray misses it), moved back by the drift of one frame, and seen from the previous
        // camera. False where the point is behind the previous camera or outside its picture.
        internal static bool PreviousUv(ReconCamera cur, ReconCamera prev, double u, double v, double driftX, double driftY, double driftZ,
                                        double shellR, double fallbackM, out double pu, out double pv)
        {
            pu = pv = 0;
            double nx = u * 2 - 1, ny = v * 2 - 1;
            double dx = cur.Fx + nx * cur.Rx * cur.TanX + ny * cur.Ux * cur.TanY;
            double dy = cur.Fy + nx * cur.Ry * cur.TanX + ny * cur.Uy * cur.TanY;
            double dz = cur.Fz + nx * cur.Rz * cur.TanX + ny * cur.Uz * cur.TanY;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= len; dy /= len; dz /= len;
            double b = cur.Px * dx + cur.Py * dy + cur.Pz * dz;
            double c = cur.Px * cur.Px + cur.Py * cur.Py + cur.Pz * cur.Pz - shellR * shellR;
            double disc = b * b - c;
            double t = fallbackM;
            if (disc > 0)
            {
                double sq = Math.Sqrt(disc);
                double t0 = -b - sq, t1 = -b + sq;
                t = t0 > 0 ? t0 : (t1 > 0 ? t1 : fallbackM);
            }
            double vx = cur.Px + dx * t - driftX - prev.Px;
            double vy = cur.Py + dy * t - driftY - prev.Py;
            double vz = cur.Pz + dz * t - driftZ - prev.Pz;
            double f = vx * prev.Fx + vy * prev.Fy + vz * prev.Fz;
            if (f <= 1e-3) return false;
            pu = (vx * prev.Rx + vy * prev.Ry + vz * prev.Rz) / (f * prev.TanX) * 0.5 + 0.5;
            pv = (vx * prev.Ux + vy * prev.Uy + vz * prev.Uz) / (f * prev.TanY) * 0.5 + 0.5;
            return pu > 0 && pu < 1 && pv > 0 && pv < 1;
        }

        // The variance clip of one channel: the history value limited to the mean +- sigmas standard deviations of the new samples.
        internal static double Clip(double history, double mean, double sd, double sigmas)
        {
            return Math.Max(mean - sigmas * sd, Math.Min(mean + sigmas * sd, history));
        }
    }
}
