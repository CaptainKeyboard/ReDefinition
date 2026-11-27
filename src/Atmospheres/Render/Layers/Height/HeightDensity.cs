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
                                          double typeMap, LocalHeights heights, double raggedUnit, double coverageShare = -1.0)
        {
            if (t == null || !t.Any) return LayerDensity.ExtinctionWithShare(layer, table, altitudeM, coverageMap, typeMap, coverageShare);
            double lb, lt;
            LocalBaseTop(layer, table, t, typeMap, heights, raggedUnit, out lb, out lt);
            return ExtinctionBetween(layer, table, altitudeM, coverageMap, typeMap, coverageShare, lb, lt);
        }

        // The same with the type's local base and top given (LocalBaseTop).
        private static double ExtinctionBetween(CloudLayerDef layer, LayerTypeTable table, double altitudeM, double coverageMap, double typeMap, double coverageShare, double lb, double lt)
        {
            double share = (altitudeM - layer.InnerAltitudeM) / layer.ThicknessM;
            // coverageShare: the shape noise's share of the column (CloudShadowShapeNoise) in place of the plain threshold; below 0 it is not given.
            double s = coverageShare >= 0.0 ? coverageShare : LayerDensity.Share(layer, coverageMap, table, typeMap);
            if (s <= 0.0) return 0.0;
            if (share < lb || share > lt) return 0.0;
            int a, c;
            double f;
            table.Neighbours(typeMap, out a, out c, out f);
            double h = (share - lb) / (lt - lb);
            double profile = table.ProfileAt(a, h) * (1.0 - f) + table.ProfileAt(c, h) * f;
            return layer.EffectiveSigmaPerM * s * profile;
        }

        // The interpolated type's base and top at a place, as shares of the layer, with the height variation (LayerGeometry.LocalBaseTop): what the shader's TypeSlab leaves in
        // ty.b and ty.tp (without the undulations and the top's short waves, which the CPU does not mirror).
        internal static void LocalBaseTop(CloudLayerDef layer, LayerTypeTable table, HeightTuning t, double typeMap, LocalHeights heights, double raggedUnit, out double lb, out double lt)
        {
            double thickness = layer.ThicknessM;
            int a, c;
            double f, b, top;
            table.Neighbours(typeMap, out a, out c, out f);
            table.InterpolatedHeights(typeMap, out b, out top);
            float ba, ta, ea, bc, tc, ec;
            table.Info(a, out ba, out ta, out ea);
            table.Info(c, out bc, out tc, out ec);
            double erosion = ea + (ec - ea) * f;
            LayerGeometry.LocalBaseTop(b, top, erosion, thickness, heights, raggedUnit, t.BaseRagged ? t.RaggedM : 0.0, t.MinThicknessM, out lb, out lt,
                                       -layer.InnerAltitudeM / thickness);
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

        // The same with the shape noise of the layer pass: the coverage share at the place (x, y, z: planet-centred, in the map frame, metres) is the noise's, as the
        // layer shader's light path has it (the shape alone, no erosion). A layer without noise, or noise not ready, gives the plain extinction.
        internal static double Extinction(ICloudLayerSource source, LayerTypeTable table, int layerIndex, double altitudeM, double latDeg, double lonDeg,
                                          CloudShadowShapeNoise noise, double x, double y, double z)
        {
            if (noise == null || !noise.Active) return Extinction(source, table, layerIndex, altitudeM, latDeg, lonDeg);
            CloudLayerDef layer = source.LayerAt(layerIndex);
            float cov, typ;
            source.SampleMaps(layerIndex, latDeg, lonDeg, out cov, out typ);
            double layerShare = (altitudeM - layer.InnerAltitudeM) / layer.ThicknessM;
            var heightSource = source as ICloudHeightSource;
            HeightTuning t = heightSource == null ? null : heightSource.HeightOf(layerIndex);
            if (t == null || !t.Any) return LayerDensity.ExtinctionWithShare(layer, table, altitudeM, cov, typ, noise.Share(layerIndex, layer, x, y, z, cov, table, typ, layerShare));
            // The type's LOCAL base and top, read once: a type with a form takes its body's height from them, as the shader does (master plan 47.U stage 1), and the
            // extinction its height profile.
            double lb, lt;
            LocalBaseTop(layer, table, t, typ, heightSource.SampleHeights(layerIndex, latDeg, lonDeg), 0.0, out lb, out lt);
            return ExtinctionBetween(layer, table, altitudeM, cov, typ, noise.Share(layerIndex, layer, x, y, z, cov, table, typ, layerShare, lb, lt), lb, lt);
        }
    }
}
