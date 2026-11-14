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

        // A type's climate (cfg node CLIMATE, E40 L3): weight 1 and the curves of latitude, surface, humidity and precipitation (null: none).
        private static CloudClimateSpec Climate(Curve latitude, Curve surface, Curve humidity, Curve precipitation)
        {
            return new CloudClimateSpec { Weight = 1f, Latitude = latitude, Surface = surface, Humidity = humidity, Precipitation = precipitation };
        }

        // The type's light response by height (cfg keys baseLight, headLight, baseAmbient, headAmbient).
        private static void Light(CloudTypeDef t, float baseLight, float headLight, float baseAmbient, float headAmbient)
        {
            t.BaseLight = baseLight;
            t.HeadLight = headLight;
            t.BaseAmbient = baseAmbient;
            t.HeadAmbient = headAmbient;
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
            rain.Types[1].DensityScale = 0.22f;
            return rain;
        }

        internal static CloudLayerDef Cumulus()
        {
            CloudLayerDef cumulus = Layer("Kerbin-cumulus", "", 1700f, 7400f, 0.05f, 0.35f, 0.15f, 1f, 500f, LayerKind.Weather,
                new CloudTypeDef("Stratocumulus", 0f, 0.11f, C(0f, 0f, 0.15f, 0.9f, 0.6f, 1f, 1f, 0f), 0.3f),
                new CloudTypeDef("Cumulus humilis", 0f, 0.21f, C(0f, 0f, 0.08f, 0.9f, 0.55f, 1f, 0.9f, 0.5f, 1f, 0f), 0.45f),
                new CloudTypeDef("Cumulus mediocris", 0f, 0.47f, C(0f, 0f, 0.06f, 0.9f, 0.5f, 1f, 0.85f, 0.6f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulus congestus", 0f, 1f, C(0f, 0f, 0.04f, 0.9f, 0.45f, 1f, 0.8f, 0.7f, 1f, 0f), 0.55f),
                new CloudTypeDef("Altocumulus", 0.54f, 0.68f, C(0f, 0f, 0.2f, 0.9f, 0.7f, 0.8f, 1f, 0f), 0.4f));
            cumulus.Types[0].DensityScale = 0.7f;
            cumulus.Types[1].DensityScale = 0.7f;
            cumulus.Types[2].DensityScale = 0.7f;
            cumulus.Types[4].DensityScale = 0.45f;
            Light(cumulus.Types[0], 1f, 1f, 0.9f, 1f);
            Light(cumulus.Types[1], 0.9f, 1.15f, 0.9f, 1f);
            Light(cumulus.Types[2], 0.7f, 1.15f, 0.85f, 1f);
            Light(cumulus.Types[3], 0.65f, 1.25f, 0.8f, 1f);
            // The types' height-shaped threshold (cfg node LIFT, E40 L2 D1).
            cumulus.Types[0].Lift = C(0f, 0f, 0.5f, 0f, 1f, 0.1f);
            cumulus.Types[1].Lift = C(0f, 0f, 0.3f, 0f, 0.7f, 0.08f, 1f, 0.2f);
            cumulus.Types[2].Lift = C(0f, 0f, 0.2f, 0f, 0.6f, 0.12f, 1f, 0.3f);
            cumulus.Types[3].Lift = C(0f, 0f, 0.15f, 0f, 0.5f, 0.15f, 1f, 0.38f);
            cumulus.Types[4].Lift = C(0f, 0f, 0.4f, 0f, 1f, 0.1f);
            // The type at no precipitation is humilis (the second of five), at full precipitation congestus (the fourth); the mid-level sheet's rule picks the altocumulus (the last).
            cumulus.FairType = 0.25f;
            cumulus.RainType = 0.75f;
            cumulus.AltoType = 1f;
            // E40 L3: the types' climate (cfg node CLIMATE), the type patches and the cover's meso pattern (cfg node MESO).
            cumulus.Types[0].Climate = Climate(C(0f, 0.35f, 0.35f, 0.6f, 0.65f, 1f, 1f, 1f), C(0f, 1f, 0.5f, 0.35f, 1f, 0.15f), C(0f, 0.1f, 0.4f, 0.3f, 0.75f, 1f, 1f, 1f), C(0f, 1f, 0.25f, 0.6f, 1f, 0.15f));
            cumulus.Types[1].Climate = Climate(C(0f, 1f, 0.55f, 1f, 1f, 0.45f), C(0f, 0.6f, 0.5f, 1f, 1f, 0.8f), C(0f, 0.4f, 0.3f, 1f, 1f, 0.8f), C(0f, 1f, 0.35f, 0.45f, 1f, 0.05f));
            cumulus.Types[2].Climate = Climate(C(0f, 1f, 0.6f, 0.8f, 1f, 0.15f), C(0f, 0.55f, 0.5f, 1f, 1f, 1f), C(0f, 0.1f, 0.35f, 0.6f, 0.7f, 1f, 1f, 1f), C(0f, 0.45f, 0.2f, 1f, 0.65f, 1f, 1f, 0.5f));
            cumulus.Types[3].Climate = Climate(C(0f, 1f, 0.4f, 0.8f, 0.7f, 0.15f, 1f, 0f), C(0f, 0.45f, 0.5f, 1f, 1f, 1f), C(0f, 0f, 0.4f, 0.15f, 0.75f, 1f, 1f, 1f), C(0f, 0.05f, 0.2f, 0.5f, 0.5f, 1f, 1f, 1f));
            cumulus.Types[4].Climate = Climate(C(0f, 0.6f, 0.5f, 1f, 1f, 0.7f), null, null, null);
            cumulus.TypePatchDepths = 6.7f;
            cumulus.TypePatchBlend = 0.06f;
            cumulus.Meso = new CloudMesoSpec { ScaleDepths = 5.25f, Octaves = 2, Amount = 1f, Edge = 0.3f, Shoulder = 1f, Cells = true, Stretch = 1f, RainKeep = 0f };
            return cumulus;
        }

        internal static CloudLayerDef Cumulonimbus()
        {
            CloudLayerDef cb = Layer("Kerbin-cumulonimbus", "", 1700f, 11000f, 0.08f, 0.12f, 0.1f, 0.4f, 5000f, LayerKind.Weather,
                new CloudTypeDef("Cumulonimbus calvus", 0f, 0.7f, C(0f, 0.2f, 0.05f, 0.9f, 0.5f, 1f, 0.85f, 0.7f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulonimbus incus", 0f, 1f, C(0f, 0.2f, 0.05f, 0.8f, 0.4f, 0.9f, 0.7f, 1f, 0.88f, 0.8f, 1f, 0f), 0.5f));
            cb.Deck = new WeatherDeckRule { Fog = false, PrecipType = 0f };
            foreach (CloudTypeDef t in cb.Types) t.RiseShare = 0.3f;
            cb.LightAmbient = 0.8f;
            Light(cb.Types[0], 0.55f, 1.2f, 0.7f, 1f);
            Light(cb.Types[1], 0.55f, 1.15f, 0.7f, 1.05f);
            cb.Types[0].Lift = C(0f, 0f, 0.1f, 0f, 0.5f, 0.25f, 1f, 0.45f);
            cb.Types[1].Lift = C(0f, 0f, 0.1f, 0f, 0.45f, 0.35f, 0.7f, 0.35f, 0.9f, 0f, 1f, 0f);
            cb.Meso = new CloudMesoSpec { ScaleDepths = 5.7f, Octaves = 2, Amount = 1f, Edge = 0.3f, Shoulder = 1f, Cells = true, Stretch = 1f, RainKeep = 0.5f };
            return cb;
        }

        internal static CloudLayerDef Cirrus()
        {
            CloudLayerDef cirrus = Layer("Kerbin-cirrus", "", 9000f, 11000f, 0.0002f, 0.3f, 0.35f, 0.5f, 3000f, LayerKind.Cheap,
                new CloudTypeDef("Cirrus", 0.1f, 0.9f, C(0f, 0f, 0.35f, 0.9f, 0.65f, 0.8f, 1f, 0f), 0.7f),
                new CloudTypeDef("Cirrostratus", 0.2f, 0.8f, C(0f, 0f, 0.3f, 0.7f, 0.7f, 0.7f, 1f, 0f), 0.3f));
            // Thin sheets have no core: a softer rim and the water content enters the density linearly (the cfg's numbers).
            cirrus.Types[0].DensityScale = 2.5f;
            cirrus.Types[1].DensityScale = 4f;
            cirrus.LightAmbient = 1.3f;
            cirrus.LightIsotropy = 0.5f;
            Light(cirrus.Types[0], 1f, 1.15f, 1f, 1f);
            cirrus.SoftnessScale = 2f;
            cirrus.WaterPower = 1f;
            cirrus.FibreStretch = 6f;
            cirrus.Types[0].Climate = Climate(null, null, null, C(0f, 1f, 0.3f, 0.45f, 1f, 0.15f));
            cirrus.Types[1].Climate = Climate(C(0f, 0.3f, 0.5f, 1f, 1f, 0.8f), null, C(0f, 0.2f, 0.6f, 1f, 1f, 1f), C(0f, 0.25f, 0.3f, 1f, 1f, 1f));
            cirrus.TypePatchDepths = 10.5f;
            cirrus.Meso = new CloudMesoSpec { ScaleDepths = 5.7f, Octaves = 2, Amount = 1f, Edge = 0.35f, Shoulder = 1f, Cells = true, Stretch = 2.5f, RainKeep = 0f };
            return cirrus;
        }
    }
}
