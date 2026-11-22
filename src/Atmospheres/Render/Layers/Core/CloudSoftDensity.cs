using System;

namespace ReDefinition.Atmospheres
{
    // The numbers of the soft water content, the product's density (E40 soft density, package B1). CloudLayerDef's Effective* numbers, the type table's profile,
    // the cloud shadow and every CPU twin read these; nothing reads the settings or the tuning keys directly. The values reach here from OwnCloudOptions (the owner's
    // setting cloudSoftness, once per frame), from the noise binding (the detail volume's mean once the volumes are generated) and from the tuning table.
    // A layer with a noise amount of 0 keeps the plain soft edge of its cfg numbers (CloudLayerDef.Soft); the switch layers.softDensity and the old hardened density it
    // gave back went in step (c).
    // The outline is at noise rank 1 - map value, so a map value is the share of the sky the layer closes (the cover meaning, E40 F1; the layers' shares of the field are
    // CloudLookTuning.MeetTheField).
    internal static class CloudSoftDensity
    {
        // The width of the rise of the water content in noise rank: the owner's setting cloudSoftness, 0.3 to 1.0.
        internal const double DefaultSoftness = 0.5, MinSoftness = 0.3, MaxSoftness = 1.0;
        // The mean of the detail volume at the Medium size, used until the volumes are generated (tested against the generator).
        internal const double NominalDetailMean = 0.5;

        private static readonly object Gate = new object();
        private static double softness = DefaultSoftness, detailMean = NominalDetailMean;
        private static long stamp;

        // Rises each time a number that reaches the layers' uploaded numbers changed.
        internal static long Stamp { get { return System.Threading.Interlocked.Read(ref stamp); } }

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

        // The most grid points of a march: the setting cloudSamples (24 to 256, the shader's most) relative to the Medium step quality's 48.
        internal const int MediumSamples = 48;
        internal const int MostSamples = 256;

        internal static int ScaleSamples(int maxSteps, int samplesSetting)
        {
            int s = Math.Max(24, Math.Min(MostSamples, samplesSetting));
            return Math.Max(4, (int)Math.Round(maxSteps * (double)s / MediumSamples));
        }
    }
}
