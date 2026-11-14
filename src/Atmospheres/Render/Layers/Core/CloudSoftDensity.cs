using System;

namespace ReDefinition.Atmospheres
{
    // The switches of the soft water content as the product's density (E40 soft density, package B1). CloudLayerDef's Effective* numbers, the type table's profile,
    // the cloud shadow and every CPU twin read these; nothing reads the settings or the tuning keys directly. The values reach here from OwnCloudOptions (the owner's
    // setting cloudSoftness, once per frame), from the noise binding (the detail volume's mean once the volumes are generated) and from the tuning table.
    //   layers.softDensity  1 (default): the cloud's density is the soft water content (CloudWater); 0: the old density (the share of the soft edge, hardened by type).
    //                       A tuning key for the A/B of the verification start only; it is removed with the old density in step (c).
    //   cloud.coverMeaning  0 (default): every layer keeps the cover it has today (the outline is where the old share began); 1: the outline is at noise rank 1 - map value
    //                       (the cover meaning of the cover-meaning design; needs the per-layer shares of E40-layer-shares-design.md to be decided first).
    internal static class CloudSoftDensity
    {
        internal const string TuneOn = "layers.softDensity", TuneCoverMeaning = "cloud.coverMeaning";
        // The width of the rise of the water content in noise rank: the owner's setting cloudSoftness, 0.3 to 1.0.
        internal const double DefaultSoftness = 0.5, MinSoftness = 0.3, MaxSoftness = 1.0;
        // The mean of the detail volume at the Medium size, used until the volumes are generated (tested against the generator).
        internal const double NominalDetailMean = 0.5;

        private static readonly object Gate = new object();
        private static long seenVersion = -1;
        private static bool on = true, coverMeaning;
        private static double softness = DefaultSoftness, detailMean = NominalDetailMean;
        private static long stamp;

        private static void Refresh()
        {
            long version = Tuning.Version;
            if (version == System.Threading.Volatile.Read(ref seenVersion)) return;
            lock (Gate)
            {
                if (version == seenVersion) return;
                bool o = Tuning.Flag(TuneOn, true), c = Tuning.Flag(TuneCoverMeaning, false);
                if (o != on || c != coverMeaning) stamp++;
                on = o;
                coverMeaning = c;
                System.Threading.Volatile.Write(ref seenVersion, version);
            }
        }

        // True: the density of a layer with shape noise is the soft water content.
        internal static bool Enabled { get { Refresh(); return on; } }

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

        // The settings' choices of the most grid points of a march: the owner's setting cloudSamples (24, 32 or 48) relative to the Medium step quality's 48.
        internal const int MediumSamples = 48;

        internal static int ScaleSamples(int maxSteps, int samplesSetting)
        {
            int s = samplesSetting <= 24 ? 24 : samplesSetting <= 32 ? 32 : 48;
            return Math.Max(4, (int)Math.Round(maxSteps * (double)s / MediumSamples));
        }
    }
}
