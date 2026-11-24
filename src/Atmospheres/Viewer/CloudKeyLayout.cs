using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The groups of the viewer's key table, in the order the window shows them.
    internal enum KeySection
    {
        Shape = 0,
        Edge = 1,
        Light = 2,
        // The march and the quality: the cost controls of the pass, the reconstruction, the culling and the output stage's switches.
        Quality = 3,
        // The weather the pass is given: cover shifts, forced cells, the layers' own map and type values, the cloud speed; the generator's levers come with the weather lab.
        Weather = 4,
        // The modes and quality switches that are settings (CloudViewerModes), not tuning keys.
        Modes = 5,
        Other = 6
    }

    // When a key's value takes effect, as the code that reads it does (verified by reading the reader, see KeyLiveness for the tuning keys).
    internal enum KeyMarker
    {
        // The next frame (or the next map round, for a key that reaches the maps).
        Live = 0,
        // Only a rebuild of the generator's state (the Rebuild Weather button) shows it.
        AfterRebuildWeather = 1,
        // Only a scene change (a reload of the flight scene) shows it.
        AfterSceneChange = 2,
        // Not read through yet: shown as such, never as live.
        Unchecked = 3,
        // Only when the noise volumes are made again (a scene change, a change of the noise size or the seed): the generator reads the key, the draw never does.
        AfterVolumeRebuild = 4
    }

    // The group and the slider range of each tuning key of the own cloud pass. The group is stated for every key of CloudKeyRegistry.All (a test fails for a key without one) so that a
    // new key is placed on purpose. A range is stated where the reader clamps the key; where it does not, the range is estimated from the default (the slider only; a typed value is
    // never limited) and the window says so.
    internal static class CloudKeyLayout
    {
        private static readonly Dictionary<string, KeySection> Sections = Build();

        private static readonly Dictionary<string, float[]> Ranges = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            { CloudMotion.TuneScale, new[] { 0f, CloudMotion.MaxScale } },
            { OwnCloudOptions.TuneAccumulate, new[] { 0f, 1f } },
            { OwnCloudOptions.TuneReconResolution, new[] { 0.1f, 1f } },
            { OwnCloudModule.TuneWhitePoint, new[] { 0f, 1f } },
            // The clamps of the readers (OwnCloudOptions.Apply, CloudLookTuning.Load and the bulge getters, CloudCoverShift.Clean, EdgeSettings). layers.gridFine is the main quality lever: the reader
            // clamps it to 0.25 to 8 (the march is capped at 256 points a span), the slider shows 0.25 to 4, which holds the stops 1 (48 points), 2 (96) and 2.6667 (128) and 4 (192).
            { OwnCloudOptions.TuneGridFine, new[] { 0.25f, 4f } },
            { OwnCloudOptions.TuneLightStride, new[] { 1f, 8f } },
            { OwnCloudOptions.TuneCostCutM, new[] { 0f, 40000f } },
            { OwnCloudOptions.TuneSpanGrowthPerM, new[] { 0f, 0.002f } },
            { OwnCloudOptions.TuneSpanGrowthFromM, new[] { 0f, 20000f } },
            { OwnCloudOptions.TuneLeapVerticalShare, new[] { 0f, 1f } },
            { OwnCloudOptions.TuneReconRotate, new[] { 0f, 4f } },
            { OwnCloudOptions.TuneShadowResolution, new[] { 2f, 4f } },
            { OwnCloudOptions.TuneShadowTaps, new[] { 0f, 8f } },
            { OwnCloudOptions.TuneCheapShare, new[] { 0f, 1f } },
            { OwnCloudOptions.TuneFogShare, new[] { 0f, 1f } },
            { OwnCloudOptions.TuneCostDebug, new[] { 0f, 15f } },
            { OwnCloudOptions.TuneDitherSequence, new[] { 1f, 3f } },
            { OwnCloudOptions.TuneFootScale, new[] { 0f, 8f } },
            { OwnCloudOptions.TuneCapKeep, new[] { 1f, 8f } },
            { OwnCloudOptions.TuneMinStepM, new[] { 0f, 100f } },
            { OwnCloudOptions.TuneEarlyOut, new[] { 0f, 0.05f } },
            { OwnCloudOptions.TuneSunMarchM, new[] { 0f, 20000f } },
            { OwnCloudOptions.TuneSunSteps, new[] { 0f, 32f } },
            { EdgeSettings.TuneDilation, new[] { 0f, (float)EdgeSettings.MaxDilation } },
            { EdgeSettings.TunePersist, new[] { 1f, 32f } },
            { CloudLookTuning.TuneCoverCombine, new[] { 0f, 1f } },
            { CloudLookTuning.TuneFadeNearShare, new[] { 0f, 1f } },
            { CloudLookTuning.TuneAerialPerspective, new[] { -1f, 2f } },
            { CloudLookTuning.TuneCellLat, new[] { -90f, 90f } },
            { CloudLookTuning.TuneCellLon, new[] { -360f, 360f } },
            { CloudLookTuning.TuneBulgeLight, new[] { 0f, 8f } },
            { CloudLookTuning.TuneBulgeContrast, new[] { 0f, 8f } },
            { CloudLookTuning.TuneBulgeTop, new[] { 0f, 8f } },
            { CloudLookTuning.TuneSoloLayer, new[] { 0f, (float)CloudLayerDef.MaxLayers } },
            { CloudCoverShift.TuneShift, new[] { -1f, 1f } },
            { CloudCoverShift.TuneShiftRain, new[] { -1f, 1f } }
        };

        private static Dictionary<string, KeySection> Build()
        {
            var map = new Dictionary<string, KeySection>(StringComparer.Ordinal);
            Put(map, KeySection.Shape,
                CloudLookTuning.TuneSigma, CloudLookTuning.TuneNoiseSize,
                CloudLookTuning.TuneBulgeLight, CloudLookTuning.TuneBulgeContrast, CloudLookTuning.TuneBulgeTop, CloudLookTuning.TuneBulgeFine,
                CloudLookTuning.TuneBulgeFineFreq,
                // M2c: the stratus undulations (variety undulatus: their amount and the wavelength in the layer's thickness).
                OwnCloudOptions.TuneUndulatus, OwnCloudOptions.TuneUndulatusWavelengthShare);
            Put(map, KeySection.Edge,
                CloudLookTuning.TuneSoftness, CloudLookTuning.TuneFadeFar, CloudLookTuning.TuneFadeNearShare, CloudLookTuning.TuneAerialPerspective);
            Put(map, KeySection.Light,
                OwnCloudModule.TuneSunGain, OwnCloudModule.TuneSunUnit, OwnCloudModule.TuneExposure, OwnCloudModule.TuneOutputGamma, OwnCloudModule.TuneWhitePoint,
                OwnCloudModule.TuneWhiteAltitudeM, OwnCloudModule.TuneMoonGain, OwnCloudModule.TuneSunAirWithScatterer,
                AerialPerspectivePlan.TuneAirSource, AerialPerspectivePlan.TuneColour, AerialPerspectivePlan.TuneShare, AerialPerspectivePlan.TuneR, AerialPerspectivePlan.TuneG, AerialPerspectivePlan.TuneB, AerialPerspectivePlan.TuneSky,
                CloudLookTuning.TuneAerialPerspectiveStrength,
                OwnCloudOptions.TuneMoonMinShare, OwnCloudOptions.TuneOctaveA, OwnCloudOptions.TuneOctaveB, OwnCloudOptions.TuneOctaveC, OwnCloudOptions.TuneMsBrightness,
                OwnCloudOptions.TuneDiffusion, OwnCloudOptions.TuneDiffusionTau, OwnCloudOptions.TuneMsIsotropy, OwnCloudOptions.TuneAmbientOcclusion, OwnCloudOptions.TuneAmbientGain,
                OwnCloudOptions.TuneSkyTint, OwnCloudOptions.TuneAmbientFloor, OwnCloudOptions.TuneAmbientColumnTop, OwnCloudOptions.TuneCellGain, OwnCloudOptions.TuneCellBeta,
                OwnCloudOptions.TuneCellExtrapolation, OwnCloudOptions.TuneCellSide, OwnCloudOptions.TuneCellOctaves, OwnCloudOptions.TuneCellIncidencePower,
                OwnCloudOptions.TuneCellIncidenceRef, OwnCloudOptions.TuneCellIncidenceEps, LightWriter.TuneSunDimWeight, LightWriter.TuneSunDimWeightEve,
                CloudShadowStepPolicy.TuneKey, CloudShadowShapeNoise.TuneKey, OwnCloudOptions.TuneShadowResolution, OwnCloudOptions.TuneShadowTaps);
            Put(map, KeySection.Quality,
                OwnCloudModule.TuneAfterScatterer, OwnCloudModule.TuneCameraEvent,
                CloudDevVariant.TuneForce, OwnCloudOptions.TuneCoverageBound, OwnCloudOptions.TuneEmptyReach, OwnCloudOptions.TuneLeapScale,
                OwnCloudOptions.TuneLightStride, OwnCloudOptions.TuneLightCutoff, OwnCloudOptions.TuneEmptyLeap, OwnCloudOptions.TuneCheapShare,
                OwnCloudOptions.TuneFogShare, OwnCloudOptions.TuneCostDebug, OwnCloudOptions.TuneCostCutM, OwnCloudOptions.TuneSpanGrowthPerM,OwnCloudOptions.TuneSpanGrowthFromM, OwnCloudOptions.TuneLeapVerticalShare, OwnCloudOptions.TuneGridFine,OwnCloudOptions.TuneDitherSequence,
                OwnCloudOptions.TuneAccumulate, OwnCloudOptions.TuneReconResolution, OwnCloudOptions.TuneReconRotate, OwnCloudOptions.TuneLayerMotion, OwnCloudOptions.TuneMotionVectors,
                OwnCloudOptions.TuneMotionAudit, OwnCloudOptions.TuneHostJitter, EdgeSettings.TuneReconstructor, EdgeSettings.TuneEdgeAdaptive, EdgeSettings.TuneThreshold, EdgeSettings.TuneDilation,
                EdgeSettings.TunePersist,
                OwnCloudOptions.TuneFootScale, OwnCloudOptions.TuneCapKeep,
                OwnCloudOptions.TuneMinStepM, OwnCloudOptions.TuneEarlyOut, OwnCloudOptions.TuneSunMarchM, OwnCloudOptions.TuneSunSteps);
            Put(map, KeySection.Weather,
                CloudMotion.TuneScale, CloudCoverShift.TuneShift, CloudCoverShift.TuneShiftRain, CloudLookTuning.TuneCoverCombine, CloudLookTuning.TuneSoloLayer,
                CloudLookTuning.TuneCellKm, CloudLookTuning.TuneCellLat, CloudLookTuning.TuneCellLon);
            return map;
        }

        private static void Put(Dictionary<string, KeySection> map, KeySection section, params string[] keys)
        {
            foreach (string key in keys) map.Add(key, section);
        }

        // The group of a key; false for a key the table does not state (the registry's own keys all have one).
        internal static bool TryGetSection(string key, out KeySection section)
        {
            return Sections.TryGetValue(key ?? "", out section);
        }

        // The group of the per-layer keys by their prefix: the look factors of a layer are Shape, the rest (map and type values, low stratiform rule, shift) Weather.
        internal static KeySection SectionOfLayerKey(string prefix)
        {
            if (prefix == CloudLookTuning.TuneSigma + "." || prefix == CloudLookTuning.TuneNoiseSize + ".") return KeySection.Shape;
            if (prefix == CloudLookTuning.TuneSoftness + ".") return KeySection.Edge;
            return KeySection.Weather;
        }

        // The slider range of a key: stated where the reader clamps it, else estimated from the default. A flag is 0 to 1. estimated says which.
        internal static void Range(string key, float compiledDefault, bool flag, out float min, out float max, out bool estimated)
        {
            float[] stated;
            if (flag) { min = 0f; max = 1f; estimated = false; return; }
            if (Ranges.TryGetValue(key, out stated)) { min = stated[0]; max = stated[1]; estimated = false; return; }
            estimated = true;
            // A default below zero is a sentinel (-1: auto or the compiled value): the range starts there and reaches 1; else the range is zero to twice the default, at least one.
            min = compiledDefault < 0f ? compiledDefault : 0f;
            max = Math.Max(1f, compiledDefault * 2f);
            if (compiledDefault < 0f) max = 1f;
        }
    }
}
