using System;

namespace ReDefinition.Raytracing
{
    // How much of the sun's light a body the sun lights sends to the scene, and how bright
    // the raytracing makes it (RtReflectors hands the result to the proxy). Free of
    // Unity and KSP, for the tests.
    //
    // Physically a sphere that scatters as Lambert's law says sends, at the phase angle a
    // (sun, body, observer), the irradiance
    //   E = E_sun at the body * albedo * 2/3 * phase(a) * (R / d)^2.
    // Here phase(a) = (sin a + (pi - a) cos a) / pi, R is its radius and d its distance
    // (the Lambert sphere's phase integral; Russell 1916). For the Mun over Kerbin, full,
    // that is some 6e-5 of the sunlight: too faint to see on a screen without the eye's
    // adaptation. The raytracing takes the square root of the full share, times ten. It eases
    // the result towards 0.3 of the sunlight, so that a planet filling half the sky stays below
    // the sun; the full Mun comes to some 0.06. The unlit part and a shadow then weaken the
    // result in proportion (Lit). The player's percentage scales it.
    internal static class RtReflectorLight
    {
        // The factor on the square root of the share, and the brightness the result eases towards (see above).
        private const double Boost = 10.0;
        private const double Ceiling = 0.3;

        // How much of a disc is left past another disc in front of it: 1 in the open, 0 when covered, between at the edge.
        // It serves the sun seen past a body (eclipse) and a body seen past another. The three arguments are angles, in
        // radians, as seen by the observer: the angular radii of the disc and of the one in front, and the angle between
        // their centres. It is one minus the overlap of the two circles, over the first one's.
        internal static double Uncovered(double radius, double coverRadius, double separation)
        {
            if (radius <= 0.0 || coverRadius <= 0.0 || separation >= radius + coverRadius) return 1.0;
            if (separation <= Math.Abs(coverRadius - radius))
                return coverRadius >= radius ? 0.0 : 1.0 - coverRadius * coverRadius / (radius * radius);
            double a = Math.Max(-1.0, Math.Min(1.0, (separation * separation + radius * radius - coverRadius * coverRadius)
                                                    / (2.0 * separation * radius)));
            double b = Math.Max(-1.0, Math.Min(1.0, (separation * separation + coverRadius * coverRadius - radius * radius)
                                                    / (2.0 * separation * coverRadius)));
            double lens = radius * radius * Math.Acos(a) + coverRadius * coverRadius * Math.Acos(b)
                          - 0.5 * Math.Sqrt(Math.Max(0.0, (-separation + radius + coverRadius) * (separation + radius - coverRadius)
                                                          * (separation - radius + coverRadius) * (separation + radius + coverRadius)));
            return Math.Max(0.0, Math.Min(1.0, 1.0 - lens / (Math.PI * radius * radius)));
        }

        // Lambert's sphere at the phase angle, in radians: 1 full, 0 new.
        internal static double Phase(double angle)
        {
            angle = Math.Max(0.0, Math.Min(Math.PI, angle));
            return (Math.Sin(angle) + (Math.PI - angle) * Math.Cos(angle)) / Math.PI;
        }

        // The share of the sunlight at the observer a fully lit body sends there: albedo, its radius and
        // distance, and the sun's distance from the observer and from the body.
        internal static double Share(double albedo, double radius, double distance,
                                     double sunToObserver, double sunToBody)
        {
            if (distance <= radius || radius <= 0.0 || sunToBody <= 0.0) return 0.0;
            double size = radius / distance;
            double sun = sunToObserver / sunToBody;
            return albedo * (2.0 / 3.0) * size * size * sun * sun;
        }

        // How much of the light a fully lit body sends is left: Lambert's phase at the phase angle, in radians, times the
        // part of the sun the body's shadow leaves it (0 to 1). The brightness is multiplied by it.
        internal static double Lit(double phaseAngle, double sunVisible)
        {
            return Phase(phaseAngle) * Math.Max(0.0, Math.Min(1.0, sunVisible));
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
