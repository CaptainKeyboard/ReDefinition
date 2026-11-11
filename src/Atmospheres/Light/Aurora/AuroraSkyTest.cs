using System;

namespace ReDefinition.Atmospheres
{
    // Package D38. How the shell finds the pixels where it may draw (the sky) and which camera event it is recorded at. Pure: no engine
    // type, so the unit tests, the weather player and the series probe use one definition.
    //
    // Why a choice exists. RECON P6 (tests/data/recon/RECON.md) read the depth that Unity's global texture and
    // BuiltinRenderTextureType.Depth hold at BeforeForwardAlpha with Scatterer: ordered only up to 10 m, a smooth value of 0.314 in an
    // empty (sky) view, not the camera's projection, and nothing at AfterForwardOpaque. A test "the copy holds the far value" therefore
    // cannot be relied on: where the sky reads 0.314 it discards every pixel. The other sources named in the project's own notes are the
    // resolved depth (BuiltinRenderTextureType.ResolvedDepth, the source the upscaler rig reads in KSP's deferred path, where
    // BuiltinRenderTextureType.Depth stays empty) and the camera's hardware depth buffer, tested by the GPU itself (no copy at all).
    // The series K-W3b (start aurora) measures every mode in the real stack and the rows decide the default (E40 sets the key).
    internal enum AuroraSkyTest
    {
        // The hardware depth buffer of the camera target: the dome sits on the far plane and the GPU's depth test (less or equal) keeps
        // the pixels nothing was drawn on. No copy, no extra target. Default.
        ZBuffer = 0,
        // A copy of BuiltinRenderTextureType.Depth, drawn only where it holds the far value (the first build's test).
        CopyDepth = 1,
        // A copy of BuiltinRenderTextureType.ResolvedDepth, the far-value test.
        CopyResolved = 2,
        // A copy of the global _CameraDepthTexture, the far-value test.
        CopyGlobal = 3,
        // No depth test: the dome covers everything that is not hidden by a later draw. A control of the series, never a default.
        None = 4
    }

    // The camera event the buffer is recorded at.
    internal enum AuroraEvent
    {
        // Before the transparent range (queue above 2500). Scatterer's sky sphere (render queue 2998, multiplicative extinction then soft
        // additive in-scatter, ZWrite off) and EVE's 2D cloud layers (queue 2999 set by Scatterer) draw after it, so the sky extincts
        // the aurora (it lies above the atmosphere) and the clouds cover it. Default.
        BeforeForwardAlpha = 0,
        // After the transparent range: the aurora lies over the sky and the clouds. A control of the series.
        AfterForwardAlpha = 1
    }

    internal static class AuroraSkyTests
    {
        // The tuning number (aurora.skyTest) as a mode; anything out of range is the default.
        internal static AuroraSkyTest FromNumber(float value)
        {
            int n = (int)Math.Round(value);
            return n >= 0 && n <= (int)AuroraSkyTest.None ? (AuroraSkyTest)n : AuroraSkyTest.ZBuffer;
        }

        internal static AuroraEvent EventFromNumber(float value)
        {
            return (int)Math.Round(value) == 1 ? AuroraEvent.AfterForwardAlpha : AuroraEvent.BeforeForwardAlpha;
        }

        // Whether the mode needs a copy of a depth source (and the camera's depth texture mode where the source is the global texture).
        internal static bool UsesCopy(AuroraSkyTest mode)
        {
            return mode == AuroraSkyTest.CopyDepth || mode == AuroraSkyTest.CopyResolved || mode == AuroraSkyTest.CopyGlobal;
        }

        internal static bool NeedsDepthTexture(AuroraSkyTest mode)
        {
            return mode == AuroraSkyTest.CopyDepth || mode == AuroraSkyTest.CopyGlobal;
        }

        // The short name used in the log and in the series' variants.
        internal static string Name(AuroraSkyTest mode)
        {
            switch (mode)
            {
                case AuroraSkyTest.ZBuffer: return "zbuffer";
                case AuroraSkyTest.CopyDepth: return "copy_depth";
                case AuroraSkyTest.CopyResolved: return "copy_resolved";
                case AuroraSkyTest.CopyGlobal: return "copy_global";
                default: return "none";
            }
        }

        // The sky test on a copy, as the shader makes it. With a tolerance of zero or less a pixel is sky when the copy holds the far value
        // (0 where the buffer is reversed, else 1; the first build's test, 1e-9 reversed and 1e-6 otherwise). With a tolerance above zero
        // it is sky when the copy is within the tolerance of the reference, a sky value the series measured (aurora.skyDepthRef and
        // aurora.skyDepthTol).
        internal static bool IsSky(float depth, bool reversed, float reference, float tolerance)
        {
            if (tolerance <= 0f) return reversed ? depth <= 1e-9f : depth >= 1f - 1e-6f;
            return Math.Abs(depth - reference) <= tolerance;
        }

        // The reference and tolerance a series measurement gives: the sky values and the values of everything else, as the copy holds
        // them. The reference is the sky median; the tolerance is the larger of 3 sky spreads and 0.01, but at most half the gap to the
        // nearest other value. False where the two groups overlap (no reference separates them).
        internal static bool Calibrate(float skyMedian, float skySpread, float nearestOther, out float reference, out float tolerance)
        {
            reference = skyMedian;
            float gap = Math.Abs(nearestOther - skyMedian);
            float wanted = Math.Max(3f * Math.Max(skySpread, 0f), 0.01f);
            tolerance = Math.Min(wanted, 0.5f * gap);
            return gap > 2f * Math.Max(skySpread, 0f) + 0.004f && tolerance > Math.Max(skySpread, 0f);
        }
    }
}
