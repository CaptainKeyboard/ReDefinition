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
// it is taken. Forward rendering, without a G-buffer, takes the surface's colour from
// the frame itself: the fifth pass reads a copy of it and writes (c + min(c, w) k) t plus
// the reflections, k and w from the proxy's ratio (Raytracing.hlsl, ForwardLight), t the
// tarmac's share as Deferred colours it (ReDefinitionRtTarmac.shader).
Shader "Hidden/ReDefinition/RtForwardLight"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "ReDefinitionRtReproject.cginc"

    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    sampler2D _ReDefinitionRtForward;
    sampler2D _ReDefinitionRtForwardRatio;
    sampler2D _ReDefinitionRtFrame;
    // Per pixel, the space centre's ground as Deferred colours it against KSP's own shader
    // (ReDefinitionRtTarmac.shader), 1 elsewhere.
    sampler2D _ReDefinitionRtTarmac;

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

    // Where the pixel was in the proxy's frame, false where it holds nothing for it.
    bool Traced(v2f i, out float2 previous)
    {
        previous = 0;
        float2 uv = i.screen.xy / i.screen.w;
        float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
        #if defined(UNITY_REVERSED_Z)
        if (depth <= 0.0)
            return false;
        #else
        if (depth >= 1.0)
            return false;
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
        return RtUvFilled(world, previous);
    }

    float3 Added(v2f i)
    {
        float2 previous;
        if (!Traced(i, previous))
            return 0;
        return tex2Dlod(_ReDefinitionRtForward, float4(previous, 0.0, 0.0)).rgb;
    }

    half4 fragRelight(v2f i) : SV_Target
    {
        float2 uv = i.screen.xy / i.screen.w;
        float3 c = tex2Dlod(_ReDefinitionRtFrame, float4(uv, 0.0, 0.0)).rgb;
        // The ground's look wherever it is drawn, as Deferred colours it everywhere.
        float3 ground = tex2Dlod(_ReDefinitionRtTarmac, float4(uv, 0.0, 0.0)).rgb;
        float2 previous;
        if (!Traced(i, previous))
            return half4(c * ground, 1.0);
        float4 ratio = tex2Dlod(_ReDefinitionRtForwardRatio, float4(previous, 0.0, 0.0));
        float3 reflections = tex2Dlod(_ReDefinitionRtForward, float4(previous, 0.0, 0.0)).rgb;
        return half4(max((c + min(c, ratio.w) * ratio.rgb) * ground + max(reflections, 0.0), 0.0), 1.0);
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
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRelight
            ENDCG
        }
    }
    Fallback Off
}
