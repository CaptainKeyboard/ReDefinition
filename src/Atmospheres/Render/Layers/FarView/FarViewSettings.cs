using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The numbers of the far view of the own cloud layers (package T06): the aerial perspective of the clouds (the air between the camera and
    // the cloud: bluer, lower contrast with distance) and the distance LOD of the shape noise. Plain data the caller keeps and rewrites;
    // Upload writes it into the layer pass's material (called from LayerRenderer.Apply). Both parts are off by default, so that a caller that
    // does not know the far view (every other package's rows) sees the pass as it was; the integration (T09) switches them on for the product.
    //
    // The air is the own extinction model of the far view: Rayleigh scattering (a power of the wavelength, so the blue goes first) and a grey
    // aerosol share, each an exponential in height. The defaults are the sea-level coefficients and scale heights of Earth's atmosphere as
    // tabulated by Hillaire, "A Scalable and Production Ready Sky and Atmosphere Rendering Technique", EGSR 2020 (Rayleigh 5.802, 13.558,
    // 33.1 per Mm at 8 km; aerosol extinction 4.44 per Mm at 1.2 km); T09 replaces them with the body's own values and, where Scatterer
    // draws the sky, with Scatterer's. The in-scattered colour of the air is the sky's colour at the horizon (the caller supplies it).
    internal sealed class FarViewSettings
    {
        // Aerial perspective on or off, and a strength factor on the optical depth of the air (1: the model's coefficients).
        public bool Haze;
        public float HazeStrength = 1f;
        // Rayleigh extinction per m at sea level (red, green, blue), aerosol extinction per m (grey), and the two scale heights in m.
        public Vector3 RayleighPerM = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f);
        public float MiePerM = 4.44e-6f, RayleighHeightM = 8000f, MieHeightM = 1200f;
        // The colour of the in-scattered light of the air at the horizon, in the units of the frame's radiance.
        public Color Inscatter = new Color(0.62f, 0.68f, 0.76f);

        // The distance LOD of the shape noise: where the march's sample spacing exceeds the noise's feature size the shape is replaced by its
        // mean over the noise (closed form, FarViewMath.ShapeMean). Spacing in noise scales from which the mean takes over and at which it
        // has taken over; 1 and 3 keep the single sample where the spacing resolves the features.
        public bool ShapeMean;
        public float LodStart = 1f, LodEnd = 3f;

        // The fewest samples the march takes in a span of a layer (0: the march's own count, which is one where the step is longer than the layer):
        // a thin layer seen from far away is sampled at a jittered place, a coin toss of the cloud's coverage that changes with the step count.
        public int MinSamples;

        // The march policy of the far view: through the hand-over band the step budget and the shortest step of the frame follow
        // FarViewMath.MarchQuality (a quality reduction from orbit: off until the owner agrees; the caller applies it with Quality).
        public bool Policy;

        // The finest step of the cloud's entry as a share of the grid step above the band (the frame's own share below it, geometric between); 0: the
        // frame's own share all the way. A finer entry (0.02 against 0.125) changed the far view at 230 km by 0.9 points of coverage (further below
        // the dense march) and 0.8 points of luminance (closer to it), the second sweep: not worth the longer entry walk, so the default leaves the
        // frame's own share.
        public float RefineFraction;

        // Writes the policy's numbers into the frame for a camera altitude over a body of that radius, from the frame's near numbers: the step budget,
        // the shortest step and (where the frame refines the entry) the finest step of the entry. Nothing where the policy is off.
        internal void ApplyTo(LayerFrame frame, double altitudeM, double radiusM)
        {
            if (!Policy) return;
            double bandStart = radiusM * FarViewMath.BandStartRadii, bandEnd = radiusM * FarViewMath.BandEndRadii, minStep;
            int steps;
            FarViewMath.MarchQuality(altitudeM, bandStart, bandEnd, frame.MaxSteps, frame.MinStepM, out steps, out minStep);
            frame.MaxSteps = steps;
            frame.MinStepM = (float)minStep;
            if (frame.RefineBudget > 0f)
                frame.RefineFraction = (float)FarViewMath.GeometricShare(frame.RefineFraction, RefineFraction, 1.0 - FarViewMath.NearWeight(altitudeM, bandStart, bandEnd));
        }

        // The property ids live in a nested class: it is initialised on the first Upload, so that a settings object can be made (and read by the unit
        // tests) without the engine.
        private static class Ids
        {
            internal static readonly int A = Shader.PropertyToID("_FarA"), B = Shader.PropertyToID("_FarB"), C = Shader.PropertyToID("_FarC"),
                D = Shader.PropertyToID("_FarD");
        }

        // The top of the air that counts for the haze, in m above sea level: where the denser of the two species has fallen to a thousandth.
        internal static float AirTop(float rayleighHeightM, float mieHeightM)
        {
            return 7f * Mathf.Max(rayleighHeightM, mieHeightM);
        }

        internal void Upload(Material m)
        {
            m.SetVector(Ids.A, new Vector4(Haze ? 1f : 0f, Mathf.Max(HazeStrength, 0f), Mathf.Max(RayleighHeightM, 1f), Mathf.Max(MieHeightM, 1f)));
            m.SetVector(Ids.B, new Vector4(RayleighPerM.x, RayleighPerM.y, RayleighPerM.z, MiePerM));
            m.SetVector(Ids.C, new Vector4(Inscatter.r, Inscatter.g, Inscatter.b, 0f));
            m.SetVector(Ids.D,new Vector4(ShapeMean ? 1f : 0f, Mathf.Max(LodStart, 0f), Mathf.Max(LodEnd, LodStart + 0.01f), Mathf.Clamp(MinSamples, 0, 16)));
        }
    }
}
