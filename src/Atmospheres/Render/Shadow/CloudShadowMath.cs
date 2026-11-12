using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the cloud shadow (package T04): the darkness law, the sun disc's penumbra, the displacement of a shadow and the
    // diffuse light a cloud passes on. The shader (ReDefinitionAtmospheresCloudShadow.shader) mirrors these functions in float.
    //
    // Technique: a cloud shadow is a transmittance of the sun's direct light along the sun ray, T = exp(-tau) with tau the
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

        // The sun's energy that reaches the ground under a slab of vertical optical depth tauV, beam and scattered light together, for the
        // dual-lobe phase function of the reference renderer (g 0.85 and -0.3, forward weight 0.92, albedo 0.999), as a share of the open sky's.
        // The reference is the slab-flux photon walk of tools/weather_metrics/parity_ref.py (slab_sun_transmission, sun at 45 degrees, 400000
        // photons a point, sampling error 0.2 %): 0.9852, 0.8570, 0.5221 and 0.2045 for tau 0.1, 1, 5 and 20 (the same for every seed; the
        // two-stream form above gives 0.9822, 0.8464, 0.5242 and 0.2160 with the asymmetry 0.758 of the phase function, within 6 %).
        // 1 / T - 1 = A tau^P with A 0.168 and P 1.05 follows the four points to 0.3 % (0.9852, 0.8561, 0.5236, 0.2042). The numbers belong
        // to that phase function; the lighting package (T03) owns the phase and may refit them. An earlier fit (A 0.0453, P 1.54) followed the
        // image renderer's ground radiance, whose diffuse estimate lost the multiply scattered light (see tools/weather_metrics/parity_run.py
        // cap_shadow_vs_tau). [VERIFY] for suns other than 45 degrees: the reference was run there only.
        internal const double FitA = 0.168, FitP = 1.05;

        internal static double FittedTransmission(double tauVertical)
        {
            if (tauVertical <= 0.0) return 1.0;
            return 1.0 / (1.0 + FitA * Math.Pow(tauVertical, FitP));
        }

        // The sun's irradiance under a cloud as a share of the open sky's, with the diffuse floor: never below the direct beam along the sun
        // path, never above 1. This is the mask value only where the lighting wants the floor in it (CloudShadowFrame.Floor); the shadow mask
        // itself is the direct beam.
        internal static double TotalTransmission(double tauAlongSun, double tauVertical)
        {
            double direct = DirectTransmission(tauAlongSun);
            double total = FittedTransmission(tauVertical);
            return total < direct ? direct : total > 1.0 ? 1.0 : total;
        }

        // The share of a lit surface's colour that is the sun's direct light, for the forward path (which cannot tell the sun's term from the
        // ambient): sunLuminance x cosine of the sun's zenith angle over that plus the ambient luminance. Unity's diffuse lighting is
        // albedo x (ambient + light colour x intensity x N.L), so a horizontal surface has this share whatever its albedo.
        internal static double DirectShare(double sunLuminance, double cosZenith, double ambientLuminance)
        {
            double sun = Math.Max(0.0, sunLuminance) * Math.Max(0.0, cosZenith);
            double total = sun + Math.Max(0.0, ambientLuminance);
            return total <= 1e-9 ? 1.0 : sun / total;
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
