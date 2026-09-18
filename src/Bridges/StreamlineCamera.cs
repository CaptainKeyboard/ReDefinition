using UnityEngine;

namespace ReDefinition.Bridges
{
    // The camera matrices DLSS frame generation takes from the frame packet
    // (FrameGeneration.h, FramePacket), in the form Streamline's common constants
    // want them: "All matrices are row major ... and must NOT contain temporal AA
    // jitter offset" (sl_consts.h, Constants).
    //
    // Unity's matrices are for column vectors, clip = M * v; Streamline's for row
    // vectors, clip = v * M', laid out row by row (sl_matrix_helpers.h multiplies
    // them that way). The same transform is therefore the transpose, and a
    // product keeps its meaning with its factors in reverse order.
    //
    // Unity's view space looks down -z (worldToCameraMatrix, OpenGL's
    // convention); Streamline's looks down +z, as its helpers build a view from the
    // camera's right, up and forward vectors. The flip goes into viewToClip, so
    // clip space itself is unchanged: the one Unity drew with.
    //
    // Only matrix arithmetic Unity implements in managed code is used here, so the
    // tests run without the game (StreamlineCameraTests).
    internal static class StreamlineCamera
    {
        private static readonly double[,] Augmented = new double[4, 8];

        // gpuProjection: GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix,
        // false) -- Direct3D's clip space with Unity's reversed depth, top of the
        // screen up, as the depth and motion vectors reach DLSS-G flipped to the
        // screen's orientation (fgFlipInputs). viewProjection and
        // previousViewProjection: that projection times worldToCameraMatrix, this
        // frame and the one before; the same twice where there is no frame before.
        //
        // clipToPrevClip = previousViewProjection * inverse(viewProjection) for
        // column vectors: from this frame's clip space through the world into the
        // last frame's -- the way Unity's own camera motion vectors reproject.
        public static void Fill(Matrix4x4 gpuProjection, Matrix4x4 viewProjection, Matrix4x4 previousViewProjection,
                                float[] viewToClip, float[] clipToView, float[] clipToPrevClip, float[] prevClipToClip)
        {
            Matrix4x4 lookingDownPlusZ = gpuProjection * FlipZ();
            WriteRowMajor(lookingDownPlusZ, viewToClip);
            WriteRowMajor(Invert(lookingDownPlusZ), clipToView);

            Matrix4x4 toPrevious = previousViewProjection * Invert(viewProjection);
            WriteRowMajor(toPrevious, clipToPrevClip);
            WriteRowMajor(Invert(toPrevious), prevClipToClip);
        }

        // The transpose, row by row: element [row, column] of the row-vector matrix
        // is element [column, row] of Unity's.
        internal static void WriteRowMajor(Matrix4x4 m, float[] target)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    target[4 * row + column] = m[column, row];
        }

        internal static Matrix4x4 FlipZ()
        {
            Matrix4x4 flip = Matrix4x4.identity;
            flip.m22 = -1f;
            return flip;
        }

        // Gauss-Jordan elimination with partial pivoting, in double:
        // Matrix4x4.inverse is native code, and a projection whose far plane lies
        // thousands of times further than its near one loses digits in float. The
        // zero matrix for a singular one.
        internal static Matrix4x4 Invert(Matrix4x4 matrix)
        {
            // [matrix | identity], reduced until the left half is the identity. One
            // array for every call, on the main thread, rather than one a frame.
            double[,] a = Augmented;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                    a[row, column] = matrix[row, column];
                for (int column = 0; column < 4; column++)
                    a[row, 4 + column] = row == column ? 1.0 : 0.0;
            }

            for (int column = 0; column < 4; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < 4; row++)
                    if (System.Math.Abs(a[row, column]) > System.Math.Abs(a[pivot, column]))
                        pivot = row;
                if (System.Math.Abs(a[pivot, column]) < 1e-300) return new Matrix4x4();

                if (pivot != column)
                    for (int k = 0; k < 8; k++)
                    {
                        double swap = a[column, k];
                        a[column, k] = a[pivot, k];
                        a[pivot, k] = swap;
                    }

                double scale = a[column, column];
                for (int k = 0; k < 8; k++) a[column, k] /= scale;

                for (int row = 0; row < 4; row++)
                {
                    if (row == column) continue;
                    double factor = a[row, column];
                    if (factor == 0.0) continue;
                    for (int k = 0; k < 8; k++) a[row, k] -= factor * a[column, k];
                }
            }

            Matrix4x4 result = new Matrix4x4();
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    result[row, column] = (float)a[row, 4 + column];
            return result;
        }
    }
}
