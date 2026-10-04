// What Scatterer's ocean reads where it is shaded, after the opaque image effects:
// Deferred's ssrOutput, copied for the player's check of the ocean's reflections.
Shader "Hidden/ReDefinitionTest/ReadSsr"
{
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D ssrOutput;
            float4 frag(v2f_img i) : SV_Target { return tex2D(ssrOutput, i.uv); }
            ENDCG
        }
    }
    Fallback Off
}
