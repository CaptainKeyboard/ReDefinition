// Compiles every function of ReDefinition.cginc (Editor/IncludeCheck.cs). Not in the bundle:
// BundleBuilder takes the shaders directly in Assets/ReDefinition/Shaders.
Shader "Hidden/ReDefinition/IncludeCheck"
{
    SubShader
    {
        // The one pass: a vertex and a fragment function that between them call every function of the include.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "ReDefinition.cginc"

            // What the vertex function hands the fragment function.
            struct v2f
            {
                float4 pos : SV_POSITION; // the jittered clip position the GPU rasterizes
                float4 current : TEXCOORD0; // the clip position without the jitter
                float4 previous : TEXCOORD1; // the clip position through _CheckPreviousVP
            };

            // A previous view-projection, only so the motion vector has a second position; never set.
            float4x4 _CheckPreviousVP;

            // Projects the vertex and calls the jitter removal on it.
            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.current = ReDefinitionRemoveJitter(o.pos);
                o.previous = mul(_CheckPreviousVP, mul(unity_ObjectToWorld, v.vertex));
                return o;
            }

            // Calls every other function of the include and returns their results, so none is compiled away.
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
