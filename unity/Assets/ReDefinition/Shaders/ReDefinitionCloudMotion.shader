// EVE's raymarched clouds in the motion vectors the upscalers and frame
// generation read (CloudMotionVectors in the plugin). Built into
// redefinition.shaders by BundleBuilder.
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
    }
}
