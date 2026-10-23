using System;

namespace ReDefinition.Raytracing
{
    // The one light EVE's raymarched clouds take (Scatterer's _Sun_WorldSunDir and
    // _sunColor), made from the sun and a moon as the sun sets and rises
    // (EveCloudMoonlight). Free of Unity and KSP, for the tests.
    //
    // With the moon's light added per pixel (EveCloudRelight) the sun stays the clouds'
    // light, each cloud in its own sunset as Scatterer shades it, until it reaches none in
    // view; the light then turns to the moon (Handoff) as the per-pixel light gives way.
    // Without it, as follows.
    //
    // A cloud at height h over a planet of radius R sees the sun until it sinks
    // acos(R / (R + h)) under the horizon of the ground below (Dip): on Kerbin some 10
    // degrees for the top of a layer at 10 km. The clouds farther off towards the sunset
    // see it longer, by the angle between their place and the camera's from the planet's
    // centre; the farthest in view, on the horizon of the layer's top, by that dip again
    // (FarthestDip). Down to the top's dip the sun alone lights them, as without this, and
    // the clouds overhead that it no longer reaches go dark as they would; from the
    // farthest clouds' dip on the moon alone; in between the sun's share eases from 1 to 0
    // (Reach) -- so that the clouds still in the sunset keep its light (the player,
    // 2026-10-23: the moon's light on them at 9 degrees under the horizon was wrong).
    // Each light counts by its brightness times its share: the light comes from their
    // weighted mean direction and in the sum of their colours, so that it moves from the
    // sun to the moon while both are faint, instead of switching.
    internal static class CloudLightBlend
    {
        // Where the layer is too thin for a window of its own: so wide a one, in radians.
        private const double NarrowestWindow = 2.0 * Math.PI / 180.0;

        // How far under the horizon of the ground below, in radians, the sun sinks before
        // it leaves a cloud `radius` from the centre of a planet of `planetRadius`; 0 at or
        // below the ground.
        internal static double Dip(double planetRadius, double radius)
        {
            if (planetRadius <= 0.0 || radius <= planetRadius) return 0.0;
            return Math.Acos(planetRadius / radius);
        }

        // How far under the horizon of the ground below the sun sinks before it leaves the
        // farthest clouds in view, given the dip of the layer's top and that of the camera's
        // own height (0 on the ground): those clouds stand where the line of sight that
        // grazes the ground meets the top, as far round the planet as the two dips together,
        // and see the sun so much longer, by the top's dip again.
        internal static double FarthestDip(double topDip, double cameraDip = 0.0)
        {
            return 2.0 * Math.Max(topDip, 0.0) + Math.Max(cameraDip, 0.0);
        }

        // From how high a sun, in radians over the camera's horizon, the moon's light per
        // pixel eases in as the sun goes down: by day the sun's outshines it on every cloud.
        internal const double DuskStart = 5.0 * Math.PI / 180.0;

        // The share of the moon's light per pixel by the sun's elevation over the camera's
        // horizon, in radians: 0 from DuskStart up, 1 from the horizon down, smooth between.
        internal static double Dusk(double elevation)
        {
            double t = 1.0 - elevation / DuskStart;
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // Over how many radians past FarthestDip the clouds' one light turns to the moon,
        // where the moon's light is added per pixel until then (EveCloudRelight).
        internal const double HandoffWindow = 2.0 * Math.PI / 180.0;

        // The moon's share of the clouds' one light at the sun's elevation over the camera's
        // horizon, in radians, with the moon's light added per pixel: 0 while the sun still
        // reaches a cloud in view (down to -farthest), 1 from HandoffWindow further on, smooth
        // in between. The sun's light no longer reaches any cloud then, so the light can take
        // the moon's direction at once and only its strength eases in.
        internal static double Handoff(double elevation, double farthest)
        {
            double t = (-elevation - Math.Max(farthest, 0.0)) / HandoffWindow;
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // The sun's share of the clouds' light at its elevation over the ground's horizon,
        // in radians: 1 down to -lowest, 0 from -highest on, smooth in between.
        internal static double Reach(double elevation, double lowest, double highest)
        {
            if (highest < lowest + NarrowestWindow) highest = lowest + NarrowestWindow;
            double t = (elevation + highest) / (highest - lowest);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        // The direction the light comes from, unit length, into `towards`: the sun's and
        // the moon's (unit length each) weighted by brightness times share, `reach` the
        // sun's. The sun's where neither has any weight, or where they cancel.
        internal static void Direction(double[] sun, double sunBrightness, double[] moon, double moonBrightness,
                                       double reach, double[] towards)
        {
            double sunWeight = Math.Max(sunBrightness, 0.0) * reach;
            double moonWeight = Math.Max(moonBrightness, 0.0) * (1.0 - reach);
            double x = sunWeight * sun[0] + moonWeight * moon[0];
            double y = sunWeight * sun[1] + moonWeight * moon[1];
            double z = sunWeight * sun[2] + moonWeight * moon[2];
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-9 * (sunWeight + moonWeight) || length <= 0.0)
            {
                double[] from = moonWeight > sunWeight ? moon : sun;
                towards[0] = from[0];
                towards[1] = from[1];
                towards[2] = from[2];
                return;
            }
            towards[0] = x / length;
            towards[1] = y / length;
            towards[2] = z / length;
        }
    }
}
