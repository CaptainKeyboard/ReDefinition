using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // Kerbin's four layers as GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg has them (the rain deck, cumulus, cumulonimbus, cirrus), for the tests of the cloud look.
    internal static class CloudLookFixtures
    {
        private static Curve C(params float[] xy)
        {
            var x = new float[xy.Length / 2];
            var y = new float[xy.Length / 2];
            for (int i = 0; i < x.Length; i++) { x[i] = xy[2 * i]; y[i] = xy[2 * i + 1]; }
            return new Curve(x, y);
        }

        private static CloudLayerDef Layer(string name, string group, float inner, float outer, float sigma, float coverage, float edge, float noise, float scaleM, LayerKind kind,
                                           params CloudTypeDef[] types)
        {
            return new CloudLayerDef(name, 1, inner, outer, types, sigma, kind) { Group = group, Coverage = coverage, EdgeSoftness = edge, NoiseAmount = noise, NoiseScaleM = scaleM };
        }

        internal static CloudLayerDef Rain()
        {
            CloudLayerDef rain = Layer("Kerbin-weather-rain", CloudCoverShift.RainGroup, 0f, 2100f, 0.045f, 0.5f, 0.2f, 0.3f, 10000f, LayerKind.Weather,
                new CloudTypeDef("Fog", 0f, 0.1f, C(0f, 0.9f, 0.7f, 0.5f, 1f, 0f), 0.2f),
                new CloudTypeDef("Stratus", 0.1f, 0.5f, C(0f, 0f, 0.12f, 0.8f, 0.5f, 1f, 0.85f, 0.7f, 1f, 0f), 0.15f),
                new CloudTypeDef("Nimbostratus", 0.05f, 1f, C(0f, 0.1f, 0.08f, 0.9f, 0.6f, 1f, 0.9f, 0.6f, 1f, 0f), 0.2f));
            rain.Deck = new WeatherDeckRule { RainFloor = 0.9f };
            // The soft water content's numbers of the cfg (E40 soft density): a softer, flatter deck.
            rain.SoftnessScale = 1.5f;
            foreach (CloudTypeDef t in rain.Types) t.BaseGain = 0.7f;
            return rain;
        }

        internal static CloudLayerDef Cumulus()
        {
            return Layer("Kerbin-cumulus", "", 1700f, 7400f, 0.05f, 0.35f, 0.15f, 1f, 500f, LayerKind.Weather,
                new CloudTypeDef("Stratocumulus", 0f, 0.11f, C(0f, 0f, 0.15f, 0.9f, 0.6f, 1f, 1f, 0f), 0.3f),
                new CloudTypeDef("Cumulus humilis", 0f, 0.21f, C(0f, 0f, 0.08f, 0.9f, 0.55f, 1f, 0.9f, 0.5f, 1f, 0f), 0.45f),
                new CloudTypeDef("Cumulus mediocris", 0f, 0.47f, C(0f, 0f, 0.06f, 0.9f, 0.5f, 1f, 0.85f, 0.6f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulus congestus", 0f, 1f, C(0f, 0f, 0.04f, 0.9f, 0.45f, 1f, 0.8f, 0.7f, 1f, 0f), 0.55f),
                new CloudTypeDef("Altocumulus", 0.54f, 0.68f, C(0f, 0f, 0.2f, 0.9f, 0.7f, 0.8f, 1f, 0f), 0.4f));
        }

        internal static CloudLayerDef Cumulonimbus()
        {
            CloudLayerDef cb = Layer("Kerbin-cumulonimbus", "", 1700f, 11000f, 0.08f, 0.12f, 0.1f, 0.4f, 8000f, LayerKind.Weather,
                new CloudTypeDef("Cumulonimbus calvus", 0f, 0.7f, C(0f, 0.2f, 0.05f, 0.9f, 0.5f, 1f, 0.85f, 0.7f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulonimbus incus", 0f, 1f, C(0f, 0.2f, 0.05f, 0.8f, 0.4f, 0.9f, 0.7f, 1f, 0.88f, 0.8f, 1f, 0f), 0.5f));
            cb.Deck = new WeatherDeckRule { Fog = false, PrecipType = 0f };
            foreach (CloudTypeDef t in cb.Types) t.RiseShare = 0.3f;
            return cb;
        }

        internal static CloudLayerDef Cirrus()
        {
            CloudLayerDef cirrus = Layer("Kerbin-cirrus", "", 9000f, 11000f, 0.0002f, 0.3f, 0.35f, 0.5f, 15000f, LayerKind.Cheap,
                new CloudTypeDef("Cirrus", 0.1f, 0.9f, C(0f, 0f, 0.35f, 0.9f, 0.65f, 0.8f, 1f, 0f), 0.7f),
                new CloudTypeDef("Cirrostratus", 0.2f, 0.8f, C(0f, 0f, 0.3f, 0.7f, 0.7f, 0.7f, 1f, 0f), 0.3f));
            // Thin sheets have no core: a softer rim and the water content enters the density linearly (the cfg's numbers).
            cirrus.SoftnessScale = 2f;
            cirrus.WaterPower = 1f;
            return cirrus;
        }
    }
}
