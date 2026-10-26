// Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)
//
// Changed for ReDefinition (docs/development/flow-raytracing.md, "Where the raytraced light
// enters Unity's lighting"): for the camera that is raytraced, the mask holds the
// raytraced sun visibility, found for each point by ReDefinitionRtReproject.cginc --
// the game's own frame's, traced before its lighting; the frame before's, carried over,
// where the trace follows the frame (the test players) --, in place of the cascaded
// shadow maps (RtShadow, and its two calls in frag_hard and frag_pcfSoft). Every other
// camera collects its shadows as Unity's shader does. Unity's name is kept apart: Hidden/ReDefinition/ScreenSpaceShadows.

// Collects cascaded shadows into screen space buffer
Shader "Hidden/ReDefinition/ScreenSpaceShadows" {
Properties {
    _ShadowMapTexture ("", any) = "" {}
    _ODSWorldTexture("", 2D) = "" {}
}

CGINCLUDE

// The sun's cascaded shadow map and its texel size, declared before UnityShadowLibrary.cginc so that its PCF
// filters, which read both, are compiled.
UNITY_DECLARE_SHADOWMAP(_ShadowMapTexture);
float4 _ShadowMapTexture_TexelSize;
#define SHADOWMAPSAMPLER_AND_TEXELSIZE_DEFINED // UnityShadowLibrary.cginc compiles its PCF filters only with it
sampler2D _ODSWorldTexture; // the world positions of a stereo cubemap render

#include "UnityCG.cginc"
#include "UnityShadowLibrary.cginc"

// Configuration


// Should receiver plane bias be used? This estimates receiver slope using derivatives,
// and tries to tilt the PCF kernel along it. However, since we're doing it in screenspace
// from the depth texture, the derivatives are wrong on edges or intersections of objects,
// leading to possible shadow artifacts. So it's disabled by default.
// See also UnityGetReceiverPlaneDepthBias in UnityShadowLibrary.cginc.
//#define UNITY_USE_RECEIVER_PLANE_BIAS


// Blend between shadow cascades to hide the transition seams?
#define UNITY_USE_CASCADE_BLENDING 0
#define UNITY_CASCADE_BLEND_DISTANCE 0.1


// The full-screen quad's vertex as Unity draws it for the shadow collection, with each corner's ray.
struct appdata {
    // The quad's position and uv as Unity draws it.
    float4 vertex : POSITION;
    float2 texcoord : TEXCOORD0;
#ifdef UNITY_STEREO_INSTANCING_ENABLED
    float3 ray0 : TEXCOORD1; // the left eye's view-space ray
    float3 ray1 : TEXCOORD2; // the right eye's view-space ray
#else
    float3 ray : TEXCOORD1; // the view-space ray to the far plane
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// What the vertex function hands the fragment functions.
struct v2f {

    float4 pos : SV_POSITION; // the quad's clip position

    // xy uv / zw screenpos
    float4 uv : TEXCOORD0;
    // View space ray, for perspective case
    float3 ray : TEXCOORD1;
    // Orthographic view space positions (need xy as well for oblique matrices)
    float3 orthoPosNear : TEXCOORD2;
    float3 orthoPosFar  : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// The full-screen quad: its uv and screen position, its view-space ray, and the orthographic near and far
// positions for the fragment function.
v2f vert (appdata v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    float4 clipPos;
#if defined(STEREO_CUBEMAP_RENDER_ON)
    clipPos = mul(UNITY_MATRIX_VP, mul(unity_ObjectToWorld, v.vertex));
#else
    clipPos = UnityObjectToClipPos(v.vertex);
#endif
    o.pos = clipPos;
    o.uv.xy = v.texcoord;

    // unity_CameraInvProjection at the PS level.
    o.uv.zw = ComputeNonStereoScreenPos(clipPos);

    // Perspective case
#ifdef UNITY_STEREO_INSTANCING_ENABLED
    o.ray = unity_StereoEyeIndex == 0 ? v.ray0 : v.ray1;
#else
    o.ray = v.ray;
#endif

    // To compute view space position from Z buffer for orthographic case,
    // we need different code than for perspective case. We want to avoid
    // doing matrix multiply in the pixel shader: less operations, and less
    // constant registers used. Particularly with constant registers, having
    // unity_CameraInvProjection in the pixel shader would push the PS over SM2.0
    // limits.
    clipPos.y *= _ProjectionParams.x;
    float3 orthoPosNear = mul(unity_CameraInvProjection, float4(clipPos.x,clipPos.y,-1,1)).xyz;
    float3 orthoPosFar  = mul(unity_CameraInvProjection, float4(clipPos.x,clipPos.y, 1,1)).xyz;
    orthoPosNear.z *= -1;
    orthoPosFar.z *= -1;
    o.orthoPosNear = orthoPosNear;
    o.orthoPosFar = orthoPosFar;

    return o;
}

// ------------------------------------------------------------------
//  Helpers
// ------------------------------------------------------------------
// The camera's depth, from which each pixel's position is rebuilt.
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
// sizes of cascade projections, relative to first one
float4 unity_ShadowCascadeScales;

//
// Keywords based defines
//
#if defined (SHADOWS_SPLIT_SPHERES)
    // The cascade a world position falls in, chosen by the split spheres.
    #define GET_CASCADE_WEIGHTS(wpos, z)    getCascadeWeights_splitSpheres(wpos)
#else
    // The cascade a world position falls in, chosen by its view depth z.
    #define GET_CASCADE_WEIGHTS(wpos, z)    getCascadeWeights( wpos, z )
#endif

#if defined (SHADOWS_SINGLE_CASCADE)
    // A single cascade: the shadow map coordinates of a world position.
    #define GET_SHADOW_COORDINATES(wpos,cascadeWeights) getShadowCoord_SingleCascade(wpos)
#else
    // The shadow map coordinates of a world position in the cascade the weights pick.
    #define GET_SHADOW_COORDINATES(wpos,cascadeWeights) getShadowCoord(wpos,cascadeWeights)
#endif

/**
 * Gets the cascade weights based on the world position of the fragment.
 * Returns a float4 with only one component set that corresponds to the appropriate cascade.
 */
inline fixed4 getCascadeWeights(float3 wpos, float z)
{
    fixed4 zNear = float4( z >= _LightSplitsNear );
    fixed4 zFar = float4( z < _LightSplitsFar );
    fixed4 weights = zNear * zFar;
    return weights;
}

/**
 * Gets the cascade weights based on the world position of the fragment and the poisitions of the split spheres for each cascade.
 * Returns a float4 with only one component set that corresponds to the appropriate cascade.
 */
inline fixed4 getCascadeWeights_splitSpheres(float3 wpos)
{
    float3 fromCenter0 = wpos.xyz - unity_ShadowSplitSpheres[0].xyz;
    float3 fromCenter1 = wpos.xyz - unity_ShadowSplitSpheres[1].xyz;
    float3 fromCenter2 = wpos.xyz - unity_ShadowSplitSpheres[2].xyz;
    float3 fromCenter3 = wpos.xyz - unity_ShadowSplitSpheres[3].xyz;
    float4 distances2 = float4(dot(fromCenter0,fromCenter0), dot(fromCenter1,fromCenter1), dot(fromCenter2,fromCenter2), dot(fromCenter3,fromCenter3));
    fixed4 weights = float4(distances2 < unity_ShadowSplitSqRadii);
    weights.yzw = saturate(weights.yzw - weights.xyz);
    return weights;
}

/**
 * Returns the shadowmap coordinates for the given fragment based on the world position and z-depth.
 * These coordinates belong to the shadowmap atlas that contains the maps for all cascades.
 */
inline float4 getShadowCoord( float4 wpos, fixed4 cascadeWeights )
{
    float3 sc0 = mul (unity_WorldToShadow[0], wpos).xyz;
    float3 sc1 = mul (unity_WorldToShadow[1], wpos).xyz;
    float3 sc2 = mul (unity_WorldToShadow[2], wpos).xyz;
    float3 sc3 = mul (unity_WorldToShadow[3], wpos).xyz;
    float4 shadowMapCoordinate = float4(sc0 * cascadeWeights[0] + sc1 * cascadeWeights[1] + sc2 * cascadeWeights[2] + sc3 * cascadeWeights[3], 1);
#if defined(UNITY_REVERSED_Z)
    float  noCascadeWeights = 1 - dot(cascadeWeights, float4(1, 1, 1, 1));
    shadowMapCoordinate.z += noCascadeWeights;
#endif
    return shadowMapCoordinate;
}

/**
 * Same as the getShadowCoord; but optimized for single cascade
 */
inline float4 getShadowCoord_SingleCascade( float4 wpos )
{
    return float4( mul (unity_WorldToShadow[0], wpos).xyz, 0);
}

/**
* Get camera space coord from depth and inv projection matrices
*/
inline float3 computeCameraSpacePosFromDepthAndInvProjMat(v2f i)
{
    float zdepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv.xy);

    #if defined(UNITY_REVERSED_Z)
        zdepth = 1 - zdepth;
    #endif

    // View position calculation for oblique clipped projection case.
    // this will not be as precise nor as fast as the other method
    // (which computes it from interpolated ray & depth) but will work
    // with funky projections.
    float4 clipPos = float4(i.uv.zw, zdepth, 1.0);
    clipPos.xyz = 2.0f * clipPos.xyz - 1.0f;
    float4 camPos = mul(unity_CameraInvProjection, clipPos);
    camPos.xyz /= camPos.w;
    camPos.z *= -1;
    return camPos.xyz;
}

/**
* Get the camera space position of the pixel from its depth and the vertex shader's
* interpolated ray and orthographic near and far positions, for perspective and
* orthographic cameras
*/
inline float3 computeCameraSpacePosFromDepthAndVSInfo(v2f i)
{
    float zdepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv.xy);

    // 0..1 linear depth, 0 at camera, 1 at far plane.
    float depth = lerp(Linear01Depth(zdepth), zdepth, unity_OrthoParams.w);
#if defined(UNITY_REVERSED_Z)
    zdepth = 1 - zdepth;
#endif

    // view position calculation for perspective & ortho cases
    float3 vposPersp = i.ray * depth;
    float3 vposOrtho = lerp(i.orthoPosNear, i.orthoPosFar, zdepth);
    // pick the perspective or ortho position as needed
    float3 camPos = lerp(vposPersp, vposOrtho, unity_OrthoParams.w);
    return camPos.xyz;
}

// The camera-space position of the pixel, defined in each pass for its subshader's way of finding it.
inline float3 computeCameraSpacePosFromDepth(v2f i);

// ReDefinition: the raytraced sun visibility. _ReDefinitionRtOn is set around the
// raytraced camera's rendering (RtCamera, its command buffers), _ReDefinitionRtSunMask
// around the sun's own mask (its light's command buffers): other directional lights --
// PlanetShine's, other stars' -- keep their shadow maps. The texture holds the frame
// before, found again from this frame's world by RtUv.
#include "ReDefinitionRtReproject.cginc"

// 1 around the sun's own shadow mask, 0 for every other directional light's.
float _ReDefinitionRtSunMask;

// The raytraced sun visibility at a point, where the raytraced camera draws the sun's mask and the raytraced
// frame saw the point; false where the shadow maps are to be read.
inline bool RtShadow(float3 worldPos, out fixed visibility)
{
    visibility = 1;
    if (_ReDefinitionRtOn < 0.5 || _ReDefinitionRtSunMask < 0.5)
        return false;
    // Where the raytraced frame did not see this point, the shadow maps.
    float2 uv;
    if (!RtUv(worldPos, uv))
        return false;
    visibility = tex2Dlod(_ReDefinitionRtSun, float4(uv, 0.0, 0.0)).r;
    return true;
}

/**
 *  Hard shadow: one tap of the shadow map, or the raytraced sun visibility
 */
fixed4 frag_hard (v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i); // required for sampling the correct slice of the shadow map render texture array
    float4 wpos;
    float3 vpos;

#if defined(STEREO_CUBEMAP_RENDER_ON)
    wpos.xyz = tex2D(_ODSWorldTexture, i.uv.xy).xyz;
    wpos.w = 1.0f;
    vpos = mul(unity_WorldToCamera, wpos).xyz;
#else
    vpos = computeCameraSpacePosFromDepth(i);
    wpos = mul (unity_CameraToWorld, float4(vpos,1));
#endif
    fixed raytraced;
    if (RtShadow(wpos.xyz, raytraced))
        return lerp(_LightShadowData.r, 1.0, raytraced);

    fixed4 cascadeWeights = GET_CASCADE_WEIGHTS (wpos, vpos.z);
    float4 shadowCoord = GET_SHADOW_COORDINATES(wpos, cascadeWeights);

    //1 tap hard shadow
    fixed shadow = UNITY_SAMPLE_SHADOW(_ShadowMapTexture, shadowCoord);
    shadow = lerp(_LightShadowData.r, 1.0, shadow);

    fixed4 res = shadow;
    return res;
}

/**
 *  Soft Shadow (SM 3.0)
 */
fixed4 frag_pcfSoft(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i); // required for sampling the correct slice of the shadow map render texture array
    float4 wpos;
    float3 vpos;

#if defined(STEREO_CUBEMAP_RENDER_ON)
    wpos.xyz = tex2D(_ODSWorldTexture, i.uv.xy).xyz;
    wpos.w = 1.0f;
    vpos = mul(unity_WorldToCamera, wpos).xyz;
#else
    vpos = computeCameraSpacePosFromDepth(i);

    // sample the cascade the pixel belongs to
    wpos = mul(unity_CameraToWorld, float4(vpos,1));
#endif
    fixed raytraced;
    if (RtShadow(wpos.xyz, raytraced))
        return lerp(_LightShadowData.r, 1.0, raytraced);

    fixed4 cascadeWeights = GET_CASCADE_WEIGHTS(wpos, vpos.z);
    float4 coord = GET_SHADOW_COORDINATES(wpos, cascadeWeights);

    float3 receiverPlaneDepthBias = 0.0;
#ifdef UNITY_USE_RECEIVER_PLANE_BIAS
    // Reveiver plane depth bias: need to calculate it based on shadow coordinate
    // as it would be in first cascade; otherwise derivatives
    // at cascade boundaries will be all wrong. So compute
    // it from cascade 0 UV, and scale based on which cascade we're in.
    float3 coordCascade0 = getShadowCoord_SingleCascade(wpos);
    float biasMultiply = dot(cascadeWeights,unity_ShadowCascadeScales);
    receiverPlaneDepthBias = UnityGetReceiverPlaneDepthBias(coordCascade0.xyz, biasMultiply);
#endif

#if defined(SHADER_API_MOBILE)
    half shadow = UnitySampleShadowmap_PCF5x5(coord, receiverPlaneDepthBias);
#else
    half shadow = UnitySampleShadowmap_PCF7x7(coord, receiverPlaneDepthBias);
#endif
    shadow = lerp(_LightShadowData.r, 1.0f, shadow);

    // Blend between shadow cascades if enabled
    //
    // Not working yet with split spheres, and no need when 1 cascade
#if UNITY_USE_CASCADE_BLENDING && !defined(SHADOWS_SPLIT_SPHERES) && !defined(SHADOWS_SINGLE_CASCADE)
    half4 z4 = (float4(vpos.z,vpos.z,vpos.z,vpos.z) - _LightSplitsNear) / (_LightSplitsFar - _LightSplitsNear);
    half alpha = dot(z4 * cascadeWeights, half4(1,1,1,1));

    UNITY_BRANCH
        if (alpha > 1 - UNITY_CASCADE_BLEND_DISTANCE)
        {
            // get alpha to 0..1 range over the blend distance
            alpha = (alpha - (1 - UNITY_CASCADE_BLEND_DISTANCE)) / UNITY_CASCADE_BLEND_DISTANCE;

            // sample next cascade
            cascadeWeights = fixed4(0, cascadeWeights.xyz);
            coord = GET_SHADOW_COORDINATES(wpos, cascadeWeights);

#ifdef UNITY_USE_RECEIVER_PLANE_BIAS
            biasMultiply = dot(cascadeWeights,unity_ShadowCascadeScales);
            receiverPlaneDepthBias = UnityGetReceiverPlaneDepthBias(coordCascade0.xyz, biasMultiply);
#endif

            half shadowNextCascade = UnitySampleShadowmap_PCF3x3(coord, receiverPlaneDepthBias);
            shadowNextCascade = lerp(_LightShadowData.r, 1.0f, shadowNextCascade);
            shadow = lerp(shadow, shadowNextCascade, alpha);
        }
#endif

    return shadow;
}
ENDCG


// ----------------------------------------------------------------------------------------
// Subshader for hard shadows:
// Just collect shadows into the buffer. Used on pre-SM3 GPUs and when hard shadows are picked.

SubShader {
    Tags{ "ShadowmapFilter" = "HardShadow" }
    // Hard shadows, the position from the view-space ray.
    Pass {
        ZWrite Off ZTest Always Cull Off

        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag_hard
        #pragma multi_compile_shadowcollector

        // The camera-space position from the view-space ray the vertex function interpolates.
        inline float3 computeCameraSpacePosFromDepth(v2f i)
        {
            return computeCameraSpacePosFromDepthAndVSInfo(i);
        }
        ENDCG
    }
}

// ----------------------------------------------------------------------------------------
// Subshader for hard shadows:
// Just collect shadows into the buffer. Used on pre-SM3 GPUs and when hard shadows are picked.
// This version does inv projection at the PS level, slower and less precise however more general.

SubShader {
    Tags{ "ShadowmapFilter" = "HardShadow_FORCE_INV_PROJECTION_IN_PS" }
    // Hard shadows, the position from the inverse projection.
    Pass{
        ZWrite Off ZTest Always Cull Off

        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag_hard
        #pragma multi_compile_shadowcollector

        // The camera-space position from the inverse projection, per pixel.
        inline float3 computeCameraSpacePosFromDepth(v2f i)
        {
            return computeCameraSpacePosFromDepthAndInvProjMat(i);
        }
        ENDCG
    }
}

// ----------------------------------------------------------------------------------------
// Subshader that does soft PCF filtering while collecting shadows.
// Requires SM3 GPU.

Subshader {
    Tags {"ShadowmapFilter" = "PCF_SOFT"}
    // Soft PCF shadows, the position from the view-space ray.
    Pass {
        ZWrite Off ZTest Always Cull Off

        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag_pcfSoft
        #pragma multi_compile_shadowcollector
        #pragma target 3.0

        // The camera-space position from the view-space ray the vertex function interpolates.
        inline float3 computeCameraSpacePosFromDepth(v2f i)
        {
            return computeCameraSpacePosFromDepthAndVSInfo(i);
        }
        ENDCG
    }
}

// ----------------------------------------------------------------------------------------
// Subshader that does soft PCF filtering while collecting shadows.
// Requires SM3 GPU.
// This version does inv projection at the PS level, slower and less precise however more general.

Subshader{
    Tags{ "ShadowmapFilter" = "PCF_SOFT_FORCE_INV_PROJECTION_IN_PS" }
    // Soft PCF shadows, the position from the inverse projection.
    Pass{
        ZWrite Off ZTest Always Cull Off

        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag_pcfSoft
        #pragma multi_compile_shadowcollector
        #pragma target 3.0

        // The camera-space position from the inverse projection, per pixel.
        inline float3 computeCameraSpacePosFromDepth(v2f i)
        {
            return computeCameraSpacePosFromDepthAndInvProjMat(i);
        }
        ENDCG
    }
}

Fallback Off
}
