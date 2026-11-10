// The weather player's analytic motion vector and depth for the cloud slab (no mask): where the slab covers a pixel, the pixel
// takes the motion and the depth of the point where its view ray meets the slab's middle surface, as it moved with the wind since
// the frame before. Elsewhere the scene's own values stay. Pass 0 writes the motion (Unity's convention: the pixel's position in
// texture space now minus before, in UV units), pass 1 the depth (Direct3D's clip-space z).
Shader "Hidden/ReDefinition/WeatherPlayerSlabMotion"
{
    Properties
    {
        _MainTex ("Scene motion or depth", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        // The slab texture, premultiplied colour and opacity, and the scene's depth as Direct3D's z.
        sampler2D _SlabTex;
        sampler2D _SceneDepth;
        // The cell's frame as the slab shader has it, and the middle surface's height above the centre, m.
        float4 _Cell;
        float4 _Up;
        float4 _CamRight;
        float4 _CamUp;
        float4 _CamForward;
        // The wind's displacement since the last frame, world space, m; the unjittered view-projection now and before.
        float4 _WindStep;
        float4x4 _CurViewProj;
        float4x4 _PrevViewProj;
        float4 _Mid;
        // x: the opacity above which the slab counts, y: 1 where the signs of the motion are to be flipped.
        float4 _Gate;

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

        // Whether the slab covers the pixel and where its middle surface is met: world position in xyz, 1 in w; 0 in w otherwise.
        float4 Hit(float2 uv)
        {
            float4 slab = tex2D(_SlabTex, uv);
            if (slab.a < _Gate.x) return 0.0;
            float2 ndc = uv * 2.0 - 1.0;
            float3 ro = _WorldSpaceCameraPos;
            float3 rd = normalize(_CamForward.xyz + ndc.x * _CamRight.xyz + ndc.y * _CamUp.xyz);
            float rdy = dot(rd, _Up.xyz);
            float roy = dot(ro - _Cell.xyz, _Up.xyz);
            if (abs(rdy) < 1e-5) return 0.0;
            float t = (_Mid.x - roy) / rdy;
            if (t <= 0.0) return 0.0;
            // Nearer scene geometry hides the slab there.
            float sceneZ = tex2D(_SceneDepth, uv).r;
            float eye = LinearEyeDepth(sceneZ);
            float forward = dot(rd, _CamForward.xyz);
            if (eye < _ProjectionParams.z * 0.99 && eye < t * forward) return 0.0;
            return float4(ro + rd * t, 1.0);
        }

        float2 ToUv(float4 clip)
        {
            float2 ndc = clip.xy / clip.w;
            // Unity's motion on Direct3D 11 is measured in y-up clip space (ReDefinition.cginc, ReDefinitionMotionVector).
            return ndc * 0.5 + 0.5;
        }

        float2 fragMotion(v2f i) : SV_Target
        {
            float4 hit = Hit(i.uv);
            float2 scene = tex2D(_MainTex, i.uv).rg;
            if (hit.w == 0.0) return scene;
            float2 now = ToUv(mul(_CurViewProj, float4(hit.xyz, 1.0)));
            float2 before = ToUv(mul(_PrevViewProj, float4(hit.xyz - _WindStep.xyz, 1.0)));
            float2 motion = now - before;
            return _Gate.y > 0.5 ? -motion : motion;
        }

        float fragDepth(v2f i) : SV_Target
        {
            float4 hit = Hit(i.uv);
            float scene = tex2D(_MainTex, i.uv).r;
            if (hit.w == 0.0) return scene;
            float4 clip = mul(_CurViewProj, float4(hit.xyz, 1.0));
            return clip.z / clip.w;
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMotion
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma target 3.5
            ENDCG
        }
    }
}
