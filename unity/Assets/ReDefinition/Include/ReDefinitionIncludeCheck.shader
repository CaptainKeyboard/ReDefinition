// Compiles every function of ReDefinition.cginc (Editor/IncludeCheck.cs). Not in the bundle:
// BundleBuilder takes the shaders directly in Assets/ReDefinition/Shaders.
Shader "Hidden/ReDefinition/IncludeCheck"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "ReDefinition.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 current : TEXCOORD0;
                float4 previous : TEXCOORD1;
            };

            float4x4 _CheckPreviousVP;

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.current = ReDefinitionRemoveJitter(o.pos);
                o.previous = mul(_CheckPreviousVP, mul(unity_ObjectToWorld, v.vertex));
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float4 renderSize = ReDefinitionRenderSize();
                float4 displaySize = ReDefinitionDisplaySize();
                float2 jitter = ReDefinitionJitterPixels() * renderSize.zw + ReDefinitionJitterNdc() * displaySize.zw;
                float3 shift = ReDefinitionOriginShift();
                bool any = ReDefinitionInstalled() || ReDefinitionUpscalerActive() || ReDefinitionFrameGenerationActive()
                           || ReDefinitionHistoryReset() || ReDefinitionOriginShifted();
                half4 motion = ReDefinitionMotionVector(i.current, i.previous);
                return motion + half4(jitter, shift.x, any ? 1 : 0);
            }
            ENDCG
        }
    }
}
