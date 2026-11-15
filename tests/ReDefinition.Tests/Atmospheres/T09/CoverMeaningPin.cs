using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 F1 made the cover meaning (a map value is the share of the sky a layer closes) the default. The tests that pin the arithmetic of the OLD mapping (the outline where the old
    // share began: the layer's coverage constant, its edge softness and noise amount as configured) pin it with Legacy(), the key stays selectable (cloud.coverMeaning 0).
    internal static class CoverMeaningPin
    {
        private static int frame = 9100000;

        internal static void Legacy()
        {
            Tuning.SetOverride(CloudSoftDensity.TuneCoverMeaning, 0f);
            Tuning.Instance.BeginFrame(++frame);
        }

        internal static void Release()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
        }
    }
}
