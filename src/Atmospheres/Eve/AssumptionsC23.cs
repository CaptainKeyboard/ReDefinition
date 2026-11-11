using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the cloud-map painter takes for granted about EVE Redux's volumetric clouds (Atmosphere.dll 3.2.2.0; the source tree release-5
    // has ray placement the installed DLL lacks, so everything is looked up in the installed DLL). Every member EveLayerAccess reaches by
    // name is named here; the methods the painter's behaviour rests on are fingerprinted, so that an EVE update that changes them makes the
    // entry Changed and, with WhenChanged.Fallback, leaves the clouds as EVE has them (the painter does not run).
    //
    // What each rests on:
    //   - CloudsManager.GetObjectList lists the CloudsObject layers (it is declared in the generic base GenericEVEManager, so the lookup
    //     of EveLayerAccess finds it through the hierarchy and this entry cannot name it); CloudsManager.onApply fires after every apply.
    //   - TextureWrapper.textureValue is the Texture2D EVE's CPU readers sample (SampleCoverage -> GetPixelBilinear) and ApplyTexture sets
    //     on the material; the mirror is put there.
    //   - CloudsRaymarchedVolume.SetShadowCasterTextureParams(rt, true) binds a caster's coverage render texture on a dependent layer.
    //   - CloudsPQS.mainPeriod and offset are the layer's drift: rotation = fract(ut x mainPeriod) x 360 + offset.
    //   - The shader property names CloudCoverage, CloudType, alphaMask1/2, useAlphaMask1/2 and the keywords ALPHAMAP_1/2, MAP_TYPE_1,
    //     CLOUD_SHADOW_CASTER_OFF are the contract with EVE's closed shaders (CloudsPainter.SetTextureProperties uses the same).
    [AtmospheresAssumptions]
    internal static class AssumptionsC23
    {
        internal const string Id = "atmo-c23-eve-painter";

        private const string Manager = "Atmosphere.CloudsManager";
        private const string Layer = "Atmosphere.CloudsObject";
        private const string Volume = "Atmosphere.CloudsRaymarchedVolume";
        private const string Pqs = "Atmosphere.CloudsPQS";
        private const string Clouds2D = "Atmosphere.Clouds2D";
        private const string CloudType = "Atmosphere.CloudType";
        private const string Wrapper = "Utils.TextureWrapper";

        public static readonly ForeignAssumption EvePainter =
            new ForeignAssumption(Id, "EVE", "the cloud maps the weather field paints into EVE's volumetric layers")
                .Type(Manager).Type(Layer).Type(Volume).Type(Pqs).Type(Clouds2D).Type(CloudType).Type(Wrapper)
                .Field(Manager, "onApply")
                .Calls(Layer, "get_Name").Calls(Layer, "get_Body").Calls(Layer, "get_LayerRaymarchedVolume").Calls(Layer, "get_Layer2D")
                .Field(Layer, "cloudsPQS")
                .Calls(Pqs, "get_TimeSettings")
                .Field(Pqs, "mainPeriod").Field(Pqs, "offset", "UnityEngine.Vector3")
                .Calls(Volume, "get_CoverageMap").Calls(Volume, "get_CloudTypeMap")
                .Calls(Volume, "get_RaymarchedCloudMaterial").Calls(Volume, "get_ReflectionProbeRaymarchedCloudMaterial")
                .Calls(Volume, "get_CloudTypes").Calls(Volume, "get_InnerSphereRadius").Calls(Volume, "get_OuterSphereRadius")
                .Calls(Volume, "get_CloudRotationMatrix").Calls(Volume, "get_ReceiveShadowsFromLayer")
                .Calls(Volume, "ApplyShaderParams")
                .Method(Volume, "SetShadowCasterTextureParams")
                .Method(Volume, "SampleCoverage")
                .Field(Volume, "upwardsCloudSpeed", "System.Single")
                .Field(CloudType, "typeName", "System.String")
                .Calls(Clouds2D, "get_ScreenSpaceShadowMaterial").Calls(Clouds2D, "get_CloudRenderingMaterial")
                .Field(Wrapper, "textureValue", "UnityEngine.Texture2D").Field(Wrapper, "textureInitialized", "System.Boolean")
                .Tested("a46ebcaed810") // EVE 3.2.2: SetShadowCasterTextureParams and SampleCoverage
                .OnChange(WhenChanged.Fallback, "the clouds stay as EVE's own config draws them; the field's maps do not reach EVE");
    }
}
