// What Scatterer's ocean reads where it is shaded, after the opaque image effects:
// Deferred's ssrOutput, copied for the player's check of the ocean's reflections.
Shader "Hidden/ReDefinitionTest/ReadSsr"
{
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        // Copies ssrOutput over the whole target.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            // Deferred's screen-space reflections, as the ocean samples them.
            sampler2D ssrOutput;
            // Each pixel of ssrOutput as it stands, drawn into the copy.
            float4 frag(v2f_img i) : SV_Target { return tex2D(ssrOutput, i.uv); }
            ENDCG
        }
    }
    Fallback Off
}
