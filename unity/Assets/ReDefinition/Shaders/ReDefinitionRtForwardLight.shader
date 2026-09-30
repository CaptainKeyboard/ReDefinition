// The raytraced light added to the frame (src/Raytracing/RtCamera.cs,
// docs/development/raytracing.md, "Where the raytraced light enters Unity's lighting"):
// drawn after the opaque geometry's light, before the transparent and the atmosphere,
// for the raytraced camera, where Deferred's reflections pass is not there to take it.
// The proxy's pass gives the light to add, less KSP's flat ambient light, which Unity's
// shading added already: it may be negative. A render target without floats clamps
// what a shader writes before blending, so the added and the taken are two passes, one
// adding and one subtracting. Unity's deferred lighting without HDR keeps exp2(-light)
// (Internal-DeferredShading.shader): there the third pass multiplies by exp2(-added)
// where light is added, and the fourth by exp2(taken), as 1 + (exp2(taken) - 1), where
// it is taken.
Shader "Hidden/ReDefinition/RtForwardLight"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "ReDefinitionRtReproject.cginc"

    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    sampler2D _ReDefinitionRtForward;

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

    float3 Added(v2f i)
    {
        float2 uv = i.screen.xy / i.screen.w;
        float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
        #if defined(UNITY_REVERSED_Z)
        if (depth <= 0.0)
            return 0;
        #else
        if (depth >= 1.0)
            return 0;
        #endif
        // The position as Unity's screen-space shadows find it, from the camera's own
        // matrices, which carry the upscaler's jitter (Internal-ScreenSpaceShadows.shader,
        // computeCameraSpacePosFromDepthAndInvProjMat).
        #if defined(UNITY_REVERSED_Z)
        depth = 1.0 - depth;
        #endif
        float4 camera = mul(unity_CameraInvProjection, float4(float3(uv, depth) * 2.0 - 1.0, 1.0));
        camera.xyz /= camera.w;
        camera.z = -camera.z;
        float3 world = mul(unity_CameraToWorld, float4(camera.xyz, 1.0)).xyz;
        float2 previous;
        if (!RtUv(world, previous))
            return 0;
        return tex2Dlod(_ReDefinitionRtForward, float4(previous, 0.0, 0.0)).rgb;
    }

    half4 fragAdd(v2f i) : SV_Target
    {
        return half4(max(Added(i), 0.0), 0.0);
    }

    half4 fragTake(v2f i) : SV_Target
    {
        return half4(max(-Added(i), 0.0), 0.0);
    }

    half4 fragEncodedAdd(v2f i) : SV_Target
    {
        return half4(exp2(-max(Added(i), 0.0)), 1.0);
    }

    half4 fragEncodedTake(v2f i) : SV_Target
    {
        return half4(exp2(max(-Added(i), 0.0)) - 1.0, 0.0);
    }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        ColorMask RGB

        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdd
            ENDCG
        }
        Pass
        {
            BlendOp RevSub
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTake
            ENDCG
        }
        Pass
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragEncodedAdd
            ENDCG
        }
        Pass
        {
            Blend DstColor One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragEncodedTake
            ENDCG
        }
    }
    Fallback Off
}
