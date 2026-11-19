// The scene's white balance of ReDefinition Atmospheres (owner 2026-11-19, audit A2): the whole picture, sky, ground, sea and clouds alike, is divided by the sun's
// tint (its colour over its luminance, CloudOutputStage.WhiteTint), as a camera balances its picture for the light of the scene. It replaces the white point that
// balanced the clouds alone and the haze's counter-correction (SceneWhiteBalance.cs).
//
// Draw order: after the upscaler and before TUFX's effects, on the upscaled HDR image (linear), through the host's after-upscaling hook. Motion vectors and depth:
// none needed (a per-pixel scale of the colour).
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
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _WbScale;   // rgb: the factor on each channel (1 / the sun's tint); a: unused

            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return float4(c.rgb * _WbScale.rgb, c.a);
            }
            ENDCG
        }
    }
}
