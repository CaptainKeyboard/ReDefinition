using System;

namespace ReDefinition.Atmospheres
{
    // The shape noise in the cloud shadow (E40, FL-FIX). The layer pass moves each layer's coverage threshold by the shape volume (CloudShapeModel.Share, the shader's
    // NoiseShare); a shadow that reads the map alone keeps the map's closed shape under a cell field the picture has broken into cells (the cumulus layer of the shipped
    // cfg, noise amount 1 at 500 m). With this switch on the shadow reads the same share: the shape noise alone, the way the pass's own light march reads it (no erosion:
    // gNoiseLod), from the same volume, scale, offsets and drift (LayerNoise), so that the shader's twin (ReDefinitionAtmospheresCloudShadow.shader includes
    // ReDefinitionAtmospheresNoise.cginc and calls NoiseShareNear) and this class are one definition. The types' height-shaped threshold (the LIFT of the cfg), the types'
    // heap volumes (with the pass's blend), their edgePower exponents and their shapeSmoothing (the volume's mip) are read as the pass reads them. Not mirrored: the flow map of a body
    // that has one, and the height maps' ragged boundary (HeightDensity resolves the type's local base and top as before).
    //
    // The switch is the tuning key shadow.shapeNoise (OwnCloudOptions.ShadowShapeNoise): 1 reads the noise, 0 the map alone (the shadow before E40 FL-FIX).
    internal sealed class CloudShadowShapeNoise
    {
        internal const string TuneKey = "shadow.shapeNoise";
        // The compiled default of the switch: on, because the shader and this class agree with the pass's cell field within the old tolerance (tests CloudShadowShapeNoiseTests and
        // the player series e40-ff).
        internal const bool Default = true;

        private readonly NoiseLayerParams[] layerParams = new NoiseLayerParams[CloudLayerDef.MaxLayers];
        // The rotation from the world frame to the map frame (row-major, 3 x 3); the noise lattice is fixed in the world frame, the CPU readers work in the map frame.
        private readonly double[] worldToMap = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        private NoiseVolumeSet volumes;
        private int layers;

        // Whether the readers apply the noise: the switch is on and the volume is ready.
        internal bool Active { get { return volumes != null; } }

        // Takes the numbers of this frame from the layer pass's noise: the parameters of each layer (scale, offset, drift, seed), the volume, and the frame's rotation. on: the switch.
        internal void Update(LayerNoise noise, ICloudLayerSource source, float[] worldToMapRows, bool on)
        {
            if (!on || noise == null || source == null || !noise.Switches.Enabled || !noise.Ready || noise.ShapeVolumes == null || worldToMapRows == null || worldToMapRows.Length < 9)
            {
                volumes = null;
                return;
            }
            volumes = noise.ShapeVolumes;
            layers = Math.Min(source.LayerCount, CloudLayerDef.MaxLayers);
            for (int i = 0; i < 9; i++) worldToMap[i] = worldToMapRows[i];
            for (int l = 0; l < layers; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                layerParams[l] = noise.LayerParams(l, layer.EffectiveNoiseScaleM, source.BodyRadiusM + 0.5 * (layer.InnerAltitudeM + layer.OuterAltitudeM));
            }
        }

        // A given set for the tests and the player's scene: the parameters of a layer and the rotation (rows of the world-to-map matrix).
        internal void Set(NoiseVolumeSet set, NoiseLayerParams[] parameters, double[] worldToMapRows)
        {
            volumes = set;
            layers = parameters == null ? 0 : Math.Min(parameters.Length, CloudLayerDef.MaxLayers);
            for (int l = 0; l < layers; l++) layerParams[l] = parameters[l];
            if (worldToMapRows != null) Array.Copy(worldToMapRows, worldToMap, 9);
        }

        // The coverage share of the layer at a place (x, y, z: planet-centred, map frame, metres) for the map value cov: the map moved by the shape noise through the soft edge.
        // Below 0: not given (the switch is off, the layer has no noise, or the volume is not ready), the caller uses the plain threshold.
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov)
        {
            return Share(layerIndex, layer, x, y, z, cov, null, 0.0);
        }

        // The same for a type value of the layer's type table (CLOUDR1: the type's heap preset and density exponent; a null table: the layer's, no heap).
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov, LayerTypeTable table, double typeValue)
        {
            return Share(layerIndex, layer, x, y, z, cov, table, typeValue, double.NaN);
        }

        // The same at a height of the layer (layerShare, 0 to 1 over the layer's thickness): the type's LIFT lowers the map there as the pass does (cov -= lift x the layer's headroom, after
        // the clear-sky rule), with or without noise in the layer; NaN: no height given, no lift (a null table has none either).
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov, LayerTypeTable table, double typeValue, double layerShare)
        {
            if (volumes == null || layerIndex < 0 || layerIndex >= layers) return -1.0;
            double amount = layer.EffectiveNoiseAmount;
            double lift = table == null || double.IsNaN(layerShare) ? 0.0 : table.LiftOf(typeValue, layerShare);
            // The map value after the clear-sky rule, and with the cells (CELLS: the layer pass's single clouds from the map's share, CloudCells.CoverAt) where the layer has a pitch.
            double covMap = layer.ClearSkyMap(cov);
            bool cells = layer.CellPitchM > 0f && CloudLookTuning.Cells;
            CloudCells.CellRead read = default(CloudCells.CellRead);
            if (cells) covMap = CloudCells.CoverAt(covMap, x, y, z, layer, layerParams[layerIndex].ReferenceRadiusM, layerParams[layerIndex].Seed, out read);
            // CELLS: the single cloud's body where the cells alone draw and a height is given (the shader's LayerExtinction: it replaces the type's lift).
            if (cells && read.Body && table != null && !double.IsNaN(layerShare)) return BodyShare(layerIndex, layer, x, y, z, table, typeValue, layerShare, amount, ref read);
            if (!(amount > 0.0) || !(layer.EffectiveNoiseScaleM > 0f))
            {
                if (!(lift > 0.0) && !cells) return -1.0;
                double plain = LayerDensity.Shape(covMap - lift, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
                return CloudWater.Pow(plain, layer.EffectiveWaterPowerAt(table, typeValue));
            }
            // The lattice is fixed in the world frame: the map frame's point turned back (the transpose of the rotation).
            double wx = worldToMap[0] * x + worldToMap[3] * y + worldToMap[6] * z;
            double wy = worldToMap[1] * x + worldToMap[4] * y + worldToMap[7] * z;
            double wz = worldToMap[2] * x + worldToMap[5] * y + worldToMap[8] * z;
            NoiseLayerParams prm = TypeParams(layerIndex, table, typeValue);
            double share = CloudShapeModel.Share(volumes, ref prm, wx, wy, wz, covMap - lift, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, amount, 0.0, false);
            return CloudWater.Pow(share, table == null ? layer.EffectiveWaterPower : layer.EffectiveWaterPowerAt(table, typeValue));
        }

        // The layer's noise parameters for a type value: its heap preset and the type's shapeSmoothing (the mip of the shape volume and the heap read), as the shader's gHeapPreset and gNoiseSm
        // and CloudCoverOracle take them.
        private NoiseLayerParams TypeParams(int layerIndex, LayerTypeTable table, double typeValue)
        {
            NoiseLayerParams prm = layerParams[layerIndex];
            prm.HeapPreset = table == null ? 0 : table.HeapAtTypeValue(typeValue);
            if (table != null)
            {
                prm.SmoothingScales = table.SmoothingOf(typeValue);
                prm.ShapeLod = CloudShapeModel.SmoothingLod(prm.SmoothingScales, volumes.ShapeN);
            }
            return prm;
        }

        // The share of a single cloud's body at a height of the layer, as the shader's LayerExtinction takes it where the cells alone draw: CellBody on the bulges' rank (the threshold at the
        // noise's mean, a unit edge, the full swing: the share with cov 1 - coverage), the type's lift not asked. 0 outside the type's height. The type's density profile (pf.x) is not applied,
        // as in the other branches of Share.
        private double BodyShare(int layerIndex, CloudLayerDef layer, double x, double y, double z, LayerTypeTable table, double typeValue, double layerShare, double amount, ref CloudCells.CellRead read)
        {
            double b, tp;
            table.InterpolatedHeights(typeValue, out b, out tp);
            if (layerShare < b || layerShare > tp || !(tp > b)) return 0.0;
            double h = (layerShare - b) / (tp - b);
            double wx = worldToMap[0] * x + worldToMap[3] * y + worldToMap[6] * z;
            double wy = worldToMap[1] * x + worldToMap[4] * y + worldToMap[7] * z;
            double wz = worldToMap[2] * x + worldToMap[5] * y + worldToMap[8] * z;
            NoiseLayerParams prm = TypeParams(layerIndex, table, typeValue);
            double coverage = layer.EffectiveCoverage;
            bool noise = amount > 0.0 && layer.EffectiveNoiseScaleM > 0f;
            double rank = CloudShapeModel.Share(volumes, ref prm, wx, wy, wz, 1.0 - coverage, coverage, 1.0, noise ? amount : 0.0, 0.0, false);
            double bulge = CloudLookTuning.CellBulgeFor(layer), band = CloudLookTuning.CellBandFor(layer), gain = CloudLookTuning.CellFineFor(layer);
            double fineX = 0.0, fineY = 0.0;
            if (gain > 0.0 && layer.EffectiveNoiseScaleM > 0f) FineBulges(volumes.Heap, prm, wx, wy, wz, out fineX, out fineY);
            double s = CloudCells.CellBody(read, h, rank, fineX, fineY, (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM), bulge, band, gain);
            if (s <= 0.0) return 0.0;
            return CloudWater.Pow(s, layer.EffectiveWaterPowerAt(table, typeValue));
        }

        // The finer bulge octaves of a single cloud's body (the shader's CellFineBulges): the heap field of preset 1 (the shape volume's green channel on the GPU) read straight at 0.37 and 0.137
        // of its period, at the lattice's true vertical scale, each centred on 0. No heap volume (no type asked for a heap preset): both 0.
        private static void FineBulges(HeapVolumeSet heap, NoiseLayerParams prm, double px, double py, double pz, out double fine1, out double fine2)
        {
            fine1 = fine2 = 0.0;
            if (heap == null || heap.Levels == null || heap.Levels.Length == 0) return;
            prm.Untile = false;
            prm.FlowX = prm.FlowY = prm.FlowZ = 0.0;
            double qx, qy, qz;
            CloudShapeModel.LookupPoint(ref prm, px, py, pz, out qx, out qy, out qz);
            double rl = Math.Max(Math.Sqrt(px * px + py * py + pz * pz), 1.0);
            double iso = prm.SphereScale > 0.0 ? (1.0 - prm.SphereScale) * (rl - prm.ReferenceRadiusM) / rl : 0.0;
            qx += px * iso;
            qy += py * iso;
            qz += pz * iso;
            double period = HeapModel.PeriodScales * prm.ScaleM;
            double p1 = 0.37 * period, p2 = 0.137 * period;
            fine1 = CloudShapeModel.Trilinear(heap.Levels[0], heap.N, 4, 0, qx / p1 + 0.31, qy / p1 + 0.57, qz / p1 + 0.83) - 0.5;
            fine2 = CloudShapeModel.Trilinear(heap.Levels[0], heap.N, 4, 0, qx / p2 + 0.71, qy / p2 + 0.13, qz / p2 + 0.47) - 0.5;
        }
    }
}
