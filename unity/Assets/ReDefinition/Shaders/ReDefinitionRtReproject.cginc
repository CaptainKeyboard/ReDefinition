// Where a point of this frame's world was in the raytraced frame before
// (src/Raytracing/RtCamera.cs, docs/development/raytracing.md, "How the frame goes").
//
// KSP moves its world between two frames in two ways: the floating origin shifts
// everything near the vessel, and Krakensbane moves the bodies and what stands on them
// under a fast vessel as well. Both candidates are tried, and the one whose view depth
// matches the depth the raytraced frame kept for the pixel wins.

#ifndef REDEFINITION_RT_REPROJECT
#define REDEFINITION_RT_REPROJECT

float _ReDefinitionRtOn;
float4x4 _ReDefinitionRtPrevViewProj;   // the previous frame's camera-relative world to clip
float4 _ReDefinitionRtOffsetNear;       // xyz: this world to the previous camera-relative one, near the vessel
float4 _ReDefinitionRtOffsetFar;        // the same for the bodies and what stands on them
sampler2D _ReDefinitionRtSun;           // w: the view depth the raytraced frame kept

inline bool RtCandidate(float3 worldPos, float3 offset, out float2 uv, out float error)
{
    float4 clip = mul(_ReDefinitionRtPrevViewProj, float4(worldPos + offset, 1.0));
    uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    error = 1e30;
    if (clip.w <= 0.0 || any(uv < 0.0) || any(uv > 1.0))
        return false;
    float kept = tex2Dlod(_ReDefinitionRtSun, float4(uv, 0.0, 0.0)).w;
    error = abs(kept - clip.w) / max(clip.w, 1e-3);
    return true;
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
