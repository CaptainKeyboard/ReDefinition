using System;

namespace ReDefinition.Atmospheres
{
    // The shape noise in the cloud shadow (E40, FL-FIX). The layer pass moves each layer's coverage threshold by the shape volume (CloudShapeModel.Share, the shader's
    // NoiseShare); a shadow that reads the map alone keeps the map's closed shape under a cell field the picture has broken into cells (the cumulus layer of the shipped
    // cfg, noise amount 1 at 500 m). With this switch on the shadow reads the same share: the shape noise alone, the way the pass's own light march reads it (no erosion:
    // gNoiseLod), from the same volume, scale, offsets and drift (LayerNoise), so that the shader's twin (ReDefinitionAtmospheresCloudShadow.shader includes
    // ReDefinitionAtmospheresNoise.cginc and calls NoiseShareNear) and this class are one definition. Not mirrored: the flow map of a body that has one, the
    // types' height-shaped threshold (the LIFT of the cfg), and the height maps' ragged boundary (HeightDensity resolves the type's local base and top as before).
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
            if (volumes == null || layerIndex < 0 || layerIndex >= layers) return -1.0;
            double amount = layer.EffectiveNoiseAmount;
            if (!(amount > 0.0) || !(layer.EffectiveNoiseScaleM > 0f)) return -1.0;
            // The lattice is fixed in the world frame: the map frame's point turned back (the transpose of the rotation).
            double wx = worldToMap[0] * x + worldToMap[3] * y + worldToMap[6] * z;
            double wy = worldToMap[1] * x + worldToMap[4] * y + worldToMap[7] * z;
            double wz = worldToMap[2] * x + worldToMap[5] * y + worldToMap[8] * z;
            NoiseLayerParams prm = layerParams[layerIndex];
            double share = CloudShapeModel.Share(volumes, ref prm, wx, wy, wz, layer.ClearSkyMap(cov), layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, amount, 0.0, false);
            return CloudWater.Pow(share, layer.EffectiveWaterPower);
        }
    }
}
