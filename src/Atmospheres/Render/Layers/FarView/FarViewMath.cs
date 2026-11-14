using System;

namespace ReDefinition.Atmospheres
{
    // The pure maths of the far view of the own cloud layers (package T06), in double precision; the shader (ReDefinitionAtmospheresFarView.cginc)
    // is the same arithmetic in float and the tests hold the two to each other. Plain numbers only (no engine types), so the unit tests run
    // without the engine.
    //
    //   Aerial perspective: the optical depth of the air between the camera and a cloud, from the exponential atmosphere along the straight ray
    //   (a sum of short pieces; over each the altitude is taken as linear, for which the integral of an exponential is closed), and the fog
    //   mix of a premultiplied cloud colour with the horizon sky.
    //   Shape mean: the share of a column that is cloud averaged over the shape noise, for a march whose sample spacing is wider than the noise.
    //   Hand-over: the weights of the near march and the far picture through the hand-over band, and the map view's fade.
    //
    // Techniques: the exponential atmosphere and its tabulated coefficients are Bruneton and Neyret, "Precomputed Atmospheric
    // Scattering", EGSR 2008, and Hillaire, "A Scalable and Production Ready Sky and Atmosphere Rendering Technique", EGSR 2020; the fog
    // mix C' = C T + L (1 - T) is the exponential fog of the real-time cloud renderers (Schneider, SIGGRAPH 2015; Hillaire, SIGGRAPH 2016).
    internal static class FarViewMath
    {
        // The integral of exp(-h / H) over a path of length L along which the altitude is linear from h0 to h1 (altitudes in m, never below 0).
        internal static double Chord(double h0, double h1, double lengthM, double scaleHeightM)
        {
            h0 = Math.Max(h0, 0.0);
            h1 = Math.Max(h1, 0.0);
            double dh = h1 - h0;
            if (Math.Abs(dh) < 1e-3 * scaleHeightM) return lengthM * Math.Exp(-0.5 * (h0 + h1) / scaleHeightM);
            return lengthM * scaleHeightM / dh * (Math.Exp(-h0 / scaleHeightM) - Math.Exp(-h1 / scaleHeightM));
        }

        // The optical depth per channel of the air along the ray o + s d from s = 0 to s = t (o relative to the planet's centre, m): the pieces
        // are the stretch inside the top of the air (7 scale heights of the thicker species), cut into Pieces equal parts.
        internal const int Pieces = 6;

        internal static void AirTau(double radius, double ox, double oy, double oz, double dx, double dy, double dz, double t,
                                    double rayR, double rayG, double rayB, double mie, double rayleighHeightM, double mieHeightM, double strength,
                                    out double tauR, out double tauG, out double tauB)
        {
            tauR = tauG = tauB = 0.0;
            if (!(t > 0.0)) return;
            double top = radius + 7.0 * Math.Max(rayleighHeightM, mieHeightM);
            double a0, a1;
            if (!LayerGeometry.HitSphere(ox, oy, oz, dx, dy, dz, top, out a0, out a1)) return;
            double s0 = Math.Max(0.0, a0), s1 = Math.Min(t, a1);
            if (!(s1 > s0)) return;
            double seg = (s1 - s0) / Pieces;
            double hPrev = LayerGeometry.AltitudeOf(ox + dx * s0, oy + dy * s0, oz + dz * s0, radius);
            double sumR = 0.0, sumM = 0.0;
            for (int k = 0; k < Pieces; k++)
            {
                double s = s0 + seg * (k + 1);
                double hNext = LayerGeometry.AltitudeOf(ox + dx * s, oy + dy * s, oz + dz * s, radius);
                sumR += Chord(hPrev, hNext, seg, rayleighHeightM);
                sumM += Chord(hPrev, hNext, seg, mieHeightM);
                hPrev = hNext;
            }
            tauR = (rayR * sumR + mie * sumM) * strength;
            tauG = (rayG * sumR + mie * sumM) * strength;
            tauB = (rayB * sumR + mie * sumM) * strength;
        }

        // The fog mix of a premultiplied cloud colour (rgb, alpha) with the air's in-scattered colour for the transmittance of the air in front:
        // rgb' = rgb T + alpha L (1 - T). The alpha stays (what is behind the cloud is the scene's, with its own air).
        internal static double Fog(double premultipliedChannel, double alpha, double airTransmittance, double inscatterChannel)
        {
            return premultipliedChannel * airTransmittance + alpha * inscatterChannel * (1.0 - airTransmittance);
        }

        // ---- the shape's mean over the noise ----

        // The share of a column that is cloud at a sample of the shape noise value n (0 to 1, uniform over the volume: the volume is rank
        // normalised): the coverage threshold moves by (n - 0.5) * amount, the soft edge turns it into a share, the erosion eats into the edge
        // band with the detail's mean (detailMean) once the detail is dropped, and the edge hardness (hardBase + hardGain * erosion) stiffens it.
        // This is the shader's NoiseShareAt at its coarsest distance LOD.
        internal static double ShapeSample(double cov, double coverage, double edge, double amount, double erosion, double detailMean,
                                           double hardBase, double hardGain, double n)
        {
            double u = (cov + (n - 0.5) * amount + coverage - 1.0) / edge + 0.5;
            return EdgeValue(u, erosion, detailMean, hardBase, hardGain);
        }

        private static double EdgeValue(double u, double erosion, double detailMean, double hardBase, double hardGain)
        {
            double s = u <= 0.0 ? 0.0 : u >= 1.0 ? 1.0 : u;
            if (s <= 0.0 || s >= 1.0 || erosion <= 0.0) return s;
            double er = erosion * (1.0 - detailMean);
            double c = (s - er) / Math.Max(1.0 - er, 1e-3);
            c = c <= 0.0 ? 0.0 : c >= 1.0 ? 1.0 : c;
            double v = c * (hardBase + hardGain * erosion);
            return v <= 0.0 ? 0.0 : v >= 1.0 ? 1.0 : v;
        }

        // The mean of ShapeSample over n uniform in [0, 1], in closed form: the value is a piecewise linear function of u = x0 + a (n - 0.5), so
        // its mean over the uniform u is the difference of an antiderivative. (A step of the closed form: u at or above 1 is a full cloud, not
        // the end of the ramp, which is the shader's early return.)
        // power: the layer's exponent of the share (CloudWater.Pow); the mean is that of the share to the power (the closed form below), not the power of the mean.
        // The shader's FarShareMean returns the mean of the share and the exponent is applied to it afterwards (E40 B1: the residual, MEASURED in FarViewTests).
        internal static double ShapeMean(double cov, double coverage, double edge, double amount, double erosion, double detailMean,
                                         double hardBase, double hardGain, double densityRemap = 0.0, double power = 1.0)
        {
            double mean = ShapeMeanPlain(cov, coverage, edge, amount, erosion, detailMean, hardBase, hardGain, power);
            // layers.densityRemap thins every share by the same factor once the detail is its mean (CloudShapeModel.InteriorThinning).
            return densityRemap > 0.0 && erosion > 0.0 ? mean * CloudShapeModel.InteriorThinning(densityRemap, erosion, detailMean) : mean;
        }

        private static double ShapeMeanPlain(double cov, double coverage, double edge, double amount, double erosion, double detailMean,
                                             double hardBase, double hardGain, double power)
        {
            double x0 = (cov + coverage - 1.0) / edge + 0.5;
            double a = amount / edge;
            if (a < 1e-3) return CloudWater.Pow(EdgeValue(x0, erosion, detailMean, hardBase, hardGain), power);
            double lo = x0 - 0.5 * a, hi = x0 + 0.5 * a;
            double er = erosion > 0.0 ? erosion * (1.0 - detailMean) : 0.0;
            double k = erosion > 0.0 ? hardBase + hardGain * erosion : 1.0;
            double m = Math.Max(k, 1e-6) / Math.Max(1.0 - er, 1e-3);   // the slope of the ramp above u = er
            double u1 = er + 1.0 / m;                                    // where the ramp reaches 1
            double b = Math.Min(hi, 1.0);
            double integral = b > lo ? Antiderivative(b, er, m, u1, power) - Antiderivative(lo, er, m, u1, power) : 0.0;
            double ones = Math.Max(0.0, hi - Math.Max(lo, 1.0));
            return (integral + ones) / (hi - lo);
        }

        // The antiderivative of clamp(m (u - er), 0, 1) to the power p.
        private static double Antiderivative(double u, double er, double m, double u1, double power)
        {
            if (u <= er) return 0.0;
            if (u < u1) return Math.Pow(m, power) * Math.Pow(u - er, power + 1.0) / (power + 1.0);
            return (u1 - er) / (power + 1.0) + (u - u1);
        }

        // The weight of the mean against the single sample for a march spacing foot (m) and the noise scale (m): 0 up to start scales, 1 from
        // end scales, linear between.
        internal static double NoiseLod(double footM, double scaleM, double startScales, double endScales)
        {
            if (!(scaleM > 0.0) || !(footM > 0.0)) return 0.0;
            double x = (footM - startScales * scaleM) / Math.Max((endScales - startScales) * scaleM, 1e-3);
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // ---- hand-over and map view ----

        // The band in which the near march gives way to the far picture, as shares of the body's radius (the camera's altitude over the radius).
        // The shares are the brief's 200 to 260 km on a 600 km body, a starting point per body (the body's own config sets them).
        internal const double BandStartRadii = 1.0 / 3.0, BandEndRadii = 13.0 / 30.0;

        // The weight of the near march at a camera altitude: 1 below the band, 0 above it, smooth between (the far picture has the rest).
        internal static double NearWeight(double altitudeM, double bandStartM, double bandEndM)
        {
            if (!(bandEndM > bandStartM)) return altitudeM < bandStartM ? 1.0 : 0.0;
            double x = (bandEndM - altitudeM) / (bandEndM - bandStartM);
            x = x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
            return x * x * (3.0 - 2.0 * x);
        }

        // The march quality of the far view at a camera altitude (the policy of package T06): the near march's step budget and shortest step
        // through the band, continuous in the altitude. Above the band the budget is half and the shortest step four times: from orbit a pixel
        // spans hundreds of metres and the grid the near march needs for a cloud's edge is wasted. With the policy off the caller keeps its numbers.
        internal const double FarStepShare = 0.5, FarMinStepFactor = 4.0;

        internal static void MarchQuality(double altitudeM, double bandStartM, double bandEndM, int nearMaxSteps, double nearMinStepM,
                                          out int maxSteps, out double minStepM)
        {
            double far = 1.0 - NearWeight(altitudeM, bandStartM, bandEndM);
            maxSteps = Math.Max(1, (int)Math.Round(nearMaxSteps * (1.0 + (FarStepShare - 1.0) * far)));
            minStepM = nearMinStepM * (1.0 + (FarMinStepFactor - 1.0) * far);
        }

        // The coverage of the hand-over: the two renderers draw the same layer, so the picture is the cross-fade w a_near + (1 - w) a_far (a
        // premultiplied sum): equal inputs give the same coverage at every weight.
        internal static double CrossFade(double near, double far, double nearWeight)
        {
            return nearWeight * near + (1.0 - nearWeight) * far;
        }

        // The coverage if the two renderers were simply drawn one over the other with those weights: 1 - (1 - w a)(1 - (1 - w) a). For equal
        // inputs it dips below a by a w (1 - w) a, the visible switch of a stacked hand-over (up to a quarter of the cloud at the middle).
        internal static double StackedFade(double near, double far, double nearWeight)
        {
            return 1.0 - (1.0 - nearWeight * near) * (1.0 - (1.0 - nearWeight) * far);
        }

        // The map view's fade (P69): the far picture is hidden while the map camera is closer to the body than from radii and fully shown from
        // to radii (distances in body radii, smooth between).
        internal static double MapFade(double distanceRadii, double fromRadii, double toRadii)
        {
            if (!(toRadii > fromRadii)) return distanceRadii >= toRadii ? 1.0 : 0.0;
            double x = (distanceRadii - fromRadii) / (toRadii - fromRadii);
            x = x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
            return x * x * (3.0 - 2.0 * x);
        }
    }
}
