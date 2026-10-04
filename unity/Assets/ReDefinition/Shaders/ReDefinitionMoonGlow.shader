// A moon's disc made to stand out in the sky (src/Raytracing/Ksp/MoonGlow.cs): a brighter
// disc and a soft glow round it, added where the scene camera sees sky, before the
// transparent pass, so that clouds and the atmosphere draw over it. It lights nothing.
Shader "Hidden/ReDefinition/MoonGlow"
{
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            // xy: the disc's middle on the screen (0 to 1, from the bottom left); z: its
            // radius in heights of the screen; w: the screen's width over its height.
            float4 _ReDefinitionMoon;
            // rgb: the disc's colour as the frame's; a: how strongly it stands out.
            float4 _ReDefinitionMoonColour;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 screen : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = float4(vertex.xy, 0.0, 1.0);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            #if defined(UNITY_REVERSED_Z)
                if (depth > 0.0)
                    return 0.0;
            #else
                if (depth < 1.0)
                    return 0.0;
            #endif
                float2 offset = uv - _ReDefinitionMoon.xy;
                offset.x *= _ReDefinitionMoon.w;
                float r = length(offset) / max(_ReDefinitionMoon.z, 1e-5);
                // The disc a little brighter, its edge soft; round it a glow falling off
                // over a few radii.
                float disc = 1.0 - smoothstep(0.85, 1.05, r);
                float glow = exp(-max(r - 1.0, 0.0) * 1.2) * (1.0 - disc);
                return half4(_ReDefinitionMoonColour.rgb * _ReDefinitionMoonColour.a * (1.2 * disc + 0.6 * glow), 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
