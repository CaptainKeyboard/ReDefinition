namespace ReDefinition.Atmospheres
{
    // The distance of a pixel of the camera's depth texture, from the projection the camera really renders with (PRUEFPROTOKOLL 374, 375).
    //
    // The depth pass turned the raw depth into metres with Unity's LinearEyeDepth, which reads _ZBufferParams: numbers Unity works out from the camera's near and far
    // plane PROPERTIES (0.21 m and 750,000 m on KSP's flight camera). The graphics card does not use those: it multiplies by the projection MATRIX, whose elements are
    // 32-bit floats, and with a near plane 3.6 million times nearer than the far plane the far plane's term of that matrix is at the last bits of a float: the element
    // m22 = -(f + n) / (f - n) is -1.00000056 on paper and comes out of the float arithmetic as -1.00000048 (floats near 1 are 0.00000012 apart), which is the
    // matrix of a far plane at 880,804 m (the upscaler's work had found that number on Camera 00: IntegrationReport.cs). Read back with the 750,000 of the properties,
    // every far depth came out too near: 1 / read = 1 / true + (1 / 750000 - 1 / 880804), that is 0.5 km at 50 km, 2 km at 100 km, 3.6 km at 135 km, 8 km at 200 km,
    // 27 km at 370 km. The march ended its rays there, before every cloud that stood within that distance in front of the ground or the sea: from 130 km up the decks
    // were gone while their shadows lay on the sea (measured: the depth's middle values in the picture's quarters were 3 to 8 % below the sea's sphere, a constant
    // 2.3 to 2.5e-7 per m in the inverse, and the picture without the scene depth had the decks), and near the ground the far, low cloud before the horizon.
    //
    // The remedy is the matrix itself, read in the shader (ReDefinitionAtmospheresSceneDepth.cginc, SceneEyeDepth: unity_CameraProjection is the projection of the
    // camera a pass runs under), used by the cloud pass's depth copy, the cloud motion vectors' visibility and the cloud shadow's world position. This class is
    // the CPU twin of that function. For a projection in OpenGL's convention with the rows (0, 0, m22, m23) and (0, 0, -1, 0), Unity hands the card on Direct3D with
    // the reversed depth the row (row 3 - row 2) / 2, so a point e metres before the eye gets the raw depth a / e - b with a = -m23 / 2 and b = (-1 - m22) / 2, and
    // back: e = a / (raw + b). With the forward depth of the other platforms (raw' = 1 - raw of the reversed one) the same a and b hold.
    internal static class SceneDepthPlan
    {
        // The two terms from the projection's elements (floats, as the card gets them; the subtraction of two numbers this near to 1 is exact in floats).
        internal static void Terms(float m22, float m23, out float a, out float b)
        {
            a = -m23 * 0.5f;
            b = (-1f - m22) * 0.5f;
        }

        // Whether the terms hold: a perspective whose fourth row is (0, 0, -1, 0) and whose depth range a float can hold (b is 0 where the far plane is so far that
        // m22 rounds to -1: nothing is known of the far plane then). Otherwise the shader keeps LinearEyeDepth.
        internal static bool Usable(float m32, float m33, float a, float b)
        {
            return m32 == -1f && m33 == 0f && a > 0f && b > 0f;
        }

        // The distance before the eye of a raw depth (reversedZ: Direct3D 11 and the like, where the far plane is 0).
        internal static double EyeDepth(double raw, double a, double b, bool reversedZ)
        {
            double r = reversedZ ? raw : 1.0 - raw;
            double d = r + b;
            return d > 0.0 ? a / d : double.PositiveInfinity;
        }

        // The raw depth the card writes for a point e metres before the eye.
        internal static double RawDepth(double e, double a, double b, bool reversedZ)
        {
            double r = a / e - b;
            return reversedZ ? r : 1.0 - r;
        }

        // The far plane the matrix stands for (the distance whose raw depth is the far plane's): a / b.
        internal static double ImpliedFar(double a, double b)
        {
            return b > 0.0 ? a / b : double.PositiveInfinity;
        }

        // What LinearEyeDepth made of a raw depth of the reversed kind: 1 / ((1 / near - 1 / far) raw + 1 / far), with the camera's properties.
        internal static double PropertiesEyeDepth(double raw, double near, double far)
        {
            return 1.0 / ((1.0 / near - 1.0 / far) * raw + 1.0 / far);
        }
    }
}
