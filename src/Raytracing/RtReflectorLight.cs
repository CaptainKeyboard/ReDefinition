using System;

namespace ReDefinition.Raytracing
{
    // How much of the sun's light a body the sun lights sends to the scene, and how bright
    // the raytracing makes it (docs/development/raytracing.md, "Light sources"). Free of
    // Unity and KSP, for the tests.
    //
    // Physically a sphere that scatters as Lambert's law says sends, at the phase angle a
    // (sun, body, observer), the irradiance
    //   E = E_sun at the body * albedo * 2/3 * phase(a) * (R / d)^2.
    // Here phase(a) = (sin a + (pi - a) cos a) / pi, R is its radius and d its distance
    // (the Lambert sphere's phase integral; Russell
    // 1916). For the Mun over Kerbin, full, that is some 6e-5 of the sunlight: too faint
    // to see on a screen without the eye's adaptation. The raytracing takes the square
    // root of that share, times ten. That keeps the order of the bodies, Minmus fainter
    // than the Mun and the Mun's crescent fainter than its full disc, and brings the full
    // Mun to some 0.06 of the sunlight. It also eases the result towards 0.3, so that a planet that
    // fills half the sky from a low orbit, physically some 0.15 already, stays below the
    // sun. The player's percentage scales the result.
    internal static class RtReflectorLight
    {
        // The factor on the square root of the share, and the brightness the result eases towards (see above).
        private const double Boost = 10.0;
        private const double Ceiling = 0.3;

        // Lambert's sphere at the phase angle, in radians: 1 full, 0 new.
        internal static double Phase(double angle)
        {
            angle = Math.Max(0.0, Math.Min(Math.PI, angle));
            return (Math.Sin(angle) + (Math.PI - angle) * Math.Cos(angle)) / Math.PI;
        }

        // The share of the sunlight at the observer the body sends there: albedo, phase
        // angle, its radius and distance, and the sun's distance from the observer and
        // from the body.
        internal static double Share(double albedo, double phaseAngle, double radius, double distance,
                                     double sunToObserver, double sunToBody)
        {
            if (distance <= radius || radius <= 0.0 || sunToBody <= 0.0) return 0.0;
            double size = radius / distance;
            double sun = sunToObserver / sunToBody;
            return albedo * (2.0 / 3.0) * Phase(phaseAngle) * size * size * sun * sun;
        }

        // The raytracing's brightness for that share, in units of the sunlight, at the
        // player's percentage.
        internal static double Brightness(double share, double percent)
        {
            if (share <= 0.0 || percent <= 0.0) return 0.0;
            double boosted = Boost * Math.Sqrt(share);
            return percent / 100.0 * boosted / (1.0 + boosted / Ceiling);
        }
    }
}
