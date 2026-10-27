// A moon's disc made to stand out in the sky (src/Raytracing/Ksp/MoonGlow.cs). It works where the scene camera sees
// sky, before the transparent pass, so that clouds and the atmosphere draw over it, on a rectangle around the disc. It multiplies the pixels the
// moon's own sphere covers: the moon as the game draws it, brighter, with its phase, its terminator and its craters
// kept and no halo past its edge. A second pass adds the planet's light on the moon's dark side. It lights nothing.
// The shader takes each pixel's direction from the camera as it renders this frame: its projection and orientation,
// whatever its field of view, its render size or the upscaler's jitter. It sets that against the moon's direction and
// angular radius, so the disc lies where the sky draws the moon.
Shader "Hidden/ReDefinition/MoonGlow"
{
    CGINCLUDE
    #include "UnityCG.cginc"

    // The scene camera's depth: the moon is touched only where it holds the far plane, the sky.
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    // xyz: towards the moon, world space; w: its angular radius, radians.
    float4 _ReDefinitionMoon;
    // x: how many times as bright the moon's lit pixels become; y: the part of the sun the moon's shadow leaves them.
    float4 _ReDefinitionMoonGain;
    // xyz: from the moon towards the sun, world space.
    float4 _ReDefinitionMoonSun;
    // xyz: from the moon towards its planet, world space; rgb of the second vector: the planet's light on the moon, in
    // the frame's units, a: 0 where the moon has none to show.
    float4 _ReDefinitionMoonPlanet;
    float4 _ReDefinitionMoonEarthLight;
    // The bodies that can stand in front of the moon: centres relative to the camera, world space, radii in w (0 for none).
    float4 _ReDefinitionMoonBlockers[2];
    // 1 where Scatterer's ocean is drawn on the main body: its depth is its own.
    float _ReDefinitionMoonOcean;
    sampler2D oceanGbufferDepth;
    // How far the moon is from the camera, metres.
    float _ReDefinitionMoonDistance;
    // The rectangle that holds the moon's disc, in the camera's viewport coordinates (-1 to 1, y up): minimum then maximum.
    float4 _ReDefinitionMoonRect;

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

    // The quad's vertices are the screen's corners in clip space; they are spread over the moon's rectangle, y flipped as
    // the projection is for a render texture, and their screen position follows.
    v2f vert(float4 vertex : POSITION)
    {
        v2f o;
        float2 corner = lerp(_ReDefinitionMoonRect.xy, _ReDefinitionMoonRect.zw, vertex.xy * 0.5 + 0.5);
        o.pos = float4(corner.x, corner.y * _ProjectionParams.x, 0.0, 1.0);
        o.screen = ComputeScreenPos(o.pos);
        return o;
    }

    // How much of the pixel the moon's sphere covers (0 to 1, its edge one pixel wide), and the sphere's normal there.
    // It is 0 where the pixel shows no sky, a body stands in front of the moon or an ocean is drawn.
    float MoonCoverage(v2f i, out float3 normal)
    {
        normal = float3(0.0, 0.0, 1.0);
        float2 uv = i.screen.xy / i.screen.w;
        float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
    #if defined(UNITY_REVERSED_Z)
        if (depth > 0.0)
            return 0.0;
    #else
        if (depth < 1.0)
            return 0.0;
    #endif
        // The pixel's direction: its point on the far plane in the camera's view space (unity_CameraInvProjection,
        // OpenGL's: -z ahead), turned to the camera's space (+z ahead) and from there to the world's.
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
        float edge = max(fwidth(angle), 1e-6);
        float coverage = saturate((_ReDefinitionMoon.w - angle) / edge + 0.5);
        if (coverage <= 0.0)
            return 0.0;
        // The sphere at distance 1: its radius the sine of its angular radius; the point the ray meets, nearest the camera
        // (at the edge, the limb).
        float3 centre = normalize(_ReDefinitionMoon.xyz);
        float radius = sin(_ReDefinitionMoon.w);
        float along = dot(direction, centre);
        float closest2 = 1.0 - along * along;
        float3 hit = direction * (along - sqrt(max(radius * radius - closest2, 0.0)));
        normal = normalize(hit - centre);
        return coverage;
    }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        // Pass 0: the moon's lit pixels times the gain, where the sun lights the sphere (a rectangle per moon, MoonGlow's
        // command buffer). The unlit side stays as the game draws it; the terminator takes the gain on over a short way.
        Pass
        {
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(v2f i) : SV_Target
            {
                float3 normal;
                float coverage = MoonCoverage(i, normal);
                float lit = smoothstep(0.0, 0.25, dot(normal, normalize(_ReDefinitionMoonSun.xyz)));
                float gain = lerp(1.0, _ReDefinitionMoonGain.x * _ReDefinitionMoonGain.y, coverage * lit);
                return half4(gain, gain, gain, 1.0);
            }
            ENDCG
        }

        // Pass 1: the planet's light on the moon, added where the moon's sphere faces the planet.
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(v2f i) : SV_Target
            {
                float3 normal;
                float coverage = MoonCoverage(i, normal);
                float facing = saturate(dot(normal, normalize(_ReDefinitionMoonPlanet.xyz)));
                return half4(_ReDefinitionMoonEarthLight.rgb * (coverage * facing), 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
