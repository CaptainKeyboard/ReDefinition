// The active vessel's shadow for frame generation (VesselShadowLayer in the plugin).
// Built into redefinition.shaders by BundleBuilder.
//
// Frame generation moves every pixel with its motion vector, which is the motion of the
// surface. The vessel's shadow on that surface moves with the vessel instead; on a runway
// under a vessel the camera follows it stands still on the screen while the ground under
// it runs, and the generated frames drag it along with the ground. The HUD-less image
// frame generation interpolates is given the scene with that shadow taken out, where
// that is the smaller error, so the difference to the frame -- the shadow -- is treated
// like the interface and not moved.
//
// Pass 0 draws the vessel's renderers from the sun into the light map: how far the
// surface lies along the light, and how far it moved since the frame before.
// Pass 1, at render size after the scene camera's lighting, finds per pixel how much of
// the sun the vessel takes (Unity's own shadow mask, where the light map says the
// vessel is in the way), where that bit of shadow lay in the frame before, and from it
// the weight with which the shadow leaves the HUD-less image.
// Passes 2 and 3 gather, at display size, the colour in full shadow and on lit ground
// near it; their mip chains give the local mean of each.
// Pass 4 writes the HUD-less image: the colour brightened by the local ratio of lit to
// shadowed colour, in proportion to the weight.
Shader "Hidden/ReDefinition/VesselShadow"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }
    SubShader
    {
        // 0: the light map. xyz the surface's position in the frame before minus its
        // position now, w its distance along the light from the map's near plane.
        Pass
        {
            ZTest LEqual
            ZWrite On
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"

            // The light's view-projection as Unity makes it for drawing into a render
            // texture (GL.GetGPUProjectionMatrix(projection, true)).
            float4x4 _VesselShadowLightRaster;
            // xyz the direction towards the light, w the map's near plane distance from
            // the origin along it (the distance a point on the near plane has).
            float4 _VesselShadowLight;
            float4x4 _VesselShadowPreviousModel;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 moved : TEXCOORD0;
                float distance : TEXCOORD1;
            };

            v2f vert (float4 vertex : POSITION)
            {
                float4 local = float4(vertex.xyz, 1.0);
                float3 world = mul(unity_ObjectToWorld, local).xyz;
                float3 before = mul(_VesselShadowPreviousModel, local).xyz;
                v2f o;
                o.pos = mul(_VesselShadowLightRaster, float4(world, 1.0));
                o.moved = before - world;
                o.distance = _VesselShadowLight.w - dot(world, _VesselShadowLight.xyz);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                return float4(i.moved, i.distance);
            }
            ENDCG
        }

        // 1: the weight, at render size. r the weight, g the share of the sun the vessel
        // takes, b lit ground near the vessel's shadow.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "../Include/ReDefinition.cginc"

            sampler2D _VesselShadowDepth;
            sampler2D _VesselShadowMotion;
            sampler2D _VesselShadowMask;
            sampler2D _VesselShadowLightMap;
            // The inverse of the scene camera's jittered view-projection, y up
            // (GL.GetGPUProjectionMatrix(projection, false)): a texture coordinate and
            // the depth there back to the world. Unity samples a render texture upright
            // with plain texture coordinates.
            float4x4 _VesselShadowRasterInverse;
            float4x4 _VesselShadowViewProjection;
            float4x4 _VesselShadowPreviousViewProjection;
            // The light's view-projection, y up, for reading the light map.
            float4x4 _VesselShadowLightViewProjection;
            float4 _VesselShadowLight;
            // 1 / width, 1 / height, width, height of the render size.
            float4 _VesselShadowTexel;
            // x the light's shadow strength, y 1 / light map size, z the depth bias in
            // metres, w how far from the shadow lit ground counts as near it, in light map
            // texels.
            float4 _VesselShadowParams;
            // x how far from a point the vessel may stand in the light and still take part
            // in its shadow -- as wide as the soft edge Unity draws -- in light map texels.
            float4 _VesselShadowReach;

            float3 WorldAt(float2 uv)
            {
                float depth = tex2Dlod(_VesselShadowDepth, float4(uv, 0.0, 0.0)).r;
                float4 clip = float4(uv * 2.0 - 1.0, depth, 1.0);
                float4 world = mul(_VesselShadowRasterInverse, clip);
                return world.xyz / world.w;
            }

            // How far along the light a point lies, and where it falls in the light map.
            float2 LightUv(float3 world)
            {
                float4 clip = mul(_VesselShadowLightViewProjection, float4(world, 1.0));
                return clip.xy / clip.w * 0.5 + 0.5;
            }

            float4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float depth = tex2Dlod(_VesselShadowDepth, float4(uv, 0.0, 0.0)).r;
                // Reversed depth: 0 is the far plane, nothing stands there.
                if (depth <= 0.0)
                    return 0.0;

                float3 world = WorldAt(uv);
                float distance = _VesselShadowLight.w - dot(world, _VesselShadowLight.xyz);
                float2 lightUv = LightUv(world);
                if (any(lightUv < 0.0) || any(lightUv > 1.0))
                    return 0.0;

                // The vessel in the way: texels nearer the light than the point by more than
                // the bias, on a 5x5 grid as wide as Unity's soft shadow edge. Whether the
                // vessel takes part in the shadow here; the mean motion and distance of
                // those texels are the occluder's.
                float blocked = 0.0;
                float3 moved = 0.0;
                float occluder = 0.0;
                float nearby = 0.0;
                float texelStep = _VesselShadowParams.y;
                [unroll] for (int y = -2; y <= 2; y++)
                {
                    [unroll] for (int x = -2; x <= 2; x++)
                    {
                        float2 offset = float2(x, y) * (_VesselShadowReach.x * 0.5) * texelStep;
                        float4 texel = tex2Dlod(_VesselShadowLightMap, float4(lightUv + offset, 0.0, 0.0));
                        if (texel.w < distance - _VesselShadowParams.z)
                        {
                            blocked += 1.0;
                            moved += texel.xyz;
                            occluder += texel.w;
                        }
                    }
                }
                // Near the shadow: the vessel in the way somewhere within the reach -- eight
                // directions at a third, two thirds and all of it -- for the lit ground the
                // ratio is measured against.
                [unroll] for (int ring = 1; ring <= 3; ring++)
                {
                    [unroll] for (int k = 0; k < 8; k++)
                    {
                        float angle = k * 0.785398;
                        float2 offset = float2(cos(angle), sin(angle)) * (_VesselShadowParams.w * ring / 3.0) * texelStep;
                        float4 texel = tex2Dlod(_VesselShadowLightMap, float4(lightUv + offset, 0.0, 0.0));
                        if (texel.w < distance - _VesselShadowParams.z)
                            nearby = 1.0;
                    }
                }

                float mask = tex2Dlod(_VesselShadowMask, float4(uv, 0.0, 0.0)).r;
                float lit = nearby * step(0.98, mask) * (blocked < 0.5 ? 1.0 : 0.0);
                if (blocked < 0.5)
                    return float4(0.0, 0.0, lit, 0.0);

                // The share of the sun the vessel takes, where it takes part: Unity's mask is
                // 1 - strength in full shadow and 1 in full light, and Unity lights a point
                // with ambient + direct * mask, which the composition undoes exactly.
                float taken = saturate((1.0 - mask) / max(_VesselShadowParams.x, 1e-3));
                moved /= blocked;
                occluder /= blocked;

                // Where this bit of shadow lay in the frame before: the occluding point, as
                // far along the light as the light map says, moved as it moved, and its
                // shadow along the light onto the surface here -- a plane through this
                // point with the surface's normal, from the depth around it.
                float3 towardsLight = _VesselShadowLight.xyz;
                float3 occluding = world + towardsLight * (distance - occluder);
                float3 dx = WorldAt(uv + float2(_VesselShadowTexel.x, 0.0)) - world;
                float3 dy = WorldAt(uv + float2(0.0, _VesselShadowTexel.y)) - world;
                float3 normal = normalize(cross(dx, dy));
                float facing = dot(towardsLight, normal);
                float3 before = world;
                if (abs(facing) > 0.05)
                {
                    float3 occludingBefore = occluding + moved;
                    float along = dot(occludingBefore - world, normal) / facing;
                    before = occludingBefore - towardsLight * along;
                }

                // Both motions in pixels, the encoding the motion vectors have.
                float2 shadowMotion = ReDefinitionMotionVector(mul(_VesselShadowViewProjection, float4(world, 1.0)),
                                                               mul(_VesselShadowPreviousViewProjection, float4(before, 1.0))).xy
                                      * _VesselShadowTexel.zw;
                float2 surfaceMotion = tex2Dlod(_VesselShadowMotion, float4(uv, 0.0, 0.0)).xy * _VesselShadowTexel.zw;

                // Left in place (as the interface) the shadow is off by its own motion;
                // moved with the surface, by the difference. The weight goes over from one
                // to the other with the squares of the two errors: 1 where the shadow stands,
                // 0 where it moves with the surface, a half where both are off alike, and 0
                // where nothing moves, a quarter pixel squared keeping it defined.
                float still = dot(shadowMotion, shadowMotion);
                float dragged = dot(shadowMotion - surfaceMotion, shadowMotion - surfaceMotion);
                float choose = dragged / (dragged + still + 0.25);

                return float4(taken * choose, taken, 0.0, choose);
            }
            ENDCG
        }

        // 2: the colour in full shadow, weighted, into a texture with mips.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _VesselShadowWeight;

            float4 frag (v2f_img i) : SV_Target
            {
                float taken = tex2D(_VesselShadowWeight, i.uv).g;
                float full = saturate((taken - 0.9) * 10.0);
                return float4(tex2D(_MainTex, i.uv).rgb * full, full);
            }
            ENDCG
        }

        // 3: the colour of lit ground near the shadow, weighted.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _VesselShadowWeight;

            float4 frag (v2f_img i) : SV_Target
            {
                float lit = tex2D(_VesselShadowWeight, i.uv).b;
                return float4(tex2D(_MainTex, i.uv).rgb * lit, lit);
            }
            ENDCG
        }

        // 4: the HUD-less image.
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _VesselShadowWeight;
            sampler2D _VesselShadowShadowed;
            sampler2D _VesselShadowLit;
            // x the finest mip the local means are read at, y the coarsest.
            float4 _VesselShadowMips;

            // The local ratio of shadowed to lit colour, per channel, at the finest mip
            // where both are there; 1 where there is nothing to compare.
            float3 Ratio(float2 uv)
            {
                [loop] for (float mip = _VesselShadowMips.x; mip <= _VesselShadowMips.y; mip += 1.0)
                {
                    float4 shadowed = tex2Dlod(_VesselShadowShadowed, float4(uv, 0.0, mip));
                    float4 lit = tex2Dlod(_VesselShadowLit, float4(uv, 0.0, mip));
                    if (shadowed.a > 1e-3 && lit.a > 1e-3)
                    {
                        float3 s = shadowed.rgb / shadowed.a;
                        float3 l = lit.rgb / lit.a;
                        return saturate(s / max(l, 1e-3));
                    }
                }
                return 1.0;
            }

            float4 frag (v2f_img i) : SV_Target
            {
                float4 colour = tex2D(_MainTex, i.uv);
                float weight = tex2D(_VesselShadowWeight, i.uv).r;
                if (weight < 1e-3)
                    return colour;
                float3 ratio = max(Ratio(i.uv), 0.05);
                colour.rgb /= 1.0 - weight * (1.0 - ratio);
                return saturate(colour);
            }
            ENDCG
        }
    }
}
