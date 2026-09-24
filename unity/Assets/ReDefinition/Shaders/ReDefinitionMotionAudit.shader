// The active vessel's motion vectors checked and written pixel by pixel
// (VesselMotionVectors in the plugin). Built into redefinition.shaders by BundleBuilder.
Shader "Hidden/ReDefinition/MotionAudit"
{
    SubShader
    {
        // Drawn per renderer of the vessel at the scene camera's BeforeImageEffects,
        // after the capture: where the renderer is the surface the scene camera saw
        // (its depth against the captured depth), the motion vector Unity writes for it
        // -- this frame's position through this frame's view-projection against the
        // previous frame's through the previous one, as Hidden/Internal-MotionVectors
        // does -- is compared with the captured one. Per part, into a buffer: pixels,
        // pixels off by more than _AuditBadPixels, the sum and the largest error in
        // sixteenths of a pixel.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "../Include/ReDefinition.cginc"

            // Rasterised as the scene camera drew it: its jittered projection, for
            // a render texture.
            float4x4 _AuditRasterViewProjection;
            // Without jitter, y up: this frame's and the previous frame's.
            float4x4 _AuditViewProjection;
            float4x4 _AuditPreviousViewProjection;
            // The renderer's local-to-world matrix in the previous frame.
            float4x4 _AuditPreviousModel;
            float _AuditPart;
            float _AuditBadPixels;
            // 1 / width, 1 / height, width, height of the render size.
            float4 _AuditTexel;
            sampler2D _AuditMotion;
            sampler2D _AuditDepth;
            RWStructuredBuffer<uint> _AuditStats : register(u1);

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 now : TEXCOORD0;
                float4 before : TEXCOORD1;
            };

            v2f vert (float4 vertex : POSITION)
            {
                float4 local = float4(vertex.xyz, 1.0);
                float4 world = mul(unity_ObjectToWorld, local);
                v2f o;
                o.pos = mul(_AuditRasterViewProjection, world);
                o.now = mul(_AuditViewProjection, world);
                o.before = mul(_AuditPreviousViewProjection, mul(_AuditPreviousModel, local));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.pos.xy * _AuditTexel.xy;
                float scene = tex2Dlod(_AuditDepth, float4(uv, 0.0, 0.0)).r;
                float own = i.pos.z;
                // Not the visible surface: something nearer, or the renderer's back.
                if (abs(own - scene) > 0.01 * max(own, scene) + 1e-7)
                    discard;
                float2 expected = ReDefinitionMotionVector(i.now, i.before).xy;
                float2 read = tex2Dlod(_AuditMotion, float4(uv, 0.0, 0.0)).xy;
                float error = length((read - expected) * _AuditTexel.zw);
                uint slot = (uint)_AuditPart * 4u;
                uint sixteenths = (uint)min(error * 16.0, 1.0e9);
                InterlockedAdd(_AuditStats[slot], 1u);
                if (error > _AuditBadPixels)
                    InterlockedAdd(_AuditStats[slot + 1u], 1u);
                InterlockedAdd(_AuditStats[slot + 2u], min(sixteenths, 65535u));
                InterlockedMax(_AuditStats[slot + 3u], sixteenths);
                return 0;
            }
            ENDCG
        }

        // 1: the same motion vector written into the captured ones, where the
        // renderer is the surface the scene camera saw. Drawn before the other mods'
        // motion vector hooks, so a mod that writes its own for a part keeps them.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            ColorMask RG

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "../Include/ReDefinition.cginc"

            float4x4 _AuditRasterViewProjection;
            float4x4 _AuditViewProjection;
            float4x4 _AuditPreviousViewProjection;
            float4x4 _AuditPreviousModel;
            float4 _AuditTexel;
            sampler2D _AuditDepth;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 now : TEXCOORD0;
                float4 before : TEXCOORD1;
            };

            v2f vert (float4 vertex : POSITION)
            {
                float4 local = float4(vertex.xyz, 1.0);
                float4 world = mul(unity_ObjectToWorld, local);
                v2f o;
                o.pos = mul(_AuditRasterViewProjection, world);
                o.now = mul(_AuditViewProjection, world);
                o.before = mul(_AuditPreviousViewProjection, mul(_AuditPreviousModel, local));
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 uv = i.pos.xy * _AuditTexel.xy;
                float scene = tex2Dlod(_AuditDepth, float4(uv, 0.0, 0.0)).r;
                float own = i.pos.z;
                if (abs(own - scene) > 0.01 * max(own, scene) + 1e-7)
                    discard;
                return float4(ReDefinitionMotionVector(i.now, i.before).xy, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
