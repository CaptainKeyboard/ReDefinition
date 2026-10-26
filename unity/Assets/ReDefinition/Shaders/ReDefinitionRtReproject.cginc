// Where a point of this frame's world was in the raytraced frame before
// (src/Raytracing/RtCamera.cs, docs/development/flow-raytracing.md, "How the frame goes").
//
// KSP moves its world between two frames in two ways: the floating origin shifts
// everything near the vessel, and Krakensbane moves the bodies and what stands on them
// under a fast vessel as well. Both candidates are tried, and the one whose view depth
// matches the depth the raytraced frame kept for the pixel wins.
//
// The raytraced frame's texel is the one of the four around the point whose kept depth
// matches best, read at its centre: a bilinear read would mix a silhouette's signals
// with the ground's behind it. The point counts as seen where that texel's depth lies
// within kMaxDepthError of it, or its depth lies between the four kept ones, as on ground
// seen at a grazing angle, whose depth changes much from texel to texel. Otherwise the
// point was hidden in the raytraced frame -- it came out from behind something, or the
// camera turned towards it --, and nothing is carried: the sun's shadow falls back to
// Unity's shadow maps there.
//
// The raytraced diffuse light and reflections take such a point's light from the nearest
// texel around it, up to 512 texels away, whose kept depth matches (RtUvFilled), as NRD
// fills its history after a disocclusion. Unity's flat ambient light there for a frame,
// and the raytraced light the frame after, would be a jump DLSS Ray Reconstruction takes
// for noise and keeps in its history: a band trailing a silhouette as the camera turns.

#ifndef REDEFINITION_RT_REPROJECT
#define REDEFINITION_RT_REPROJECT

// 1 around the raytraced camera's rendering (RtCamera's flag buffers), 0 for every other camera.
float _ReDefinitionRtOn;
float4x4 _ReDefinitionRtPrevViewProj;   // the previous frame's camera-relative world to clip
float4 _ReDefinitionRtOffsetNear;       // xyz: this world to the previous camera-relative one, near the vessel
float4 _ReDefinitionRtOffsetFar;        // the same for the bodies and what stands on them
sampler2D _ReDefinitionRtSun;           // w: the view depth the raytraced frame kept
float4 _ReDefinitionRtTexel;            // 1 / width, 1 / height, width, height of its textures

// The relative view depth error above which a texel is another surface.
static const float kMaxDepthError = 0.05;

// The point carried over by one of the two offsets: the best-matching of the four texels around its place in
// the frame before, its relative depth error, and whether it counts as seen there.
inline bool RtCandidate(float3 worldPos, float3 offset, out float2 uv, out float error)
{
    float4 clip = mul(_ReDefinitionRtPrevViewProj, float4(worldPos + offset, 1.0));
    uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    error = 1e30;
    if (clip.w <= 0.0 || any(uv < 0.0) || any(uv > 1.0))
        return false;
    float2 texel = uv * _ReDefinitionRtTexel.zw - 0.5;
    float2 base = floor(texel);
    float2 best = uv;
    float nearest = 1e30;
    float farthest = 0.0;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 at = clamp(base + float2(i & 1, i >> 1), 0.0, _ReDefinitionRtTexel.zw - 1.0);
        float2 centre = (at + 0.5) * _ReDefinitionRtTexel.xy;
        float kept = tex2Dlod(_ReDefinitionRtSun, float4(centre, 0.0, 0.0)).w;
        float e = abs(kept - clip.w) / max(clip.w, 1e-3);
        // The sky keeps 0: never a surface, nor part of the span.
        nearest = kept > 0.0 ? min(nearest, kept) : 0.0;
        farthest = max(farthest, kept);
        if (e < error)
        {
            error = e;
            best = centre;
        }
    }
    uv = best;
    bool spanned = nearest > 0.0 && clip.w >= nearest * (1.0 - kMaxDepthError) && clip.w <= farthest * (1.0 + kMaxDepthError);
    if (spanned)
        error = min(error, kMaxDepthError);
    return error <= kMaxDepthError;
}

// The texel within 512 of uv, in eight directions, whose kept depth matches viewDepth
// best, within twice kMaxDepthError.
inline bool RtFill(float2 uv, float viewDepth, out float2 found)
{
    found = uv;
    float bestError = 2.0 * kMaxDepthError;
    bool any = false;
    float2 centre = (floor(uv * _ReDefinitionRtTexel.zw) + 0.5) * _ReDefinitionRtTexel.xy;
    [loop]
    // As far as a vessel's width at 2560 pixels: a fast turn uncovers a strip that wide
    // behind it, and 32 texels found matching ground for only half of it (a recording,
    // 2026-10-01); 512 for nine in ten.
    for (int ring = 1; ring <= 9 && !any; ring++)
    {
        float reach = exp2(ring);
        [unroll]
        for (int d = 0; d < 8; d++)
        {
            float angle = d * (UNITY_PI / 4.0);
            float2 at = saturate(centre + round(float2(cos(angle), sin(angle)) * reach) * _ReDefinitionRtTexel.xy);
            float kept = tex2Dlod(_ReDefinitionRtSun, float4(at, 0.0, 0.0)).w;
            float e = abs(kept - viewDepth) / max(viewDepth, 1e-3);
            if (kept > 0.0 && e < bestError)
            {
                bestError = e;
                found = at;
                any = true;
            }
        }
    }
    return any;
}

// The previous frame's texture coordinate of a point, where the raytraced frame has it.
inline bool RtUv(float3 worldPos, out float2 uv)
{
    uv = 0;
    if (_ReDefinitionRtOn < 0.5)
        return false;
    float2 uvNear, uvFar;
    float errorNear, errorFar;
    bool near = RtCandidate(worldPos, _ReDefinitionRtOffsetNear.xyz, uvNear, errorNear);
    bool far = RtCandidate(worldPos, _ReDefinitionRtOffsetFar.xyz, uvFar, errorFar);
    if (!near && !far)
        return false;
    uv = errorNear <= errorFar ? uvNear : uvFar;
    return true;
}

// RtUv, and where the raytraced frame did not see the point, the texel RtFill finds
// beside it: for the diffuse light and the reflections, not the sun's shadow.
inline bool RtUvFilled(float3 worldPos, out float2 uv)
{
    if (RtUv(worldPos, uv))
        return true;
    if (_ReDefinitionRtOn < 0.5)
        return false;
    // Near the vessel the floating origin's offset; the far one only matters for a
    // fast vessel, where the point is near it anyway.
    float4 clip = mul(_ReDefinitionRtPrevViewProj, float4(worldPos + _ReDefinitionRtOffsetNear.xyz, 1.0));
    if (clip.w <= 0.0)
        return false;
    float2 at = saturate(float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5));
    return RtFill(at, clip.w, uv);
}

#endif
