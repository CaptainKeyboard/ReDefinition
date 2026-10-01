// Where a point of this frame's world was in the raytraced frame before
// (src/Raytracing/RtCamera.cs, docs/development/raytracing.md, "How the frame goes").
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
// camera turned towards it --, and nothing is carried: the shaders fall back to Unity's
// own lighting there.

#ifndef REDEFINITION_RT_REPROJECT
#define REDEFINITION_RT_REPROJECT

float _ReDefinitionRtOn;
float4x4 _ReDefinitionRtPrevViewProj;   // the previous frame's camera-relative world to clip
float4 _ReDefinitionRtOffsetNear;       // xyz: this world to the previous camera-relative one, near the vessel
float4 _ReDefinitionRtOffsetFar;        // the same for the bodies and what stands on them
sampler2D _ReDefinitionRtSun;           // w: the view depth the raytraced frame kept
float4 _ReDefinitionRtTexel;            // 1 / width, 1 / height, width, height of its textures

// The relative view depth error above which a texel is another surface.
static const float kMaxDepthError = 0.05;

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

#endif
