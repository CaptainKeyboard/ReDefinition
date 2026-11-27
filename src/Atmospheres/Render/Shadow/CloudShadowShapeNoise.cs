using System;

namespace ReDefinition.Atmospheres
{
    // The shape noise in the cloud shadow (E40, FL-FIX). The layer pass moves each layer's coverage threshold by the shape volume (CloudShapeModel.Share, the shader's
    // NoiseShare); a shadow that reads the map alone keeps the map's closed shape under a cell field the picture has broken into cells (the cumulus layer of the shipped
    // cfg, noise amount 1 at 500 m). With this switch on the shadow reads the same share: the shape noise alone, the way the pass's own light march reads it (no erosion:
    // gNoiseLod), from the same volume, scale, offsets and drift (LayerNoise), so that the shader's twin (ReDefinitionAtmospheresCloudShadow.shader includes
    // ReDefinitionAtmospheresNoise.cginc and calls NoiseShareNear) and this class are one definition. The types' height-shaped threshold (the LIFT of the cfg), the types'
    // cumuliform volumes (with the pass's blend), their edgePower exponents and their shapeSmoothing (the volume's mip) are read as the pass reads them. Not mirrored: the flow map of a body
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
        // CLOUD FIELD: the field's channels (CloudField.Build; null: not built yet, the types with a form read the map's threshold until it is), its size, and the street direction of
        // this frame (CloudLookTuning.FieldWindDeg), as the shader's _CloudField and _FieldP have them.
        private float[] field;
        private int fieldSize;
        private double fieldCos = 1.0, fieldSin;

        // The radius of each layer's middle in m (the field's map place in metres: the shaders' _PlanetR + half of inner + outer).
        private readonly double[] layerRm = new double[CloudLayerDef.MaxLayers];

        // Whether the readers have a shape to give: the shape noise (the switch is on and the volume is ready) or the cloud field. The field does not wait for the noise: the
        // shaders place a form type's clouds by it whether or not the shape noise is bound (their noise amount is then 0), and so does this twin.
        internal bool Active { get { return volumes != null || pending || field != null; } }

        // Whether the shape noise itself is read (the switch is on and the volumes are ready).
        internal bool NoiseReady { get { return volumes != null; } }

        // The switch is on and the layer pass has a noise, but its volumes are not ready (the first frames): the shaders then carry the layers' noise amount and the types'
        // lift already (the cloud shadow's _CsNoiseD and _CsParams2.z follow the switch and the noise object) while every noise read falls back to the plain threshold
        // (_NoiseP.x is 0). The readers do the same.
        private bool pending;

        // Takes the numbers of this frame from the layer pass's noise: the parameters of each layer (scale, offset, drift, seed), the volume, and the frame's rotation. on: the switch.
        // fieldData: the cloud field's channels (CloudFieldGpu.Data; null until the field is built).
        internal void Update(LayerNoise noise, ICloudLayerSource source, float[] worldToMapRows, bool on, float[] fieldData)
        {
            SetField(fieldData, CloudField.Size, CloudLookTuning.FieldWindDeg);
            layers = source == null ? 0 : Math.Min(source.LayerCount, CloudLayerDef.MaxLayers);
            for (int l = 0; l < layers; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                layerRm[l] = source.BodyRadiusM + 0.5 * (layer.InnerAltitudeM + layer.OuterAltitudeM);
            }
            if (!on || noise == null || source == null || !noise.Switches.Enabled || !noise.Ready || noise.ShapeVolumes == null || worldToMapRows == null || worldToMapRows.Length < 9)
            {
                volumes = null;
                pending = on && noise != null && source != null;
                return;
            }
            pending = false;
            volumes = noise.ShapeVolumes;
            for (int i = 0; i < 9; i++) worldToMap[i] = worldToMapRows[i];
            for (int l = 0; l < layers; l++) layerParams[l] = noise.LayerParams(l, source.LayerAt(l).EffectiveNoiseScaleM, layerRm[l]);
        }

        // A given set for the tests and the player's scene: the parameters of a layer and the rotation (rows of the world-to-map matrix). A null set: no shape noise (the layers'
        // radii are still taken from the parameters).
        internal void Set(NoiseVolumeSet set, NoiseLayerParams[] parameters, double[] worldToMapRows)
        {
            volumes = set;
            pending = false;
            layers = parameters == null ? 0 : Math.Min(parameters.Length, CloudLayerDef.MaxLayers);
            for (int l = 0; l < layers; l++)
            {
                layerParams[l] = parameters[l];
                layerRm[l] = parameters[l].ReferenceRadiusM;
            }
            if (worldToMapRows != null) Array.Copy(worldToMapRows, worldToMap, 9);
        }

        // The cloud field for the types with a form: its channels (size x size x 4; null: none), and the street direction in degrees from east.
        internal void SetField(float[] fieldData, int size, double windDeg)
        {
            field = fieldData != null && size > 0 && fieldData.Length >= size * size * 4 ? fieldData : null;
            fieldSize = size;
            double a = windDeg * Math.PI / 180.0;
            fieldCos = Math.Cos(a);
            fieldSin = Math.Sin(a);
        }

        // The coverage share of the layer at a place (x, y, z: planet-centred, map frame, metres) for the map value cov: the map moved by the shape noise through the soft edge.
        // Below 0: not given (the switch is off, the layer has no noise, or the volume is not ready), the caller uses the plain threshold.
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov)
        {
            return Share(layerIndex, layer, x, y, z, cov, null, 0.0);
        }

        // The same for a type value of the layer's type table (CLOUDR1: the type's cumuliform preset and density exponent; a null table: the layer's, no cumuliform).
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov, LayerTypeTable table, double typeValue)
        {
            return Share(layerIndex, layer, x, y, z, cov, table, typeValue, double.NaN);
        }

        // The same at a height of the layer (layerShare, 0 to 1 over the layer's thickness): the type's LIFT lowers the map there as the pass does (cov -= lift x the layer's headroom, after
        // the clear-sky rule), with or without noise in the layer; NaN: no height given, no lift (a null table has none either).
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov, LayerTypeTable table, double typeValue, double layerShare)
        {
            return Share(layerIndex, layer, x, y, z, cov, table, typeValue, layerShare, double.NaN, double.NaN);
        }

        // The same with the type's LOCAL base and top at the place (shares of the layer, after the height maps: LayerGeometry.LocalBaseTop, as the shader's TypeSlab has them); NaN: the
        // type table's own. A type with a form takes its body's height and the point's height in it from them, as the shader does.
        internal double Share(int layerIndex, CloudLayerDef layer, double x, double y, double z, double cov, LayerTypeTable table, double typeValue, double layerShare, double localBase,
                              double localTop)
        {
            if (layerIndex < 0 || layerIndex >= layers) return -1.0;
            // The three states of the shaders. The noise read (volumes ready). Pending (the switch on, the volumes not ready): the layers' noise amount and the types' lift
            // as with the noise, every noise read the plain threshold. Off (the switch off, no noise): the noise amount 0 and no lift (_CsNoiseD, _CsParams2.z). In all
            // three the field places a form type's clouds; off, every other type reads the map's plain threshold (-1: the caller's).
            bool noiseRead = volumes != null, noiseOn = noiseRead || pending;
            if (!noiseOn && field == null) return -1.0;
            double layerAmount = noiseOn ? layer.EffectiveNoiseAmount : 0.0;
            // Master plan 43: the type's extra noise amount b at this height (channel b of the pass's profile; 0 without a height or a table); the erosion gain g has no erosion here.
            double typeH = table == null || double.IsNaN(layerShare) ? double.NaN : table.TypeHeightOf(typeValue, layerShare);
            double extra = double.IsNaN(typeH) ? 0.0 : table.NoiseAmountOf(typeValue, typeH);
            double amount = layerAmount * (1.0 + extra);
            double lift = !noiseOn || table == null || double.IsNaN(layerShare) ? 0.0 : table.LiftOf(typeValue, layerShare);
            // The map value after the clear-sky rule.
            double covMap = layer.ClearSkyMap(cov);
            CloudCells.CellRead read = default(CloudCells.CellRead);
            // CLOUD FIELD: a type with a form stands where the field puts it, as the layer pass and the cloud shadow's shader draw it (CloudField.Cloud, the twin of the shader's
            // FieldCloud). It needs the type and the height at the point; a caller without them, and the frames before the field is built, read the map's threshold below.
            int form = field != null && table != null && !double.IsNaN(layerShare) ? table.FormAtTypeValue(typeValue) : 0;
            if (form > 0)
            {
                // The shaders' coverage bound, asked before the field is read: where even the noise's highest value cannot lift the map above the threshold there is no cloud
                // (the cloud shadow shader's noiseBound with the layer's largest extra noise amount; without it the field's lowest ranks kept a body's rim at a share of 0).
                if (covMap + layer.EffectiveCoverage - 1.0 + 0.5 * Math.Abs(layerAmount) * (1.0 + table.MaxNoiseAmount) + 0.5 * Math.Max(layer.EffectiveEdgeSoftness, 1e-4f) <= 0.0)
                    return 0.0;
                double b, tp;
                table.InterpolatedHeights(typeValue, out b, out tp);
                if (!double.IsNaN(localBase) && !double.IsNaN(localTop))
                {
                    b = localBase;
                    tp = localTop;
                }
                if (layerShare < b || layerShare > tp || !(tp > b)) return 0.0;
                double r = Math.Sqrt(x * x + y * y + z * z);
                if (r > 0.0)
                {
                    double latDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, z / r))) * (180.0 / Math.PI);
                    double lonDeg = Math.Atan2(y, x) * (180.0 / Math.PI);
                    double u = (lonDeg - layer.MapShiftDeg + 180.0) / 360.0, v = (90.0 - latDeg) / 180.0;
                    double blended;
                    int cloud = CloudField.Cloud(field, fieldSize, CloudField.Classes[form], fieldCos, fieldSin, covMap, layer.EffectiveCoverage,
                                                 Math.Max(layer.EffectiveEdgeSoftness, 1e-4f), layerAmount, u, v, layerRm[layerIndex],
                                                 (layerShare - b) / (tp - b), (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM), CloudLookTuning.CellBulgeFor(layer),
                                                 CloudLookTuning.CellBandFor(layer), CloudLookTuning.CellFineFor(layer), out blended, out read);
                    if (cloud == 0) return 0.0;
                    if (cloud > 0) return BodyShare(layerIndex, layer, x, y, z, table, typeValue, layerShare, b, tp, amount, ref read);
                    covMap = blended;   // the map draws (a cover share above 0.8): the blended value takes the lift and the noise below
                }
            }
            if (!noiseRead || !(amount > 0.0) || !(layer.EffectiveNoiseScaleM > 0f))
            {
                if (!(lift > 0.0) && form <= 0) return -1.0;
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

        // The layer's noise parameters for a type value: its cumuliform preset and the type's shapeSmoothing (the mip of the shape volume and the cumuliform read), as the shader's gCumuliformPreset and gNoiseSm
        // and CloudCoverOracle take them.
        private NoiseLayerParams TypeParams(int layerIndex, LayerTypeTable table, double typeValue)
        {
            NoiseLayerParams prm = layerParams[layerIndex];
            prm.CumuliformPreset = table == null ? 0 : table.CumuliformAtTypeValue(typeValue);
            if (table != null)
            {
                prm.SmoothingScales = table.SmoothingOf(typeValue);
                prm.ShapeLod = CloudShapeModel.SmoothingLod(prm.SmoothingScales, volumes.ShapeN);
            }
            return prm;
        }

        // The share of a cloud's body at a height of the layer, as the shader's LayerExtinction takes it where the field's read says "a body": CellBody on the bulges' rank (the threshold at the
        // noise's mean, a unit edge, the full swing: the share with cov 1 - coverage), the type's lift not asked. 0 outside the type's height. The type's density profile (pf.x) is not applied,
        // as in the other branches of Share.
        private double BodyShare(int layerIndex, CloudLayerDef layer, double x, double y, double z, LayerTypeTable table, double typeValue, double layerShare, double b, double tp, double amount,
                                 ref CloudCells.CellRead read)
        {
            if (layerShare < b || layerShare > tp || !(tp > b)) return 0.0;
            double h = (layerShare - b) / (tp - b);
            double wx = worldToMap[0] * x + worldToMap[3] * y + worldToMap[6] * z;
            double wy = worldToMap[1] * x + worldToMap[4] * y + worldToMap[7] * z;
            double wz = worldToMap[2] * x + worldToMap[5] * y + worldToMap[8] * z;
            double coverage = layer.EffectiveCoverage;
            double bulge = CloudLookTuning.CellBulgeFor(layer), band = CloudLookTuning.CellBandFor(layer), gain = CloudLookTuning.CellFineFor(layer);
            // Without the shape noise the bulges are at their mean (the shaders' rank with the amount 0 is 0.5): the bare body.
            if (volumes == null)
            {
                double bare = CloudCells.CellBody(read, h, 0.5, 0.0, 0.0, (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM), bulge, band, gain);
                return bare <= 0.0 ? 0.0 : CloudWater.Pow(bare, layer.EffectiveWaterPowerAt(table, typeValue));
            }
            NoiseLayerParams prm = TypeParams(layerIndex, table, typeValue);
            bool noise = amount > 0.0 && layer.EffectiveNoiseScaleM > 0f;
            // The rank of the bulge noise: the threshold at the noise's mean, a unit edge and the FULL swing (amount 1, whatever the layer's noise amount: the shaders pass 1 where
            // the layer has noise at all), so that the share is the noise's value itself. Until master plan 47.U stage 1 the layer's own amount was passed here: right only for a
            // layer of amount 1 (the cumulus layer), wrong for the cumulonimbus layer's 0.4 (the review of the stage found it).
            double rank = CloudShapeModel.Share(volumes, ref prm, wx, wy, wz, 1.0 - coverage, coverage, 1.0, noise ? 1.0 : 0.0, 0.0, false);
            double fineX = 0.0, fineY = 0.0;
            if (gain > 0.0 && layer.EffectiveNoiseScaleM > 0f) FineBulges(volumes.Cumuliform, prm, wx, wy, wz, out fineX, out fineY);
            double s = CloudCells.CellBody(read, h, rank, fineX, fineY, (tp - b) * (layer.OuterAltitudeM - layer.InnerAltitudeM), bulge, band, gain);
            if (s <= 0.0) return 0.0;
            return CloudWater.Pow(s, layer.EffectiveWaterPowerAt(table, typeValue));
        }

        // The finer bulge octaves of a cloud's body (the shader's CellFineBulges): the cumuliform field of preset 1 (the shape volume's green channel on the GPU) read straight at 0.37 and 0.137
        // of its period, at the lattice's true vertical scale, each centred on 0. No cumuliform volume (no type asked for a cumuliform preset): both 0.
        private static void FineBulges(CumuliformVolumeSet cumuliform, NoiseLayerParams prm, double px, double py, double pz, out double fine1, out double fine2)
        {
            fine1 = fine2 = 0.0;
            if (cumuliform == null || cumuliform.Levels == null || cumuliform.Levels.Length == 0) return;
            prm.Untile = false;
            prm.FlowX = prm.FlowY = prm.FlowZ = 0.0;
            double qx, qy, qz;
            CloudShapeModel.LookupPoint(ref prm, px, py, pz, out qx, out qy, out qz);
            double rl = Math.Max(Math.Sqrt(px * px + py * py + pz * pz), 1.0);
            double iso = prm.SphereScale > 0.0 ? (1.0 - prm.SphereScale) * (rl - prm.ReferenceRadiusM) / rl : 0.0;
            qx += px * iso;
            qy += py * iso;
            qz += pz * iso;
            double period = CumuliformModel.PeriodScales * prm.ScaleM;
            double p1 = 0.37 * period, p2 = 0.137 * period;
            fine1 = CloudShapeModel.Trilinear(cumuliform.Levels[0], cumuliform.N, 4, 0, qx / p1 + 0.31, qy / p1 + 0.57, qz / p1 + 0.83) - 0.5;
            fine2 = CloudShapeModel.Trilinear(cumuliform.Levels[0], cumuliform.N, 4, 0, qx / p2 + 0.71, qy / p2 + 0.13, qz / p2 + 0.47) - 0.5;
        }
    }
}
