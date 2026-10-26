using ReDefinition.Proxy;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The scene camera's move from the frame before to this one, against the
    // main body, as a matrix from this frame's clip space into the frame
    // before's: for the motion of far points fixed to the body (pass 2 of the
    // cloud motion shader, ScaledSpaceMotion).
    //
    // Unity works out a camera's motion vectors from world positions in single
    // precision. When the camera stands a hundred kilometres or more from the
    // origin -- KSP's flight camera zoomed out to its farthest, over the Mun --
    // and looks at ground as far away, the result is noise of several pixels
    // from one pixel to the next, and the upscaler reprojects every pixel of that
    // ground somewhere else each frame: it shimmers. Here only the camera's pose
    // against the body enters, the small difference between two frames, in
    // double precision; the distance from the origin only through the
    // single-precision transform positions, about a centimetre at 150 km.
    // Against the body rather than the world, since KSP moves
    // the world under a fast vessel (Krakensbane) and turns it with the body near
    // the ground; ground is fixed to the body either way. Free of Unity's native
    // code, tested.
    internal static class SceneMotion
    {
        // `projection` and `previousProjection`: GL.GetGPUProjectionMatrix(...,
        // false) without jitter. `position` and `rotation`: the camera's against the
        // body, this frame and the one before.
        internal static Matrix4x4 ClipToPreviousClip(Matrix4x4 projection, Matrix4x4 previousProjection,
                                                     Vector3d position, Quaternion rotation,
                                                     Vector3d previousPosition, Quaternion previousRotation)
        {
            // A point in this frame's camera space into the frame before's: Unity's
            // camera space looks down -z (worldToCameraMatrix is a flip of z times
            // the inverse of the camera's placement), so the move is taken
            // between two flips.
            Quaternion back = Conjugate(previousRotation);
            Vector3d moved = position - previousPosition;
            Vector3 translation = (Vector3)Rotate(back, moved);
            Matrix4x4 move = Placement(back * rotation, translation);
            Matrix4x4 flip = StreamlineCamera.FlipZ();
            Matrix4x4 cameraToPrevious = flip * move * flip;
            return previousProjection * cameraToPrevious * StreamlineCamera.Invert(projection);
        }

        // The inverse of a turn: Quaternion.Inverse is native code.
        internal static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        // A vector turned by a quaternion, in double.
        internal static Vector3d Rotate(Quaternion q, Vector3d v)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            // v + 2 w (q x v) + 2 q x (q x v)
            double cx = y * v.z - z * v.y, cy = z * v.x - x * v.z, cz = x * v.y - y * v.x;
            double ccx = y * cz - z * cy, ccy = z * cx - x * cz, ccz = x * cy - y * cx;
            return new Vector3d(v.x + 2.0 * (w * cx + ccx), v.y + 2.0 * (w * cy + ccy), v.z + 2.0 * (w * cz + ccz));
        }

        // Turn by `q`, then move by `t`.
        internal static Matrix4x4 Placement(Quaternion q, Vector3 t)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = 1f - 2f * (y * y + z * z);
            m.m01 = 2f * (x * y - w * z);
            m.m02 = 2f * (x * z + w * y);
            m.m10 = 2f * (x * y + w * z);
            m.m11 = 1f - 2f * (x * x + z * z);
            m.m12 = 2f * (y * z - w * x);
            m.m20 = 2f * (x * z - w * y);
            m.m21 = 2f * (y * z + w * x);
            m.m22 = 1f - 2f * (x * x + y * y);
            m.m03 = t.x;
            m.m13 = t.y;
            m.m23 = t.z;
            return m;
        }
    }
}
