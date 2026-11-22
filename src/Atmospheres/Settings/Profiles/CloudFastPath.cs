using System;

namespace ReDefinition.Atmospheres
{
    // The fast cloud path of the tier in force (TierRow.CloudFastPath): the defaults of the tuning keys that make it. A key set by hand still wins, so the
    // probe and the viewer compare both paths in one session. Read once per tuning frame (Tuning.Version changes every frame), not per call.
    internal static class CloudFastPath
    {
        // The light volume for the sun's light of every lit sample: on, handed over at 0 km, one near tap on the far path.
        internal const float LightVolume = 1f, LvHandOverKm = 0f, LvFarTaps = 1f;
        // The rotation over 4 x 4 blocks with the march's own distance history.
        internal const float ReconRotate = 4f, DistanceReject = 0.3f, ClipSigma = 2f;
        internal const bool MarchTriangle = true, TrueDistance = true, Age = true;
        // A span's points by distance: the step grows by this per m from the camera.
        internal const float SpanGrowthPerM = 0.001f;
        // The cloud shadow marched at a quarter of the camera's size.
        internal const float ShadowResolution = 4f;

        // Whether the tier in force takes the fast path; tests assign their own.
        internal static Func<bool> Source = () => TierRows.For(QualityProfile.Effective()).CloudFastPath;

        private static long seen = long.MinValue;
        private static bool on;

        internal static bool On
        {
            get
            {
                long version = Tuning.Version;
                if (version != seen)
                {
                    seen = version;
                    on = Read();
                }
                return on;
            }
        }

        private static bool reported;

        // The tier's answer; the full path where the tier cannot be read (outside the game, or a host that throws), named once in the log.
        private static bool Read()
        {
            try
            {
                return Source();
            }
            catch (Exception e)
            {
                if (!reported)
                {
                    reported = true;
                    try
                    {
                        AtmospheresLog.Say("the fast cloud path is off: the quality tier could not be read (" + e.GetType().Name + ")");
                    }
                    catch (Exception)
                    {
                        // No log outside the game either: the full path stands.
                    }
                }
                return false;
            }
        }

        // Tests: forget the cached answer after assigning Source.
        internal static void Reset()
        {
            seen = long.MinValue;
        }

        internal static float Pick(float fast, float full)
        {
            return On ? fast : full;
        }

        internal static bool Pick(bool fast, bool full)
        {
            return On ? fast : full;
        }
    }
}
