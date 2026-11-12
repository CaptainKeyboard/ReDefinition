using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the cloud shadow (package T04): the darkness law, the sun disc's penumbra, the displacement of a shadow and the
    // diffuse light a cloud passes on. The shader (ReDefinitionAtmospheresCloudShadow.shader) mirrors these functions in float.
    //
    // Technique (no code taken): a cloud shadow is a transmittance of the sun's direct light along the sun ray, T = exp(-tau) with tau the
    // optical depth of the clouds on that ray (Beer-Lambert); Hillaire, "Physically Based Sky, Atmosphere and Cloud Rendering in
    // Frostbite", 2016, section 5.9 bakes the same transmittance of the cloud layer along the sun into a 2D map that every surface reads.
    // The sun is a disc of 9.31 mrad (the mean angular diameter, 0.5334 deg), so a hard edge in the sky becomes a penumbra on the ground:
    // the shadow of an edge at height h under a sun at elevation e is blurred over h x 9.31 mrad / sin^2(e) (the slant range h / sin(e)
    // times the disc's width as seen from the ground, stretched by 1 / sin(e) across the ground).
    internal static class CloudShadowMath
    {
        // The sun's angular diameter, radians (tools/weather_metrics/parity_ref.py SUN_ANGULAR_DIAMETER, check number N01).
        internal const double SunAngularDiameter = 9.31e-3;

        // Beer-Lambert: the share of the sun's direct light left after an optical depth along the sun ray.
        internal static double DirectTransmission(double tauAlongSun)
        {
            return tauAlongSun <= 0.0 ? 1.0 : Math.Exp(-tauAlongSun);
        }

        // The horizontal shift of the shadow of a cloud at height h above the receiver for a sun at elevation e (radians): h / tan(e).
        internal static double Displacement(double heightM, double elevationRad)
        {
            return heightM / Math.Tan(elevationRad);
        }

        // The full width of the penumbra of an edge at height h on flat ground, m: h x 9.31 mrad / sin^2(e).
        internal static double PenumbraWidth(double heightM, double elevationRad)
        {
            double s = Math.Sin(elevationRad);
            return heightM * SunAngularDiameter / (s * s);
        }

        // The diffuse transmission of a slab of vertical optical depth tauV, the two-stream result for a scattering that does not absorb:
        // T = 1 / (1 + 3/4 (1 - g) tauV) with the asymmetry g of the dual-lobe phase function (Meador and Weaver 1980; Coakley and Chylek
        // 1975 for the form with the asymmetry). It is the total share of the sun's energy that reaches the ground under a cloud, direct
        // beam included; the diffuse part is that minus the direct beam.
        internal static double TwoStreamTransmission(double tauVertical, double asymmetry)
        {
            if (tauVertical <= 0.0) return 1.0;
            return 1.0 / (1.0 + 0.75 * (1.0 - asymmetry) * tauVertical);
        }

        // The asymmetry of the dual-lobe Henyey-Greenstein phase function (forward g, back g, forward weight): w g1 + (1 - w) g2.
        internal static double Asymmetry(double forwardG, double backG, double forwardWeight)
        {
            return forwardWeight * forwardG + (1.0 - forwardWeight) * backG;
        }

        // The sun's irradiance under a cloud as a share of the open sky's, with the diffuse floor: the direct beam plus the part of the
        // scattered light that comes down (the scattered share 1 - exp(-tauSun) of the beam, a share TwoStreamTransmission of it reaching
        // the ground, the rest sent back up). Never below the direct beam, never above 1.
        internal static double TotalTransmission(double tauAlongSun, double tauVertical, double asymmetry)
        {
            double direct = DirectTransmission(tauAlongSun);
            double total = direct + (1.0 - direct) * TwoStreamTransmission(tauVertical, asymmetry);
            return total < direct ? direct : total > 1.0 ? 1.0 : total;
        }

        // The i-th of n directions across the sun's disc for a sun towards (sx, sy, sz): a Vogel spiral of equal-area cells, so that the mean
        // of a transmission over them is the mean over the disc. (dx, dy, dz) is unit length. n = 1 gives the centre.
        internal static void DiscDirection(double sx, double sy, double sz, int i, int n, double rotation, out double dx, out double dy, out double dz)
        {
            if (n <= 1)
            {
                dx = sx; dy = sy; dz = sz;
                return;
            }
            double r = 0.5 * SunAngularDiameter * Math.Sqrt((i + 0.5) / n);
            double a = i * 2.399963229728653 + rotation;
            // A basis across the sun direction.
            double ux, uy, uz;
            if (Math.Abs(sz) < 0.9) { ux = -sy; uy = sx; uz = 0.0; } else { ux = 0.0; uy = -sz; uz = sy; }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            ux /= ul; uy /= ul; uz /= ul;
            double vx = sy * uz - sz * uy, vy = sz * ux - sx * uz, vz = sx * uy - sy * ux;
            double cu = r * Math.Cos(a), cv = r * Math.Sin(a);
            dx = sx + ux * cu + vx * cv;
            dy = sy + uy * cu + vy * cv;
            dz = sz + uz * cu + vz * cv;
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= l; dy /= l; dz /= l;
        }
    }
}
