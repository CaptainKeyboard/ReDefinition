using System;

namespace ReDefinition.Atmospheres
{
    // The fast cloud path of the tier in force (TierRow.CloudFastPath): the defaults of the tuning keys that make it. A key set by hand still wins, so the
    // probe and the viewer compare both paths in one session.
    internal static class CloudFastPath
    {
        // The rotation over 4 x 4 blocks with the march's own distance history.
        // The clip stays the full path's 1 sigma (OwnCloudOptions.ClipSigmaDefault; a literal, because the weather player links this file without OwnCloudOptions, and CloudFastPathTests
        // keeps the two equal): 2 sigma drew a 4-pixel stipple and a 16-pixel grid over a fog-filled view (storm, ground; PRUEFPROTOKOLL 77).
        // The distance reject's tolerance is 3 octaves (a factor 8): the history stays unless the block's own sample and the stored one are clouds of very different distances. At 0.3 to
        // 1.5 octaves the block's single sample distance threw the history of all 16 pixels of a block away at the borders of the gaps, and the block's upsample stood there as 4 px
        // squares (the pinned storm view, PRUEFPROTOKOLL 95/96); at 3 and more they are gone, and a turn and a dolly show no ghost (97, 98). The full path has no reject (0).
        internal const float ReconRotate = 4f, DistanceReject = 3f, ClipSigma = 1f;
        internal const bool MarchTriangle = true, TrueDistance = true, Age = true;
        // A span's points by distance: the step grows by this per m from the camera.
        internal const float SpanGrowthPerM = 0.001f;
        // The shortest step is no longer the fast path's own (2026-11-23): the fast path's tiers set the step quality Medium (12 m, TierRow.CloudStepQuality,
        // LayerSwitches.MinStepMediumM), which a player may raise.
        // The cloud shadow marched at a quarter of the camera's size.
        internal const float ShadowResolution = 4f;

        // Whether the tier in force takes the fast path. The game's profile module installs the tier's answer (QualityProfileModule.Init); the weather player
        // installs its own (PlayerFastPath.cs); tests assign theirs. Until then the full path: no dependency here, so the player can link this file alone.
        internal static Func<bool> Source = () => false;

        // Read at every ask, so a change of the tier (the setting, the host's profile, the memory guard) acts on the next frame: the ask is two enum reads and the
        // engine's cached video memory, about ten times a frame.
        internal static bool On
        {
            get { return Read(); }
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
