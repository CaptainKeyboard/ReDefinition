// The raytracing's inputs, copied in the scene camera's OnRenderImage
// (src/Raytracing/RtCamera.cs): depth as Direct3D's clip-space z, the G-buffer as
// Unity's deferred path left it, the motion vectors, and the sky's cube map as a
// latitude-longitude image. Each pass writes one; the source of the blit is only
// there for its orientation.
Shader "Hidden/ReDefinition/RtCapture"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    sampler2D _CameraGBufferTexture0;
    sampler2D _CameraGBufferTexture1;
    sampler2D _CameraGBufferTexture2;
    sampler2D_half _CameraMotionVectorsTexture;
    UNITY_DECLARE_TEXCUBE(_ReDefinitionRtCube);
    half4 _ReDefinitionRtCube_HDR;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float fragDepth(v2f i) : SV_Target
    {
        return SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
    }

    half4 fragGBuffer0(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture0, i.uv); }
    half4 fragGBuffer1(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture1, i.uv); }
    half4 fragGBuffer2(v2f i) : SV_Target { return tex2D(_CameraGBufferTexture2, i.uv); }

    half2 fragMotion(v2f i) : SV_Target
    {
        return tex2D(_CameraMotionVectorsTexture, i.uv).rg;
    }

    // x the angle around the vertical from the +z axis, y from straight up; as
    // RaytracingShaders/RtCommon.hlsli reads it (Sky). The first row in memory, where
    // the proxy starts, is Unity's top of a render texture: straight up (measured in
    // the raytracing test player).
    half4 fragEnvironment(v2f i) : SV_Target
    {
        float v = i.uv.y;
        float phi = (i.uv.x - 0.5) * 2.0 * UNITY_PI;
        float theta = (1.0 - v) * UNITY_PI;
        float3 direction = float3(sin(theta) * sin(phi), cos(theta), sin(theta) * cos(phi));
        half4 encoded = UNITY_SAMPLE_TEXCUBE_LOD(_ReDefinitionRtCube, direction, 2);
        return half4(DecodeHDR(encoded, _ReDefinitionRtCube_HDR), 1.0);
    }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer1
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGBuffer2
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMotion
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragEnvironment
            ENDCG }
    }
    Fallback Off
}
