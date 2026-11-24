using System;

namespace ReDefinition.Atmospheres
{
    // One layer as the cloud motion reads it (E40 MVCLOUD): the radius of its middle sphere, the vertical optical depth of a full column, the drift of its pattern over
    // one frame, and how much of the pixel's view the layer fills (the coverage map's value turned into a share, Presence).
    internal struct MotionLayer
    {
        public double ShellR, TauVertical, DriftX, DriftY, DriftZ, Presence;
    }

    // The cloud motion of the layer pass as a CPU model (the shader ReDefinitionAtmospheresCloudMotion.cginc does the same per pixel): where in the previous frame the cloud
    // seen at a pixel was, taken layer by layer and blended by the share of the pixel's cloud each layer holds. It is the place the history of the reconstruction is read from
    // and the motion vector the upscalers and frame generation get for the cloud (current minus previous viewport position, y up).
    //
    // Why layers: a pixel's cloud is not on the first layer's middle sphere. The cirrus layer at 8 km and the cumulus at 2 km move differently when the camera translates
    // (5 m between two frames at 300 m/s is 0.8 px at 5 km and 2.1 px at 2 km at 1707 x 960 and a 60 degree field), so a vector or a history place taken from one sphere is wrong for
    // the other layer. The share of a layer in a pixel follows the layers in front: a layer that fills the pixel and is thick hides the ones behind it, a thin one lets them through
    // (the stochastic cover of a layer, the opacity of its column, the order along the ray).
    internal static class LayerMotionModel
    {
        internal const int MaxLayers = 4;

        // The cosine below which a ray through a shell is treated as grazing it (the optical depth of the slant path stops growing there).
        internal const double GrazingCosine = 0.05;

        // The share of a pixel a layer fills, from the layer's coverage map value, the layer's coverage and edge softness and its clear-sky rule: the coverage threshold of the density
        // (ReDefinitionAtmospheresLayers.shader LayerDensity: cov + coverage - 1 against the soft edge) without the noise, the type's lift and the height variation.
        internal static double Presence(double cov, double coverage, double edge, double clearSkyK)
        {
            cov = Math.Min(cov, 2.0 * cov - clearSkyK);
            double x = (cov + coverage - 1.0) / Math.Max(edge, 1e-4) + 0.5;
            return Math.Max(0.0, Math.Min(1.0, x));
        }

        // The opacity of a layer's column along a ray that crosses its middle sphere at the given cosine to the radial direction.
        internal static double Opacity(double tauVertical, double cosine)
        {
            return 1.0 - Math.Exp(-tauVertical / Math.Max(Math.Abs(cosine), GrazingCosine));
        }

        // The first positive hit of a ray (origin o, unit direction d, both relative to the planet's centre) with a sphere of radius r; false where it misses.
        internal static bool Hit(double ox, double oy, double oz, double dx, double dy, double dz, double r, out double t)
        {
            t = 0;
            double b = ox * dx + oy * dy + oz * dz;
            double len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            double disc = b * b - (len - r) * (len + r);
            if (disc <= 0) return false;
            double sq = Math.Sqrt(disc);
            double t0 = -b - sq, t1 = -b + sq;
            t = t0 > 0 ? t0 : t1;
            return t > 0;
        }

        // The previous uv (0..1, not limited to the picture) of the cloud seen at the uv of the current camera, and the weighted distance of that cloud along the ray. False where
        // no layer fills the pixel (the caller then takes the first layer's shell, ReconReprojection.PreviousUv) or where the cloud was behind the previous camera.
        // The layers' Presence is the pixel's; planetR cuts the layers a ray meets behind the ground.
        internal static bool Previous(ReconCamera cur, ReconCamera prev, double u, double v, MotionLayer[] layers, int count, double planetR,
                                      out double pu, out double pv, out double depth)
        {
            pu = pv = depth = 0;
            double nx = u * 2 - 1, ny = v * 2 - 1;
            double dx = cur.Fx + nx * cur.Rx * cur.TanX + ny * cur.Ux * cur.TanY;
            double dy = cur.Fy + nx * cur.Ry * cur.TanX + ny * cur.Uy * cur.TanY;
            double dz = cur.Fz + nx * cur.Rz * cur.TanX + ny * cur.Uz * cur.TanY;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= len; dy /= len; dz /= len;
            double tPlanet = double.MaxValue, tp;
            if (Hit(cur.Px, cur.Py, cur.Pz, dx, dy, dz, planetR, out tp)) tPlanet = tp;

            int n = Math.Min(count, MaxLayers);
            var t = new double[MaxLayers];
            var p = new double[MaxLayers];
            var a = new double[MaxLayers];
            var puv = new double[MaxLayers * 2];
            var front = new bool[MaxLayers];
            for (int l = 0; l < n; l++)
            {
                double th;
                if (!Hit(cur.Px, cur.Py, cur.Pz, dx, dy, dz, layers[l].ShellR, out th) || th >= tPlanet) continue;
                double qx = cur.Px + dx * th, qy = cur.Py + dy * th, qz = cur.Pz + dz * th;
                double cosine = (dx * qx + dy * qy + dz * qz) / layers[l].ShellR;
                t[l] = th;
                p[l] = layers[l].Presence;
                a[l] = Opacity(layers[l].TauVertical, cosine);
                double wx = qx - layers[l].DriftX - prev.Px, wy = qy - layers[l].DriftY - prev.Py, wz = qz - layers[l].DriftZ - prev.Pz;
                double f = wx * prev.Fx + wy * prev.Fy + wz * prev.Fz;
                double x = (wx * prev.Rx + wy * prev.Ry + wz * prev.Rz) / prev.TanX, y = (wx * prev.Ux + wy * prev.Uy + wz * prev.Uz) / prev.TanY;
                // Only a place the previous camera saw counts: in front of it and within its view widened by half a picture (the shader's PrevSees, tested without the division).
                front[l] = f > 1e-3 && Math.Abs(x) <= 2.0 * f && Math.Abs(y) <= 2.0 * f;
                if (!front[l]) continue;
                puv[l * 2] = x / f * 0.5 + 0.5;
                puv[l * 2 + 1] = y / f * 0.5 + 0.5;
            }

            double sum = 0, seen = 0, td = 0, su = 0, sv = 0;
            for (int l = 0; l < n; l++)
            {
                double w = p[l];
                if (w <= 0) continue;
                for (int k = 0; k < n; k++)
                    if (k != l && p[k] > 0 && t[k] < t[l]) w *= 1.0 - p[k] * a[k];
                sum += w;
                td += w * t[l];
                if (!front[l]) continue;
                seen += w;
                su += w * puv[l * 2];
                sv += w * puv[l * 2 + 1];
            }
            if (sum < 1e-3 || seen < 0.5 * sum) return false;
            pu = su / seen;
            pv = sv / seen;
            depth = td / sum;
            return true;
        }
    }
}
