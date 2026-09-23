// EVE's raymarched clouds and the distant planets in the motion vectors the
// upscalers and frame generation read (CloudMotionVectors and ScaledSpaceMotion
// in the plugin). Built into redefinition.shaders by BundleBuilder.
Shader "Hidden/ReDefinition/CloudMotion"
{
    SubShader
    {
        // 0: at the scene camera's AfterForwardAlpha, while EVE's globals are still
        // bound, into a copy of our own: the clouds' motion vectors without the
        // jitter EVE rendered them with (rg), their transmittance (b), and whether
        // they have motion vectors at all (a). Addressed by pixel, like the masks:
        // the sources are the camera's own size.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            // Bound by EVE's command buffer at AfterForwardOpaque: premultiplied
            // cloud colour with the transmittance in alpha, and the clouds' motion
            // vectors -- as Unity's, the change in viewport position since the last
            // frame, and below -1 where there are none (Scatterer's
            // Scatterer-EVE/ReconstructRaymarchedClouds writes them, its
            // TemporalAntialiasing reads them so).
            sampler2D scattererReconstructedCloud;
            sampler2D scattererReconstructedCloudMotionVectors;
            // The fade EVE sets on its cloud composite (cloudFade).
            float _ReDefinitionCloudFade;
            // Half the change of the jitter, in normalized device coordinates,
            // between the frame EVE rendered last and this one: what EVE's motion
            // vectors carry of it when it renders with the jitter (EveCloudMotion).
            float4 _ReDefinitionCloudJitterDelta;
            float4 _ReDefinitionCloudTexel;

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionCloudTexel.xy;
                float transmittance = tex2D(scattererReconstructedCloud, uv).a;
                transmittance = 1.0 - _ReDefinitionCloudFade * (1.0 - transmittance);
                float2 motion = tex2D(scattererReconstructedCloudMotionVectors, uv).xy;
                float valid = motion.x >= -1.0 && motion.y >= -1.0 ? 1.0 : 0.0;
                return float4((motion - _ReDefinitionCloudJitterDelta.xy) * valid, transmittance, valid);
            }
            ENDCG
        }

        // 1: at BeforeImageEffects, over the copy of Unity's motion vectors, as
        // Scatterer's TemporalAntialiasing blends them: the clouds' where they
        // cover the pixel, Unity's where they do not -- over the sky by the
        // transmittance, over geometry already where a tenth of it is left.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _ReDefinitionUnityMotion;
            sampler2D _ReDefinitionCloudMotion;
            sampler2D _ReDefinitionCloudSceneDepth;
            float4 _ReDefinitionCloudTexel;

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionCloudTexel.xy;
                float2 unityMotion = tex2D(_ReDefinitionUnityMotion, uv).xy;
                float4 cloud = tex2D(_ReDefinitionCloudMotion, uv);
                float depth = tex2D(_ReDefinitionCloudSceneDepth, uv).r;
            #if UNITY_REVERSED_Z
                float sky = depth <= 0.0 ? 1.0 : 0.0;
            #else
                float sky = depth >= 1.0 ? 1.0 : 0.0;
            #endif
                float unityWeight = lerp(saturate(cloud.b * 10.0), cloud.b, sky);
                float2 motion = lerp(cloud.xy, unityMotion, unityWeight);
                return float4(lerp(unityMotion, motion, cloud.a), 0.0, 0.0);
            }
            ENDCG
        }

        // 2: at BeforeImageEffects, before the clouds: where the scene camera drew
        // nothing, the motion of the nearest distant body the scaled camera's ray
        // meets, on its surface or in the atmosphere around it; the scene camera's
        // own everywhere else (ScaledSpaceMotion in the plugin).
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "../Include/ReDefinition.cginc"

            #define MAX_BODIES 8

            sampler2D _ReDefinitionUnityMotion;
            sampler2D _ReDefinitionCloudSceneDepth;
            float4 _ReDefinitionCloudTexel;
            // Per body: centre and radius in scaled space; then, from MAX_BODIES
            // on, the atmosphere's radius in x, 0 while the camera is inside it.
            float4 _ReDefinitionBodySpheres[MAX_BODIES * 2];
            // Per body: from this frame's position to the frame before's.
            float4x4 _ReDefinitionBodyMotions[MAX_BODIES];
            float _ReDefinitionBodyCount;
            // The scaled camera's, through GL.GetGPUProjectionMatrix(..., false):
            // clip space with y up, without jitter.
            float4x4 _ReDefinitionScaledViewProjection;
            float4x4 _ReDefinitionScaledPreviousViewProjection;
            float4x4 _ReDefinitionScaledInverseViewProjection;
            float4 _ReDefinitionScaledCameraPosition;

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionCloudTexel.xy;
                float2 sceneMotion = tex2D(_ReDefinitionUnityMotion, uv).xy;
                float depth = tex2D(_ReDefinitionCloudSceneDepth, uv).r;
            #if UNITY_REVERSED_Z
                bool nothing = depth <= 0.0;
            #else
                bool nothing = depth >= 1.0;
            #endif
                if (!nothing || _ReDefinitionBodyCount < 0.5)
                    return float4(sceneMotion, 0.0, 0.0);

                // The pixel's viewport position, y up: a blit into a render
                // texture addresses it as the camera drew it.
                float2 viewport = uv;
                // Any depth inside the frustum gives a point on the pixel's ray.
                float4 onRay = mul(_ReDefinitionScaledInverseViewProjection, float4(viewport * 2.0 - 1.0, 0.5, 1.0));
                float3 origin = _ReDefinitionScaledCameraPosition.xyz;
                float3 direction = normalize(onRay.xyz / onRay.w - origin);

                float nearest = 1e30;
                int index = -1;
                [loop]
                for (int b = 0; b < MAX_BODIES; b++)
                {
                    if (b >= (int)_ReDefinitionBodyCount) break;
                    float4 sphere = _ReDefinitionBodySpheres[b];
                    float outer = _ReDefinitionBodySpheres[MAX_BODIES + b].x;
                    float3 toOrigin = origin - sphere.xyz;
                    float along = dot(toOrigin, direction);
                    float missSquared = dot(toOrigin, toOrigin) - along * along;
                    float t = -1.0;
                    if (missSquared <= sphere.w * sphere.w)
                        t = -along - sqrt(sphere.w * sphere.w - missSquared);
                    else if (missSquared <= outer * outer)
                        t = -along;
                    if (t > 0.0 && t < nearest)
                    {
                        nearest = t;
                        index = b;
                    }
                }
                if (index < 0)
                    return float4(sceneMotion, 0.0, 0.0);

                float4 now = float4(origin + direction * nearest, 1.0);
                float4 before = mul(_ReDefinitionBodyMotions[index], now);
                half4 motion = ReDefinitionMotionVector(mul(_ReDefinitionScaledViewProjection, now),
                                                        mul(_ReDefinitionScaledPreviousViewProjection, before));
                return float4(motion.xy, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
