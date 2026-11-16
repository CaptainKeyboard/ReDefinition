using System;

namespace ReDefinition.Atmospheres
{
    // The numbers of the soft water content, the product's density (E40 soft density, package B1). CloudLayerDef's Effective* numbers, the type table's profile,
    // the cloud shadow and every CPU twin read these; nothing reads the settings or the tuning keys directly. The values reach here from OwnCloudOptions (the owner's
    // setting cloudSoftness, once per frame), from the noise binding (the detail volume's mean once the volumes are generated) and from the tuning table.
    // A layer with a noise amount of 0 keeps the plain soft edge of its cfg numbers (CloudLayerDef.Soft); the switch layers.softDensity and the old hardened density it
    // gave back went in step (c).
    //   cloud.coverMeaning  1 (default, E40 F1): the outline is at noise rank 1 - map value, so a map value is the share of the sky the layer closes (the cover meaning of the
    //                       cover-meaning design; the layers' shares of the field are CloudLookTuning.MeetTheField); 0: every layer keeps the cover it had before (the outline is where
    //                       the old share began: a deck is a step in the map value, a tower layer shows nothing below 0.65).
    internal static class CloudSoftDensity
    {
        internal const string TuneCoverMeaning = "cloud.coverMeaning";
        // The default of cloud.coverMeaning.
        internal const bool DefaultCoverMeaning = true;
        // The width of the rise of the water content in noise rank: the owner's setting cloudSoftness, 0.3 to 1.0.
        internal const double DefaultSoftness = 0.5, MinSoftness = 0.3, MaxSoftness = 1.0;
        // The mean of the detail volume at the Medium size, used until the volumes are generated (tested against the generator).
        internal const double NominalDetailMean = 0.5;

        private static readonly object Gate = new object();
        private static long seenVersion = -1;
        private static bool coverMeaning = DefaultCoverMeaning;
        private static double softness = DefaultSoftness, detailMean = NominalDetailMean;
        private static long stamp;

        private static void Refresh()
        {
            long version = Tuning.Version;
            if (version == System.Threading.Volatile.Read(ref seenVersion)) return;
            lock (Gate)
            {
                if (version == seenVersion) return;
                bool c = Tuning.Flag(TuneCoverMeaning, DefaultCoverMeaning);
                if (c != coverMeaning) stamp++;
                coverMeaning = c;
                System.Threading.Volatile.Write(ref seenVersion, version);
            }
        }

        internal static bool CoverMeaning { get { Refresh(); return coverMeaning; } }

        // Rises each time a number that reaches the layers' uploaded numbers changed.
        internal static long Stamp { get { Refresh(); return System.Threading.Interlocked.Read(ref stamp); } }

        internal static double Softness
        {
            get { return softness; }
            set
            {
                double v = double.IsNaN(value) ? DefaultSoftness : Math.Max(MinSoftness, Math.Min(MaxSoftness, value));
                lock (Gate)
                {
                    if (v == softness) return;
                    softness = v;
                    stamp++;
                }
            }
        }

        // The mean of the noise volumes' detail (0 to 1): what the erosion at the outline is read at. Set by the noise binding when the volumes are ready.
        internal static double DetailMean
        {
            get { return detailMean; }
            set
            {
                double v = double.IsNaN(value) ? NominalDetailMean : Math.Max(0.0, Math.Min(1.0, value));
                lock (Gate)
                {
                    if (Math.Abs(v - detailMean) < 1e-6) return;
                    detailMean = v;
                    stamp++;
                }
            }
        }

        // The most grid points of a march: the setting cloudSamples (24 to 128) relative to the Medium step quality's 48.
        internal const int MediumSamples = 48;

        internal static int ScaleSamples(int maxSteps, int samplesSetting)
        {
            int s = Math.Max(24, Math.Min(128, samplesSetting));
            return Math.Max(4, (int)Math.Round(maxSteps * (double)s / MediumSamples));
        }
    }
}
