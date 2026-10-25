// Modified version of Unity built-in shader source.
// This adds the diffuse ambient evaluation here
//
// Deferred's reflections shader (Deferred 1.3.5, Copyright (c) 2024-2025 Ghassen
// Lahmar, GPL 3.0), changed for ReDefinition (docs/development/raytracing.md, "Where
// the raytraced light enters Unity's lighting"): for the camera that is raytraced,
// the ambient light is the raytraced diffuse light and the reflection the raytraced
// one, found for each point by ReDefinitionRtReproject.cginc -- the game's own frame's,
// traced before its lighting; the frame before's, carried over, where the trace follows
// the frame (the test players) --, in place of the reflection probe (the block marked
// ReDefinition in frag). Everything else is Deferred's. Its name is kept apart: ReDefinition/Internal-DeferredReflections.
Shader "ReDefinition/Internal-DeferredReflections" {
Properties {
    _SrcBlend ("", Float) = 1
    _DstBlend ("", Float) = 1
}
SubShader {

// Calculates reflection contribution from a single probe (rendered as cubes) or default reflection (rendered as full screen quad)
Pass {
    ZWrite Off
    ZTest LEqual
    Blend [_SrcBlend] [_DstBlend]
CGPROGRAM
#pragma target 3.0
#pragma vertex vert_deferred
#pragma fragment frag

#include "UnityCG.cginc"
#include "UnityDeferredLibrary.cginc"
#include "UnityStandardUtils.cginc"
#include "UnityGBuffer.cginc"
#include "UnityStandardBRDF.cginc"
#include "UnityPBSLighting.cginc"

// Unity's G-buffer: diffuse colour and occlusion, specular colour and smoothness, normal.
sampler2D _CameraGBufferTexture0;
sampler2D _CameraGBufferTexture1;
sampler2D _CameraGBufferTexture2;

// Deferred's switches for the camera being drawn: the probe's light at all, the probe's reflections, its
// screen-space reflections in their place; the probe's diffuse light's strength and how much of its colour
// is kept; the IVA's rotation.
int useReflectionProbeOnCurrentCamera;
int disableProbeReflectionsOnCurrentCamera;
int useSSROnCurrentCamera;
float deferredAmbientBrightness, deferredAmbientTint;
float4x4 internalSpaceToWorld; // Transform from internal space to world if on IVA camera, identity matrix otherwise

// How far a point lies outside a box along each axis, 0 inside it.
half3 distanceFromAABB(half3 p, half3 aabbMin, half3 aabbMax)
{
    return max(max(p - aabbMax, aabbMin - p), half3(0.0, 0.0, 0.0));
}

// KSP's ambient light without Deferred, the floor the probe's diffuse light is mixed up from.
float3 legacyAmbientColor;

// ReDefinition: the raytraced diffuse light and reflections, set around the raytraced
// camera's rendering (RtCamera); _ReDefinitionRtLight says which of them to use.
#include "ReDefinitionRtReproject.cginc"
float4 _ReDefinitionRtLight;   // x: diffuse light, y: reflections, z: the share of KSP's night light kept
// KSP's own sea, where the raytracing takes it as water (RtCamera, StockWater): the main
// body's centre in Unity's world and its radius, the sea level; w 0 for none
// (IsStockSea).
float4 _ReDefinitionWaterSphere;
// 1: where the sea could be, which of the sea's tests (IsStockSea) a pixel fails, as
// colour far over white (the light probe's -water-debug shots, src/Probe/LightProbe.cs):
// red outside the band about the sea's level, green its normal not facing up, blue not
// traced as water; none where it passes all.
float _ReDefinitionWaterDebug;
// Its reflections from the water's own trace, whole and undenoised (RtCamera, TraceOcean).
sampler2D _ReDefinitionRtOcean;
sampler2D _ReDefinitionRtDiffuse;
sampler2D _ReDefinitionRtSpecular;

// Whether a point is KSP's own sea, as the raytracing took it (its water frame holds the
// reflected rays' hit distance in w where it traced water, 0 elsewhere: Raytracing.hlsl,
// SpecularLight, with RtCommon.hlsli's IsStockWater on positions it rebuilds exactly),
// facing up, and near the sea's level, against a hull beside the water. The level test
// loose, as the position this pass rebuilds from Unity's depth is off by a share of the
// distance, a height error of about that share of the camera's height over the sea in
// every pixel (measured at sea, 2026-10-04: from 5 km up the sea in front failed a band
// of a thousandth of the distance and only the far sea kept its reflection, at an edge):
// within 2 m, a thousandth of the distance or a hundredth of the camera's height.
// `outside`, `facing`: the level and normal tests, for the debug view.
bool IsStockSea(float3 worldPos, float3 normal, float4 traced, out bool outside, out bool facing)
{
    float3 up = worldPos - _ReDefinitionWaterSphere.xyz;
    float level = length(up);
    float cameraHeight = length(_WorldSpaceCameraPos - _ReDefinitionWaterSphere.xyz) - _ReDefinitionWaterSphere.w;
    float band = max(max(2.0, 0.001 * length(worldPos - _WorldSpaceCameraPos)), 0.01 * cameraHeight);
    outside = abs(level - _ReDefinitionWaterSphere.w) >= band;
    facing = dot(normal, up / level) > 0.8;
    return _ReDefinitionWaterSphere.w > 0.0 && traced.w > 0.0 && !outside && facing;
}

// The raytraced signals are linear light (the raytracing's RtCommon.hlsli, InputColor):
// in gamma space, as KSP renders, taken to the values Unity's lighting works with here.
// Unity's lighting multiplies them by the albedo in gamma space, which for a product is
// the same as multiplying in linear and converting the result.
float3 ReDefinitionRtSignal(float3 linearLight)
{
#ifdef UNITY_COLORSPACE_GAMMA
    return LinearToGammaSpace(max(linearLight, 0.0));
#else
    return linearLight;
#endif
}

// One probe's reflection and Deferred's diffuse ambient light at the pixel, the raytraced light in their
// place where the raytraced frame has the point (or KSP's sea lit by its own reflection); alpha the
// falloff at the probe's box. Nothing where Deferred has the probe off for the camera.
half4 frag (unity_v2f_deferred i) : SV_Target
{
    [branch]
    if (useReflectionProbeOnCurrentCamera == 0)
    {
        return 0.0.xxxx;
    }

    // Stripped from UnityDeferredCalculateLightParams, refactor into function ?
    i.ray = i.ray * (_ProjectionParams.z / i.ray.z);
    float2 uv = i.uv.xy / i.uv.w;

    // read depth and reconstruct world position
    float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
    depth = Linear01Depth (depth);
    float4 viewPos = float4(i.ray * depth,1);
    float3 worldPos = mul (unity_CameraToWorld, viewPos).xyz;

    half4 gbuffer0 = tex2D (_CameraGBufferTexture0, uv);
    half4 gbuffer1 = tex2D (_CameraGBufferTexture1, uv);
    half4 gbuffer2 = tex2D (_CameraGBufferTexture2, uv);
    UnityStandardData data = UnityStandardDataFromGbuffer(gbuffer0, gbuffer1, gbuffer2);
    // In the game the G-buffer's specular colour and smoothness are the surfaces' as their
    // materials mean them, written back after the raytracing found them (RtCamera).

    float3 eyeVec = normalize(worldPos - _WorldSpaceCameraPos);
    half oneMinusReflectivity = 1 - SpecularStrength(data.specularColor);

    UnityLight light;
    light.color = half3(0, 0, 0);
    light.dir = half3(0, 1, 0);

    UnityIndirect ind;
    ind.diffuse = 0;
    ind.specular = 0;

    float blendDistance = unity_SpecCube1_ProbePosition.w; // will be set to blend distance for this probe

    // The reflection probe is only ever rendered in one space, not matching IVA orientation
    // Rotate normals when in IVA to sample the probe correctly
    float3 actualWorldNormal = data.normalWorld;
    data.normalWorld = mul(internalSpaceToWorld, float4(data.normalWorld, 0.0));

    // Unused member don't need to be initialized
    UnityGIInput d;
    d.worldPos = mul(internalSpaceToWorld, worldPos);
    d.worldViewDir = mul(internalSpaceToWorld, float4(-eyeVec, 0.0));
    d.probeHDR[0] = unity_SpecCube0_HDR;
    d.boxMin[0].w = 1; // 1 in .w allow to disable blending in UnityGI_IndirectSpecular call since it doesn't work in Deferred
        
    #ifdef UNITY_SPECCUBE_BOX_PROJECTION
    d.probePosition[0]  = unity_SpecCube0_ProbePosition;
    d.boxMin[0].xyz     = unity_SpecCube0_BoxMin - float4(blendDistance,blendDistance,blendDistance,0);
    d.boxMax[0].xyz     = unity_SpecCube0_BoxMax + float4(blendDistance,blendDistance,blendDistance,0);
    #endif

    [branch]
    if (useSSROnCurrentCamera == 0.0 && disableProbeReflectionsOnCurrentCamera == 0.0)
    {
        Unity_GlossyEnvironmentData g = UnityGlossyEnvironmentSetup(data.smoothness, d.worldViewDir, data.normalWorld, data.specularColor);
        ind.specular = UnityGI_IndirectSpecular(d, data.occlusion, g);
    }

    ind.diffuse = deferredAmbientBrightness * Unity_GlossyEnvironment (UNITY_PASS_TEXCUBE(unity_SpecCube0), d.probeHDR[0], data.normalWorld, 0.55);
    ind.diffuse = lerp(length(ind.diffuse).xxx, ind.diffuse, deferredAmbientTint); // Limit the tint because it overpowers other colors without white balance

    data.normalWorld = actualWorldNormal;

    float diffuseMagnitude = length(ind.diffuse);

    float legacyAmbientMagnitude = length(legacyAmbientColor);

    ind.diffuse = lerp(legacyAmbientColor, ind.diffuse, saturate((diffuseMagnitude - legacyAmbientMagnitude) * 100.0));

    ind.diffuse *= data.occlusion; // Take the occlusion into account for the one IVA where it's used and for AO

    // ReDefinition: the raytraced light in place of the probe's, where the raytraced
    // frame has this point; looked for only where either signal is used.
    float2 rtUv = 0.0;
    bool rtFilled = false;
    [branch]
    if (_ReDefinitionRtLight.x > 0.5 || _ReDefinitionRtLight.y > 0.5)
        rtFilled = RtUvFilled(worldPos, rtUv);
    [branch]
    if (_ReDefinitionWaterDebug > 0.5 && _ReDefinitionWaterSphere.w > 0.0)
    {
        bool outside, facing;
        float4 traced = rtFilled ? tex2Dlod(_ReDefinitionRtOcean, float4(rtUv, 0.0, 0.0)) : 0.0;
        IsStockSea(worldPos, data.normalWorld, traced, outside, facing);
        return half4(outside ? 50.0 : 0.0, facing ? 0.0 : 50.0, traced.w > 0.0 ? 0.0 : 50.0, 1.0);
    }
    if (rtFilled)
    {
        if (_ReDefinitionRtLight.x > 0.5)
        {
            // Never below the player's share of KSP's own night light: mixed with
            // Deferred's legacy ambient as Deferred mixes its probe's.
            float3 raytraced = ReDefinitionRtSignal(tex2Dlod(_ReDefinitionRtDiffuse, float4(rtUv, 0.0, 0.0)).rgb);
            // Deferred's limit on the ambient light's tint, as it applies it to the probe's
            // above, keeping the light's strength (a grey of the same length): with
            // _ReDefinitionRtLight.w at 1, the raytraced light's colour as far from grey as
            // Deferred lets its own be.
            float3 grey = length(raytraced).xxx * 0.57735027;
            raytraced = lerp(raytraced, lerp(grey, raytraced, saturate(deferredAmbientTint)), saturate(_ReDefinitionRtLight.w));
            // With w above 1, up to 2: the raytraced light's strength in the colour of
            // Deferred's own ambient light at this point (ind.diffuse above, before the
            // raytraced light takes its place), its colours as without the raytracing.
            float deferredLength = length(ind.diffuse);
            if (deferredLength > 1e-5)
                raytraced = lerp(raytraced, length(raytraced) * ind.diffuse / deferredLength, saturate(_ReDefinitionRtLight.w - 1.0));
            // From 2 to 3: more and more grey, its strength kept -- in a shadow the surface
            // keeps its own colour.
            raytraced = lerp(raytraced, length(raytraced).xxx * 0.57735027, saturate(_ReDefinitionRtLight.w - 2.0));
            float3 nightLight = legacyAmbientColor * _ReDefinitionRtLight.z;
            raytraced = lerp(nightLight, raytraced, saturate((length(raytraced) - length(nightLight)) * 100.0));
            ind.diffuse = raytraced * data.occlusion;
        }
        if (_ReDefinitionRtLight.y > 0.5)
        {
            float3 reflected = tex2Dlod(_ReDefinitionRtSpecular, float4(rtUv, 0.0, 0.0)).rgb;
            // KSP's own sea (IsStockSea): its reflection by water's Fresnel term (Schlick,
            // 0.02 head on), in linear light, added to its lighting without one -- the
            // raytracing traced it as water, with the sun's and the moons' discs
            // (ReDefinition turns the sea shader's own highlight off).
            float4 sea = tex2Dlod(_ReDefinitionRtOcean, float4(rtUv, 0.0, 0.0));
            bool outside, facing;
            if (IsStockSea(worldPos, data.normalWorld, sea, outside, facing))
            {
                reflected = sea.rgb;
                float nv = saturate(dot(data.normalWorld, -eyeVec));
                float fresnel = 0.02 + 0.98 * pow(1.0 - nv, 5.0);
                ind.specular = 0;
                half3 lit = UNITY_BRDF_PBS (data.diffuseColor, data.specularColor, oneMinusReflectivity, data.smoothness, data.normalWorld, -eyeVec, light, ind).rgb;
                // At most four times white, its colour kept: a glint of the sun is white on
                // the screen as the sun's own disc is, and far brighter only spreads the
                // bloom of post-processing (TUFX) over the sea into a smooth white band
                // (measured at sea, 2026-10-04: the traced path 100 px wide, the frame's 190).
                float3 waterLight = max(reflected, 0.0) * fresnel * data.occlusion;
                waterLight *= min(1.0, 4.0 / max(max(waterLight.r, max(waterLight.g, waterLight.b)), 1e-6));
                float3 water = ReDefinitionRtSignal(waterLight);
                half3 distanceToBox = distanceFromAABB(worldPos, unity_SpecCube0_BoxMin.xyz, unity_SpecCube0_BoxMax.xyz);
                return half4(lit + water, saturate(1.0 - length(distanceToBox) / blendDistance));
            }
            ind.specular = ReDefinitionRtSignal(reflected) * data.occlusion;
        }
    }

    half3 rgb = UNITY_BRDF_PBS (data.diffuseColor, data.specularColor, oneMinusReflectivity, data.smoothness, data.normalWorld, -eyeVec, light, ind).rgb;

    // Calculate falloff value, so reflections on the edges of the probe would gradually blend to previous reflection.
    // Also this ensures that pixels not located in the reflection probe AABB won't
    // accidentally pick up reflections from this probe.
    half3 distance = distanceFromAABB(worldPos, unity_SpecCube0_BoxMin.xyz, unity_SpecCube0_BoxMax.xyz);
    half falloff = saturate(1.0 - length(distance)/blendDistance);

    return half4(rgb, falloff);
}

ENDCG
}

// Adds reflection buffer to the lighting buffer
Pass
{
    ZWrite Off
    ZTest Always
    Blend [_SrcBlend] [_DstBlend]

    Cull Off

    ColorMask RGB // Do not overwrite emission alpha channel flagging actual surfaces from depthMasks in LDR

    CGPROGRAM
        #pragma target 3.0
        #pragma vertex vert
        #pragma fragment frag
        #pragma multi_compile ___ UNITY_HDR_ON

        #include "UnityCG.cginc"

        // Unity's reflection buffer, which the first pass filled probe by probe.
        sampler2D _CameraReflectionsTexture;

        // What the vertex function hands the fragment function.
        struct v2f {
            float2 uv : TEXCOORD0; // the screen position the reflections are read at
            float4 pos : SV_POSITION; // the quad's clip position
        };

        // A full-screen quad, its screen position as the uv.
        v2f vert (float4 vertex : POSITION)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(vertex);
            o.uv = ComputeScreenPos (o.pos).xy;
            return o;
        }

        // The gathered reflections as the lighting buffer adds them: as they are in HDR, decoded from the
        // logarithmic LDR buffer (exp2 of minus the value) otherwise.
        half4 frag (v2f i) : SV_Target
        {
            half4 c = tex2D (_CameraReflectionsTexture, i.uv);
            #ifdef UNITY_HDR_ON
            return float4(c.rgb, 0.0f);
            #else
            return float4(exp2(-c.rgb), 0.0f);
            #endif

        }
    ENDCG
}

}
Fallback Off
}
