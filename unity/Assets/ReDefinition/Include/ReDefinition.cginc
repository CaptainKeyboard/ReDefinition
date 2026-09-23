// ReDefinition's frame state for shaders (ReDefinition.Api.Frame), and helpers for the
// jitter and motion vectors.
//
// Copy this file into a shader project and include it:
//
//     #include "ReDefinition.cginc"
//
// ReDefinition sets the globals below every frame. Without ReDefinition they stay zero;
// every function here then returns what the game has without it: no upscaler, no frame
// generation, no reset, the size of the target from _ScreenParams, no jitter, no shift.
// Unity 2019.4 (KSP 1.12). Reference: docs/modders/shared-foundation.md in ReDefinition's
// repository. Licence of this file: MIT.

#ifndef REDEFINITION_INCLUDED
#define REDEFINITION_INCLUDED

#include "UnityCG.cginc"

// x upscaler active, y frame generation active, z history reset this frame, w interface
// version -- 0 without ReDefinition.
float4 _ReDefinition_Frame;
// width, height, 1/width, 1/height.
float4 _ReDefinition_RenderSize;
float4 _ReDefinition_DisplaySize;
// xy the jitter in pixels at render size, zw in normalized device coordinates.
float4 _ReDefinition_Jitter;
// xyz the floating origin's shift since the frame before, w 1 if it shifted.
float4 _ReDefinition_OriginShift;

inline bool ReDefinitionInstalled()
{
    return _ReDefinition_Frame.w > 0.5;
}

// Whether ReDefinition's upscaler reconstructs this frame: the 3D cameras render at
// ReDefinitionRenderSize, jittered.
inline bool ReDefinitionUpscalerActive()
{
    return _ReDefinition_Frame.x > 0.5;
}

inline bool ReDefinitionFrameGenerationActive()
{
    return _ReDefinition_Frame.y > 0.5;
}

// Whether temporal history is to be dropped this frame: a camera cut.
inline bool ReDefinitionHistoryReset()
{
    return _ReDefinition_Frame.z > 0.5;
}

// width, height, 1/width, 1/height of what the 3D cameras render into. Without
// ReDefinition, the current target's (_ScreenParams).
inline float4 ReDefinitionRenderSize()
{
    return ReDefinitionInstalled() && _ReDefinition_RenderSize.x > 0.0
        ? _ReDefinition_RenderSize
        : float4(_ScreenParams.xy, 1.0 / _ScreenParams.xy);
}

// The same for the image shown.
inline float4 ReDefinitionDisplaySize()
{
    return ReDefinitionInstalled() && _ReDefinition_DisplaySize.x > 0.0
        ? _ReDefinition_DisplaySize
        : float4(_ScreenParams.xy, 1.0 / _ScreenParams.xy);
}

// The jitter the 3D cameras render with, in pixels at render size and in normalized device
// coordinates, as ReDefinition adds it to the camera's projection. Zero without it.
inline float2 ReDefinitionJitterPixels()
{
    return _ReDefinition_Jitter.xy;
}

inline float2 ReDefinitionJitterNdc()
{
    return _ReDefinition_Jitter.zw;
}

// A clip-space position from the matrices a camera renders with (UNITY_MATRIX_P,
// UnityObjectToClipPos) without the jitter. ReDefinition adds the jitter to the projection
// before Unity makes it the GPU's; Unity flips y where it renders into a texture on
// Direct3D, which _ProjectionParams.x says (-1 if projection is flipped).
inline float4 ReDefinitionRemoveJitter(float4 clipPosition)
{
    clipPosition.x -= _ReDefinition_Jitter.z * clipPosition.w;
    clipPosition.y -= _ReDefinition_Jitter.w * _ProjectionParams.x * clipPosition.w;
    return clipPosition;
}

// The floating origin's shift since the frame before: KSP moved the active vessel, the
// camera and nearby objects by minus this. Zero when it did not shift.
inline float3 ReDefinitionOriginShift()
{
    return _ReDefinition_OriginShift.xyz;
}

inline bool ReDefinitionOriginShifted()
{
    return _ReDefinition_OriginShift.w > 0.5;
}

// A motion vector in the encoding Hidden/Internal-MotionVectors writes for a camera that
// renders into a render texture, as every camera ReDefinition redirects does, and every
// upscaler and frame generation read: the current minus the previous viewport position,
// 0 to 1, y up. Unity projects there with the render texture's flipped projection and flips
// y back where UNITY_UV_STARTS_AT_TOP, so the two cancel (measured in a Unity 2019.4.18f1
// player on Direct3D 11). The two positions in clip space without jitter and with y up --
// a world position through
// GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, false) * worldToCameraMatrix,
// this frame's and the previous frame's.
inline half4 ReDefinitionMotionVector(float4 clipCurrent, float4 clipPrevious)
{
    float2 current = (clipCurrent.xy / clipCurrent.w + 1.0) * 0.5;
    float2 previous = (clipPrevious.xy / clipPrevious.w + 1.0) * 0.5;
    return half4(current - previous, 0, 1);
}

#endif // REDEFINITION_INCLUDED
