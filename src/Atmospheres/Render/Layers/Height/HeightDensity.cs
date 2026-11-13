using System;

namespace ReDefinition.Atmospheres
{
    // The density of a layer with the height variation, the shader's LayerDensity in double precision for the CPU consumers: the same as
    // LayerDensity.Extinction, but the base and top of the interpolated type are the local ones (LayerGeometry.LocalBaseTop). The noise and
    // erosion of the shader are not mirrored, as in LayerDensity.
    internal static class HeightDensity
    {
        // The extinction per m at a place of the layer. altitudeM above sea level, the two map values, the offsets there, and the ragged
        // boundary's unit value (-1 to 1; 0 where the CPU consumer does not resolve it).
        internal static double Extinction(CloudLayerDef layer, LayerTypeTable table, HeightTuning t, double altitudeM, double coverageMap,
                                          double typeMap, LocalHeights heights, double raggedUnit)
        {
            if (t == null || !t.Any) return LayerDensity.Extinction(layer, table, altitudeM, coverageMap, typeMap);
            double thickness = layer.ThicknessM;
            double share = (altitudeM - layer.InnerAltitudeM) / thickness;
            double s = LayerDensity.Shape(coverageMap, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
            if (s <= 0.0) return 0.0;
            int a, c;
            double f, b, top;
            table.Neighbours(typeMap, out a, out c, out f);
            table.InterpolatedHeights(typeMap, out b, out top);
            float ba, ta, ea, bc, tc, ec;
            table.Info(a, out ba, out ta, out ea);
            table.Info(c, out bc, out tc, out ec);
            double erosion = ea + (ec - ea) * f;
            double lb, lt;
            LayerGeometry.LocalBaseTop(b, top, erosion, thickness, heights, raggedUnit, t.BaseRagged ? t.RaggedM : 0.0, t.MinThicknessM, out lb, out lt);
            if (share < lb || share > lt) return 0.0;
            double h = (share - lb) / (lt - lb);
            double profile = table.ProfileAt(a, h) * (1.0 - f) + table.ProfileAt(c, h) * f;
            return layer.EffectiveSigmaPerM * s * profile;
        }

        // The extinction with everything read from a source: the maps, the height maps (if the source has them) at a place.
        internal static double Extinction(ICloudLayerSource source, LayerTypeTable table, int layerIndex, double altitudeM, double latDeg, double lonDeg)
        {
            CloudLayerDef layer = source.LayerAt(layerIndex);
            float cov, typ;
            source.SampleMaps(layerIndex, latDeg, lonDeg, out cov, out typ);
            var heightSource = source as ICloudHeightSource;
            HeightTuning t = heightSource == null ? null : heightSource.HeightOf(layerIndex);
            if (t == null) return LayerDensity.Extinction(layer, table, altitudeM, cov, typ);
            return Extinction(layer, table, t, altitudeM, cov, typ, heightSource.SampleHeights(layerIndex, latDeg, lonDeg), 0.0);
        }
    }
}
