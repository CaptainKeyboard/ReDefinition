// The scene's white balance of ReDefinition Atmospheres (owner 2026-11-19, audit A2): the whole picture, sky, ground, sea and clouds alike, is divided by the sun's
// tint (its colour over its luminance, CloudOutputStage.WhiteTint), as a camera balances its picture for the light of the scene. It replaces the white point that
// balanced the clouds alone and the aerial perspective's counter-correction (SceneWhiteBalance.cs).
//
// Draw order: after the upscaler and before TUFX's effects, on the upscaled HDR image (linear), through the host's after-upscaling hook. Motion vectors and depth:
// none needed (a per-pixel scale of the colour).
//
// One triangle over the whole target, placed from the vertex index alone, and each pixel read back by its own index (Load): no matrix and no filtering
// takes part, so the pass neither moves nor resamples the picture, whatever view and projection the host's hook leaves current.
Shader "ReDefinition/AtmospheresWhiteBalance"
{
    Properties
    {
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _WbSource;   // an exact copy of the upscaled picture (SceneWhiteBalance.Draw)
            float4 _WbScale;       // rgb: the factor on each channel (1 / the sun's tint); a: unused

            float4 vert(uint vid : SV_VertexID) : SV_POSITION
            {
                float2 p = float2((vid << 1) & 2, vid & 2);
                return float4(p * 2.0 - 1.0, 0.0, 1.0);
            }

            float4 frag(float4 pos : SV_POSITION) : SV_Target
            {
                float4 c = _WbSource.Load(int3(pos.xy, 0));
                return float4(c.rgb * _WbScale.rgb, c.a);
            }
            ENDCG
        }
    }
}
