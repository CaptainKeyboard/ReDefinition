using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The share of a view's sky the clouds cover, estimated from the layers' maps (the T09 probe's -t09DrawnCover, T09ProbeDrawn.cs; .local/plans/wolken-m1-fakten.md H: the
    // field's column cover CloudCover01 at the site said 0.52 while the sky view looked into a clear lane). Pure geometry, no game:
    //   - the view's frame is cut into a grid of rays (columns x rows, the centres of equal cells of the picture plane: a pinhole camera of the view's heading, pitch,
    //     vertical field of view and aspect), so each ray stands for the same share of the picture;
    //   - each ray is followed over the sphere to every layer it meets before the ground (CrossingM: the layer's middle height as a shell), and the layer's map is read
    //     where the ray meets it; a layer the camera sits in (between its bottom and top, T09DrawnLayer) is read at the camera's place (distance 0) by every ray;
    //   - the ray's cloud is the union of the layers it meets (1 - the product of the clear shares); rays that meet no layer (the ground or the sea below the horizon
    //     with no layer between) are not sky and are left out.
    // Cover = the mean over the rays that meet a layer (the share of the view's sky that is cloud); LayerFraction = those rays' share of the picture, so Cover x LayerFraction
    // is the estimate of the picture's cloud share (T09.view.<view>.picture_cloud_share is the measured one).
    // What it does not see: the noise inside a layer (the map value is the layer's area share, not the opaque share), the layer's thickness and the slant through it,
    // aerial perspective, terrain and the camera's 100 to 150 m behind the vessel.
    internal static class T09DrawnCover
    {
        // The grids: the estimate after the maps settled, and the forecast that ranks the candidate times (a coarser grid: it is evaluated for every time and heading).
        internal const int Columns = 7, Rows = 9, ForecastColumns = 3, ForecastRows = 4;

        internal struct Ray
        {
            internal double AzimuthDeg, ElevationDeg;
        }

        internal struct Estimate
        {
            // The share of the view's sky that is cloud (NaN where no ray meets a layer or a map could not be read), the share of the picture's rays that meet a layer.
            internal double Cover, LayerFraction;
            internal int Rays, Counted;

            internal double PictureShare { get { return double.IsNaN(Cover) ? double.NaN : Cover * LayerFraction; } }
        }

        // The rays through the centres of a columns x rows grid of the picture plane (azimuth from north, elevation over the local horizon, degrees).
        internal static List<Ray> Rays(double headingDeg, double pitchDeg, double vfovDeg, double aspect, int columns, int rows)
        {
            double h = headingDeg * Math.PI / 180.0, p = pitchDeg * Math.PI / 180.0;
            double tv = Math.Tan(0.5 * vfovDeg * Math.PI / 180.0), th = tv * aspect;
            // Camera axes in east, north, up.
            double fx = Math.Cos(p) * Math.Sin(h), fy = Math.Cos(p) * Math.Cos(h), fz = Math.Sin(p);
            double rx = Math.Cos(h), ry = -Math.Sin(h), rz = 0.0;
            double ux = -Math.Sin(p) * Math.Sin(h), uy = -Math.Sin(p) * Math.Cos(h), uz = Math.Cos(p);
            var rays = new List<Ray>(columns * rows);
            for (int i = 0; i < columns; i++)
            {
                double u = -1.0 + (2.0 * i + 1.0) / columns;
                for (int j = 0; j < rows; j++)
                {
                    double v = -1.0 + (2.0 * j + 1.0) / rows;
                    double dx = fx + u * th * rx + v * tv * ux, dy = fy + u * th * ry + v * tv * uy, dz = fz + u * th * rz + v * tv * uz;
                    double n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    rays.Add(new Ray { AzimuthDeg = Math.Atan2(dx, dy) * 180.0 / Math.PI, ElevationDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dz / n))) * 180.0 / Math.PI });
                }
            }
            return rays;
        }

        // The distance over the surface (metres, at sea level) from the camera's foot to where a straight ray from heightM at elevationDeg meets the shell at layerM, on a sphere
        // of radiusM; NaN where the ray meets the ground (sea level) first or never meets the shell. Heights over sea level.
        internal static double CrossingM(double radiusM, double heightM, double layerM, double elevationDeg)
        {
            double r0 = radiusM + Math.Max(0.0, heightM), r1 = radiusM + layerM;
            double e = elevationDeg * Math.PI / 180.0, se = Math.Sin(e), ce = Math.Cos(e);
            // |p0 + s d|^2 = r^2: s^2 + 2 r0 sin(e) s + r0^2 - r^2 = 0.
            double groundDisc = r0 * r0 * se * se + radiusM * radiusM - r0 * r0;
            double ground = se < 0.0 && groundDisc >= 0.0 ? -r0 * se - Math.Sqrt(groundDisc) : double.PositiveInfinity;
            double disc = r0 * r0 * se * se + r1 * r1 - r0 * r0;
            if (disc < 0.0) return double.NaN;
            double s;
            if (r1 >= r0) s = -r0 * se + Math.Sqrt(disc);
            else
            {
                if (se >= 0.0) return double.NaN;
                s = -r0 * se - Math.Sqrt(disc);
            }
            if (s < 0.0 || s > ground) return double.NaN;
            return radiusM * Math.Atan2(s * ce, r0 + s * se);
        }

        // The place distanceM over the surface from (lat, lon) along the azimuth (great circle, degrees).
        internal static void Destination(double latDeg, double lonDeg, double azimuthDeg, double distanceM, double radiusM, out double lat2, out double lon2)
        {
            double phi = latDeg * Math.PI / 180.0, lambda = lonDeg * Math.PI / 180.0, a = azimuthDeg * Math.PI / 180.0, d = distanceM / radiusM;
            double sp = Math.Sin(phi) * Math.Cos(d) + Math.Cos(phi) * Math.Sin(d) * Math.Cos(a);
            double phi2 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, sp)));
            double lambda2 = lambda + Math.Atan2(Math.Sin(a) * Math.Sin(d) * Math.Cos(phi), Math.Cos(d) - Math.Sin(phi) * sp);
            lat2 = phi2 * 180.0 / Math.PI;
            lon2 = lambda2 * 180.0 / Math.PI;
            while (lon2 > 180.0) lon2 -= 360.0;
            while (lon2 < -180.0) lon2 += 360.0;
        }

        // The estimate of one view: the camera at (lat, lon, heightM over sea level), the rays, the layers' middle heights over sea level and their coverage at a place
        // (coverAt(layer, lat, lon), 0 to 1, NaN where it cannot be read).
        internal static Estimate Of(double latDeg, double lonDeg, double heightM, double radiusM, IList<Ray> rays, IList<T09DrawnLayer> layers, Func<int, double, double, double> coverAt)
        {
            var est = new Estimate { Rays = rays.Count, Cover = double.NaN };
            if (layers == null || layers.Count == 0 || rays.Count == 0) return est;
            double sum = 0.0;
            foreach (Ray ray in rays)
            {
                double clear = 1.0;
                bool met = false;
                for (int l = 0; l < layers.Count; l++)
                {
                    // The camera inside the layer: every ray starts in it, read at the camera's place.
                    bool inside = layers[l].TopM > layers[l].BottomM && heightM >= layers[l].BottomM && heightM <= layers[l].TopM;
                    double d = inside ? 0.0 : CrossingM(radiusM, heightM, layers[l].MidM, ray.ElevationDeg);
                    if (double.IsNaN(d)) continue;
                    double lat2, lon2;
                    Destination(latDeg, lonDeg, ray.AzimuthDeg, d, radiusM, out lat2, out lon2);
                    double c = coverAt(l, lat2, lon2);
                    if (double.IsNaN(c)) return est;
                    clear *= 1.0 - Math.Max(0.0, Math.Min(1.0, c));
                    met = true;
                }
                if (!met) continue;
                sum += 1.0 - clear;
                est.Counted++;
            }
            est.LayerFraction = (double)est.Counted / rays.Count;
            est.Cover = est.Counted > 0 ? sum / est.Counted : double.NaN;
            return est;
        }

        // Whether every required view shows at least min (a NaN fails), and how far the views lie from the target (the mean distance over the required ones; lower is better).
        internal static bool Qualifies(double[] covers, bool[] required, double min)
        {
            for (int v = 0; v < covers.Length; v++)
                if (required[v] && !(covers[v] >= min)) return false;
            return true;
        }

        internal static double Distance(double[] covers, bool[] required, double target, double min)
        {
            double sum = 0.0;
            int n = 0;
            for (int v = 0; v < covers.Length; v++)
            {
                if (!required[v]) continue;
                if (double.IsNaN(covers[v])) return double.PositiveInfinity;
                // Below the minimum counts twice: a forecast that misses it ranks behind one that holds it at the same distance.
                sum += Math.Abs(covers[v] - target) + Math.Max(0.0, min - covers[v]);
                n++;
            }
            return n > 0 ? sum / n : double.PositiveInfinity;
        }
    }
}
