using System;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 F1: what a map value of a layer draws, looking straight up, from the product's own numbers: the share the layer pass forms (CloudLayerDef's Effective* numbers and the clear-sky
    // rule, the water content's exponent), the erosion at the detail's mean, the type table's profile (rise and density scale baked in): the optical depth of a vertical column at each
    // noise rank. Noise ranks are uniform (the shape volume is ranked), so the shares over a grid of ranks are the shares of the sky: VISIBLE where the column's opacity is at least
    // VisibleAlpha (a viewer sees a cloud), CLOSED where it is at least ClosedAlpha (the sky is shut). The union of layers is taken as independent (the layers' noises differ).
    // The model is a vertical column: a slanted ray sees more, and the erosion's detail is its mean; the weather player's pictures are the judge of a look, this is the number beside it.
    internal static class SkyClearModel
    {
        internal const double VisibleAlpha = 0.10, ClosedAlpha = 0.90;

        // The vertical optical depth of a column of full water content for a type value: sigma x the integral of the baked profile over the type's height (metres).
        internal static double ColumnTauFull(CloudLayerDef layer, LayerTypeTable table, double typeValue, int steps = 100)
        {
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            if (!(et > eb)) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < steps; i++) sum += table.Profile(typeValue, eb + (et - eb) * (i + 0.5) / steps);
            return layer.EffectiveSigmaPerM * layer.ThicknessM * (et - eb) * sum / steps;
        }

        // The erosion at a type value (the two neighbouring types' blended).
        internal static double ErosionOf(LayerTypeTable table, double typeValue)
        {
            int ia, ib;
            double f;
            table.Neighbours(typeValue, out ia, out ib, out f);
            float b0, t0, e0, b1, t1, e1;
            table.Info(ia, out b0, out t0, out e0);
            table.Info(ib, out b1, out t1, out e1);
            return e0 + (e1 - e0) * f;
        }

        // The optical depth of the column at noise rank n for a map value: the product's share (LayerDensity.Shape of the map after the clear-sky rule, moved by the noise),
        // eroded at the detail's mean, to the exponent of the type at the type value (a type's edgePower replaces the layer's waterPower, as in LayerDensity.Share).
        private static double Tau(CloudLayerDef layer, LayerTypeTable table, double typeValue, double mapValue, double n, double erosion, double tauFull)
        {
            double s = LayerDensity.Shape(layer.ClearSkyMap(mapValue) + (n - 0.5) * layer.EffectiveNoiseAmount, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
            if (s <= 0.0) return 0.0;
            double eroded = CloudWater.Erode(s, CloudSoftDensity.DetailMean, erosion);
            return tauFull * CloudWater.Pow(eroded, layer.EffectiveWaterPowerAt(table, typeValue));
        }

        // The shares of the sky above a map value that are visible and closed (uniform noise ranks, `samples` of them).
        internal static void Shares(CloudLayerDef layer, LayerTypeTable table, double mapValue, double typeValue, out double visible, out double closed, int samples = 200)
        {
            double tauFull = ColumnTauFull(layer, table, typeValue);
            double erosion = ErosionOf(table, typeValue);
            double tauVisible = -Math.Log(1.0 - VisibleAlpha), tauClosed = -Math.Log(1.0 - ClosedAlpha);
            int v = 0, c = 0;
            for (int i = 0; i < samples; i++)
            {
                double tau = Tau(layer, table, typeValue, mapValue, (i + 0.5) / samples, erosion, tauFull);
                if (tau >= tauVisible) v++;
                if (tau >= tauClosed) c++;
            }
            visible = v / (double)samples;
            closed = c / (double)samples;
        }
    }
}
