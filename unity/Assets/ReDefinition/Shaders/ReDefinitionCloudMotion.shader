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
        // the sources are the camera's own size. With Scatterer's reconstruction;
        // pass 4 takes its place with any other.
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
        // own everywhere else (ScaledSpaceMotion in the plugin). Where the scene
        // camera drew something far away, the motion of a point fixed to the main
        // body, from the depth and the camera's move against that body; where it
        // drew nothing and no body is met, the camera's turn alone. Unity's own
        // there is noise, worked out in single precision from world positions a
        // hundred kilometres and more from the origin.
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
            // The scene camera's clip space, as GL.GetGPUProjectionMatrix(..., false)
            // gives it without jitter, into the frame before's, through the
            // camera's move against the main body.
            float4x4 _ReDefinitionSceneClipToPrevClip;
            // The same for points at infinity: the camera's turn alone.
            float4x4 _ReDefinitionSkyClipToPrevClip;
            // Near and far plane, the distance from which the scene camera's own
            // motion is replaced, and 1 while this frame has the matrices above.
            float4 _ReDefinitionSceneRange;

            float SceneDistance(float depth)
            {
                float nearPlane = _ReDefinitionSceneRange.x;
                float farPlane = _ReDefinitionSceneRange.y;
            #if UNITY_REVERSED_Z
                return nearPlane * farPlane / (depth * (farPlane - nearPlane) + nearPlane);
            #else
                return nearPlane * farPlane / (farPlane - depth * (farPlane - nearPlane));
            #endif
            }

            float2 SceneMotion(float4x4 clipToPrevClip, float2 uv, float depth)
            {
                float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
                return ReDefinitionMotionVector(clip, mul(clipToPrevClip, clip)).xy;
            }

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
                bool precise = _ReDefinitionSceneRange.w > 0.5;
                if (!nothing)
                {
                    if (precise && SceneDistance(depth) > _ReDefinitionSceneRange.z)
                        return float4(SceneMotion(_ReDefinitionSceneClipToPrevClip, uv, depth), 0.0, 0.0);
                    return float4(sceneMotion, 0.0, 0.0);
                }
                // Where nothing is drawn and no body met: the sky.
                float2 empty = precise ? SceneMotion(_ReDefinitionSkyClipToPrevClip, uv, depth) : sceneMotion;
                if (_ReDefinitionBodyCount < 0.5)
                    return float4(empty, 0.0, 0.0);

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
                    return float4(empty, 0.0, 0.0);

                float4 now = float4(origin + direction * nearest, 1.0);
                float4 before = mul(_ReDefinitionBodyMotions[index], now);
                half4 motion = ReDefinitionMotionVector(mul(_ReDefinitionScaledViewProjection, now),
                                                        mul(_ReDefinitionScaledPreviousViewProjection, before));
                return float4(motion.xy, 0.0, 0.0);
            }
            ENDCG
        }

        // 3: at the scene camera's BeforeImageEffectsOpaque, before anything reads
        // Unity's motion vectors, back into them (UnityMotionRepair in the plugin):
        // where the copy still holds the value the plugin cleared the buffer to at
        // the end of the frame before, Unity has not written the pixel, and the
        // camera's own move takes its place -- against the main body where
        // something is drawn, the camera's turn alone where nothing is. Unity's
        // own everywhere else.
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

            sampler2D _ReDefinitionRepairMotion;
            sampler2D _ReDefinitionRepairDepth;
            float4 _ReDefinitionRepairTexel;
            float4x4 _ReDefinitionSceneClipToPrevClip;
            float4x4 _ReDefinitionSkyClipToPrevClip;
            float4 _ReDefinitionSceneRange;

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionRepairTexel.xy;
                float2 unityMotion = tex2D(_ReDefinitionRepairMotion, uv).xy;
                if (unityMotion.x >= -50.0)
                    return float4(unityMotion, 0.0, 0.0);
                if (_ReDefinitionSceneRange.w < 0.5)
                    return float4(0.0, 0.0, 0.0, 0.0);
                float depth = tex2D(_ReDefinitionRepairDepth, uv).r;
            #if UNITY_REVERSED_Z
                bool nothing = depth <= 0.0;
            #else
                bool nothing = depth >= 1.0;
            #endif
                float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
                float4 before = mul(nothing ? _ReDefinitionSkyClipToPrevClip : _ReDefinitionSceneClipToPrevClip, clip);
                return float4(ReDefinitionMotionVector(clip, before).xy, 0.0, 0.0);
            }
            ENDCG
        }

        // 4: in place of pass 0 where EVE's reconstruction leaves no motion vectors
        // (CloudMotionVectors in the plugin), the same four values, the motion
        // worked out: each pixel's ray meets the cloud layer's shell around the main
        // body where it enters it -- from below at its base, from above at its top
        // -- or, from inside, half way to where it leaves it; that point, fixed to
        // the body, into the frame before's clip space through the camera's move
        // against the body. Without jitter, as the camera's own projection is
        // taken without it.
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

            sampler2D scattererReconstructedCloud;
            float _ReDefinitionCloudFade;
            float4 _ReDefinitionCloudTexel;
            // The scene camera's projection through GL.GetGPUProjectionMatrix(...,
            // false), without jitter, and its inverse.
            float4x4 _ReDefinitionCloudProjection;
            float4x4 _ReDefinitionCloudInverseProjection;
            // From this frame's camera space into the frame before's clip space.
            float4x4 _ReDefinitionCloudViewToPrevClip;
            // xyz the camera against the shell's centre, in camera space; w 1 while
            // there is a frame before to move from.
            float4 _ReDefinitionCloudOrigin;
            // |origin|^2 - r^2 for the shell's base (x) and top (y), in double on the
            // CPU: negative inside the sphere.
            float4 _ReDefinitionCloudShell;

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.pos.xy * _ReDefinitionCloudTexel.xy;
                float transmittance = tex2D(scattererReconstructedCloud, uv).a;
                transmittance = 1.0 - _ReDefinitionCloudFade * (1.0 - transmittance);
                if (_ReDefinitionCloudOrigin.w < 0.5)
                    return float4(0.0, 0.0, transmittance, 0.0);

                // Any depth inside the frustum gives a point on the pixel's ray.
                float4 onRay = mul(_ReDefinitionCloudInverseProjection, float4(uv * 2.0 - 1.0, 0.5, 1.0));
                float3 direction = normalize(onRay.xyz / onRay.w);
                float along = dot(_ReDefinitionCloudOrigin.xyz, direction);
                float baseDisc = along * along - _ReDefinitionCloudShell.x;
                float topDisc = along * along - _ReDefinitionCloudShell.y;
                float reach = -1.0;
                if (_ReDefinitionCloudShell.x < 0.0)
                {
                    // Below the base: where the ray reaches it.
                    reach = -along + sqrt(baseDisc);
                }
                else if (_ReDefinitionCloudShell.y > 0.0)
                {
                    // Above the top: where the ray enters, if in front.
                    if (topDisc >= 0.0)
                        reach = -along - sqrt(topDisc);
                }
                else
                {
                    // Inside: half way to the base below or the top above.
                    float toBase = baseDisc >= 0.0 ? -along - sqrt(baseDisc) : -1.0;
                    reach = 0.5 * (toBase > 0.0 ? toBase : -along + sqrt(max(topDisc, 0.0)));
                }
                if (reach <= 0.0)
                    return float4(0.0, 0.0, transmittance, 0.0);

                float4 onShell = float4(direction * reach, 1.0);
                half4 motion = ReDefinitionMotionVector(mul(_ReDefinitionCloudProjection, onShell),
                                                        mul(_ReDefinitionCloudViewToPrevClip, onShell));
                return float4(motion.xy, transmittance, 1.0);
            }
            ENDCG
        }
    }
}
