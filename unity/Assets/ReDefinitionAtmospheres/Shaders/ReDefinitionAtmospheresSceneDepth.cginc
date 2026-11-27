// The distance before the eye of a raw depth of the camera's depth texture, from the projection the camera renders with (SceneDepthPlan.cs is the CPU twin and
// holds the derivation; PRUEFPROTOKOLL 374, 375).
//
// Unity's LinearEyeDepth and Linear01Depth read _ZBufferParams, which Unity works out from the camera's near and far plane PROPERTIES. The card renders with the
// projection MATRIX, 32-bit floats, in which the far plane's term of a camera with a near plane millions of times nearer than its far plane is at a float's last
// bit: KSP's flight camera (0.21 m, 750 km) renders with the matrix of a far plane at 880.8 km. Read back with the properties, every far depth came out too near
// (0.5 km at 50 km, 3.6 km at 135 km, 27 km at 370 km), and the cloud march ended before the clouds that stand within that distance of the ground or the sea.
//
// unity_CameraProjection is the camera's own projection in OpenGL's convention, whatever is drawn at the moment (Unity's UnityShaderVariables.cginc: "the
// projection of original camera"), so this holds for every camera a pass runs under. Third row (0, 0, m22, m23), fourth row (0, 0, -1, 0): the card's depth of a
// point e before the eye is a / e - b on the reversed depth (1 minus that on the forward one), a = -m23 / 2, b = (-1 - m22) / 2. A projection of another kind
// (orthographic, or without a depth range a float can hold) keeps LinearEyeDepth.
#ifndef REDEFINITION_ATMOSPHERES_SCENEDEPTH
#define REDEFINITION_ATMOSPHERES_SCENEDEPTH

float SceneEyeDepth(float raw)
{
    float a = -0.5 * unity_CameraProjection._m23;
    float b = 0.5 * (-1.0 - unity_CameraProjection._m22);
    if (unity_CameraProjection._m32 != -1.0 || unity_CameraProjection._m33 != 0.0 || !(a > 0.0) || !(b > 0.0)) return LinearEyeDepth(raw);
#if defined(UNITY_REVERSED_Z)
    float r = raw;
#else
    float r = 1.0 - raw;
#endif
    return a / max(r + b, 1e-12);
}

#endif
