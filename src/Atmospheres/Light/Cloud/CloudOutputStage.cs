using System;

namespace ReDefinition.Atmospheres
{
    // The units of the own cloud pass against the scene it is drawn into (K-T2d, start 44: the deck came out about 30 times darker than the sky next to it).
    //
    // Two facts of the scene, one stage each:
    //  1. The pass is radiometric. Its sun colour is an irradiance E and a thick, white, Lambertian top returns E cos(theta) / pi. Unity's legacy lights have no 1/pi:
    //     the same white surface shows E cos(theta) (the surface shader multiplies albedo, light colour and N.L and nothing else), so the light colour of the scene
    //     is E / pi in radiometric terms. The pass is therefore given pi times the scene's sun colour (SunUnit). The harness the light was tuned in uses a sun of
    //     (3.0, 2.9, 2.7) in the same radiometric unit (CloudBridgeAir.HorizonShare, OwnCloudPlan.HarnessSunLuminance). The ambient is not scaled: a sky colour is
    //     already the radiance a white card shows under it.
    //  2. The target is a display-value target. KSP runs in the Gamma colour space, so what the camera target holds is display-encoded, and the scene's white is
    //     the sun's own display value W (a white card facing the sun shows its colour). A radiance r times W, written unencoded, is a linear value in an encoded
    //     target and comes out too dark in every shadowed part (r = 0.1 would show 0.1 W where the eye, and the scene's own gamma-encoded textures, give 0.35 W).
    //     The stage encodes the radiance relative to W: D = W (c * exposure / W) ^ (1 / gamma). A card in the sun (c = W) keeps its value, whatever the gamma.
    // Both are switchable (cloud.sunUnit, cloud.exposure, cloud.outputGamma); the defaults make the pass commensurable with the sky and the sea next to it.
    internal static class CloudOutputStage
    {
        // pi: the factor between the scene's light colour and the radiometric irradiance (fact 1).
        internal const float SunUnitDefault = (float)Math.PI;
        // The exposure of the stage (1: the white card of the sun keeps its display value) and the display gamma of the Gamma colour space (sRGB's 2.2).
        internal const float ExposureDefault = 1f, GammaDefault = 2.2f;
        // A gamma at or below this is the stage off (the shader then writes the radiance as it is).
        internal const float OffGamma = 1.0001f;

        // The light colour the pass is given for the scene's sun colour (light colour times intensity) and the stage's unit.
        internal static float SunForPass(float sceneSun, float sunUnit, float gain)
        {
            return sceneSun * sunUnit * gain;
        }

        // The display value of a radiance c (one channel, premultiplied colour over its coverage already removed), white W, exposure e, gamma g. Mirrors OutputStage in
        // ReDefinitionAtmospheresLayers.shader.
        internal static float Encode(float c, float white, float exposure, float gamma)
        {
            if (gamma <= OffGamma) return c;
            float w = Math.Max(white, 1e-4f);
            float u = Math.Max(c * exposure / w, 0f);
            return w * (float)Math.Pow(u, 1.0 / gamma);
        }

        // The radiance whose display value is p (the inverse of Encode): c = W / e (p / W) ^ gamma.
        internal static float Decode(float p, float white, float exposure, float gamma)
        {
            if (gamma <= OffGamma) return p;
            float w = Math.Max(white, 1e-4f);
            return w / Math.Max(exposure, 1e-4f) * (float)Math.Pow(Math.Max(p, 0f) / w, gamma);
        }

        // The same for a premultiplied colour channel with its alpha (the shader divides the coverage out and puts it back).
        internal static float EncodePremultiplied(float premultiplied, float alpha, float white, float exposure, float gamma)
        {
            if (gamma <= OffGamma) return premultiplied;
            float a = Math.Max(alpha, 1e-4f);
            return Encode(premultiplied / a, white, exposure, gamma) * alpha;
        }

        // What a thick, white, Lambertian top lit by the sun alone shows in the scene's units: the sun colour times the cosine of its angle to the normal. The
        // reference every cloud value is read against (a card facing the sun, cos = 1, shows W).
        internal static float WhiteCard(float sceneSun, float cosTheta)
        {
            return sceneSun * Math.Max(cosTheta, 0f);
        }
    }
}
