// The main body's cloud layer for the raytracing (src/Raytracing/Ksp/RtEveClouds.cs). EVE's
// volumetric layer's maps are packed into one equirectangular texture the proxy takes
// (RT_INPUT_CLOUDS; RtCommon.hlsli, CloudTransmittanceAt). In it r is the coverage, g the
// density curve's coverage at the height of EVE's cloud shadow, for the cloud type there.
// The maps are read as EVE's own shaders read them (its RaymarchCloud and
// ScreenSpaceCloudShadow shaders). The coverage is the alpha mask's channel where it has
// one (useAlphaMask1, alphaMask1), else its alpha. The type is the mask's channel
// (useAlphaMask2, alphaMask2), else its red. The curve is DensityCurve's red at (type,
// height between the inner and the outer radius).
Shader "Hidden/ReDefinition/RtClouds"
{
    Properties
    {
        CloudCoverage ("Coverage", 2D) = "white" {}
        CloudType ("Type", 2D) = "white" {}
        DensityCurve ("Density curve", 2D) = "white" {}
        useAlphaMask1 ("", Float) = 0
        alphaMask1 ("", Vector) = (0, 0, 0, 1)
        useAlphaMask2 ("", Float) = 0
        alphaMask2 ("", Vector) = (1, 0, 0, 0)
        _ReDefinitionCloudHeight ("Shadow height in the layer, 0 to 1", Float) = 0
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        // The one pass: EVE's maps blitted into the packed map, a texel per pixel.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            // EVE's layer's maps and alpha-mask switches, copied from its material (RtEveClouds), and the cloud
            // shadow's height in the layer.
            sampler2D CloudCoverage;
            sampler2D CloudType;
            sampler2D DensityCurve;
            float useAlphaMask1, useAlphaMask2;
            float4 alphaMask1, alphaMask2;
            float _ReDefinitionCloudHeight;

            // One texel of the packed map: the coverage in r, the density curve for the texel's cloud type in g.
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2Dlod(CloudCoverage, float4(i.uv, 0.0, 0.0));
                float4 t = tex2Dlod(CloudType, float4(i.uv, 0.0, 0.0));
                float coverage = useAlphaMask1 > 0.5 ? dot(c, alphaMask1) : c.a;
                float type = useAlphaMask2 > 0.5 ? dot(t, alphaMask2) : t.r;
                float curve = tex2Dlod(DensityCurve, float4(type, saturate(_ReDefinitionCloudHeight), 0.0, 0.0)).r;
                return float4(coverage, curve, 0.0, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
