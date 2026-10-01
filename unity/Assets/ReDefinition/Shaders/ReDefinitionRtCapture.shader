// The raytracing's inputs, copied in the scene camera's OnRenderImage
// (src/Raytracing/RtCamera.cs): depth as Direct3D's clip-space z, the G-buffer as
// Unity's deferred path left it, the motion vectors, and the sky's cube map as a
// latitude-longitude image. Each pass writes one; the source of the blit is only
// there for its orientation. The last pass makes a material's small texture
// (src/Raytracing/RtScene.cs, ReadTexture) from its full-size one.
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
    sampler2D _MainTex;
    float _ReDefinitionRtCutoff;

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

    // A texel of a material's small texture from the full-size texture's texels under
    // it, read at full size in 8 x 8 points: a mip level's averaged alpha would blur a
    // cut-out's edge into the threshold. For a cut-out material (_ReDefinitionRtCutoff
    // above 0) the alpha is the share of points the cut-out keeps and the colour that of
    // those points alone, as only they are ever seen; otherwise both are the average.
    half4 fragMaterialTexture(v2f i) : SV_Target
    {
        float2 span = float2(abs(ddx(i.uv.x)), abs(ddy(i.uv.y)));
        float3 kept = 0.0;
        float3 all = 0.0;
        float covered = 0.0;
        float alpha = 0.0;
        [unroll]
        for (int y = 0; y < 8; y++)
        {
            [unroll]
            for (int x = 0; x < 8; x++)
            {
                float2 uv = i.uv + span * ((float2(x, y) + 0.5) / 8.0 - 0.5);
                half4 c = tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
                float keep = c.a >= _ReDefinitionRtCutoff ? 1.0 : 0.0;
                kept += c.rgb * keep;
                covered += keep;
                all += c.rgb;
                alpha += c.a;
            }
        }
        if (_ReDefinitionRtCutoff <= 0.0)
            return half4(all / 64.0, alpha / 64.0);
        return half4(covered > 0.0 ? kept / covered : all / 64.0, covered / 64.0);
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
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMaterialTexture
            #pragma target 3.0
            ENDCG }
    }
    Fallback Off
}
