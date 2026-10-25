// A moon's disc made to stand out in the sky (src/Raytracing/Ksp/MoonGlow.cs): a brighter
// disc and a soft glow round it, added where the scene camera sees sky, before the
// transparent pass, so that clouds and the atmosphere draw over it. It lights nothing.
// Each pixel's direction from the camera as it renders this frame -- its projection and
// orientation, whatever its field of view, its render size or the upscaler's jitter --
// against the moon's direction and angular radius: the glow lies on the disc as the sky
// draws it.
Shader "Hidden/ReDefinition/MoonGlow"
{
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Blend One One

        // The one pass: a quad per moon over the screen, added (MoonGlow's command buffer, before the transparent pass).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // The scene camera's depth: the glow is added only where it holds the far plane, the sky.
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            // xyz: towards the moon, world space; w: its angular radius, radians.
            float4 _ReDefinitionMoon;
            // rgb: the disc's colour as the frame's; a: how strongly it stands out.
            float4 _ReDefinitionMoonColour;
            // The bodies that can stand in front of the moon: centres relative to the
            // camera, world space, radii in w (0 for none).
            float4 _ReDefinitionMoonBlockers[2];
            // 1 where Scatterer's ocean is drawn on the main body: its depth is its own.
            float _ReDefinitionMoonOcean;
            sampler2D oceanGbufferDepth;

            // How far the moon is from the camera, metres.
            float _ReDefinitionMoonDistance;

            // Whether the ray meets the sphere nearer than the moon.
            bool Blocked(float3 direction, float4 sphere)
            {
                if (sphere.w <= 0.0)
                    return false;
                float along = dot(sphere.xyz, direction);
                float3 closest = sphere.xyz - direction * along;
                float inside = sphere.w * sphere.w - dot(closest, closest);
                return inside > 0.0 && along - sqrt(inside) > 0.0 && along - sqrt(inside) < _ReDefinitionMoonDistance;
            }

            // What the vertex function hands the fragment function.
            struct v2f
            {
                float4 pos : SV_POSITION; // the quad corner's clip position
                float4 screen : TEXCOORD0; // the screen position, divided by w in the fragment function
            };

            // The quad's vertices are the screen's corners in clip space already; their screen position follows.
            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = float4(vertex.xy, 0.0, 1.0);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            // The disc's and glow's light added to the pixel where it shows sky, no body stands in front of the moon
            // and no ocean is drawn; nothing elsewhere.
            half4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
            #if defined(UNITY_REVERSED_Z)
                if (depth > 0.0)
                    return 0.0;
            #else
                if (depth < 1.0)
                    return 0.0;
            #endif
                // The pixel's direction: its point on the far plane in the camera's view
                // space (unity_CameraInvProjection, OpenGL's: -z ahead), turned to the
                // camera's space (+z ahead) and from there to the world's.
                float4 view = mul(unity_CameraInvProjection, float4(uv * 2.0 - 1.0, 1.0, 1.0));
                float3 camera = float3(view.x, view.y, -view.z) / view.w;
                float3 direction = normalize(mul((float3x3)unity_CameraToWorld, camera));
                if (Blocked(direction, _ReDefinitionMoonBlockers[0]) || Blocked(direction, _ReDefinitionMoonBlockers[1]))
                    return 0.0;
                if (_ReDefinitionMoonOcean > 0.5)
                {
                    float ocean = tex2D(oceanGbufferDepth, uv).r;
                #if defined(UNITY_REVERSED_Z)
                    if (ocean > 0.0)
                        return 0.0;
                #else
                    if (ocean < 1.0)
                        return 0.0;
                #endif
                }
                float angle = acos(clamp(dot(direction, _ReDefinitionMoon.xyz), -1.0, 1.0));
                float r = angle / max(_ReDefinitionMoon.w, 1e-5);
                // The disc a little brighter, its edge soft; round it a glow falling off
                // over a few radii.
                float disc = 1.0 - smoothstep(0.85, 1.05, r);
                float glow = exp(-max(r - 1.0, 0.0) * 1.2) * (1.0 - disc);
                return half4(_ReDefinitionMoonColour.rgb * _ReDefinitionMoonColour.a * (1.2 * disc + 0.6 * glow), 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
