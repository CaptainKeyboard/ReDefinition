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
        Unchecked = 3
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
            { OwnCloudModule.TuneWhitePoint, new[] { 0f, 1f } }
        };

        private static Dictionary<string, KeySection> Build()
        {
            var map = new Dictionary<string, KeySection>(StringComparer.Ordinal);
            Put(map, KeySection.Shape,
                CloudLookTuning.TuneSigma, CloudLookTuning.TuneNoiseAmount, CloudLookTuning.TuneNoiseSize, CloudLookTuning.TuneDensityRemap,
                CloudSoftDensity.TuneCoverMeaning,
                CloudLookTuning.TuneBillowLight, CloudLookTuning.TuneBillowContrast, CloudLookTuning.TuneBillowTop, CloudLookTuning.TuneBillowFine,
                CloudLookTuning.TuneBillowFineFreq, CloudLookTuning.TuneBillowSharpen);
            Put(map, KeySection.Edge,
                CloudLookTuning.TuneSoftness, CloudLookTuning.TuneFadeFar, CloudLookTuning.TuneFadeNearShare, CloudLookTuning.TuneFarHaze);
            Put(map, KeySection.Light,
                OwnCloudModule.TuneSunGain, OwnCloudModule.TuneSunUnit, OwnCloudModule.TuneExposure, OwnCloudModule.TuneOutputGamma, OwnCloudModule.TuneWhitePoint,
                OwnCloudModule.TuneWhiteAltitudeM, OwnCloudModule.TuneMoonGain, OwnCloudModule.TuneSunAirWithScatterer,
                CloudHazePlan.TuneAirSource, CloudHazePlan.TuneColour, CloudHazePlan.TuneShare, CloudHazePlan.TuneR, CloudHazePlan.TuneG, CloudHazePlan.TuneB, CloudHazePlan.TuneSky,
                CloudLookTuning.TuneHazeStrength,
                OwnCloudOptions.TuneMoonMinShare, OwnCloudOptions.TuneOctaveA, OwnCloudOptions.TuneOctaveB, OwnCloudOptions.TuneOctaveC, OwnCloudOptions.TuneMsBrightness,
                OwnCloudOptions.TuneDiffusion, OwnCloudOptions.TuneDiffusionTau, OwnCloudOptions.TuneMsIsotropy, OwnCloudOptions.TuneAmbientOcclusion, OwnCloudOptions.TuneAmbientGain,
                OwnCloudOptions.TuneSkyTint, OwnCloudOptions.TuneAmbientFloor, OwnCloudOptions.TuneAmbientColumnTop, OwnCloudOptions.TuneCellGain, OwnCloudOptions.TuneCellBeta,
                OwnCloudOptions.TuneCellExtrapolation, OwnCloudOptions.TuneCellSide, OwnCloudOptions.TuneCellOctaves, OwnCloudOptions.TuneCellIncidencePower,
                OwnCloudOptions.TuneCellIncidenceRef, OwnCloudOptions.TuneCellIncidenceEps, LightWriter.TuneSunDimWeight, LightWriter.TuneSunDimWeightEve,
                CloudShadowStepPolicy.TuneKey, CloudShadowShapeNoise.TuneKey);
            Put(map, KeySection.Quality,
                OwnCloudModule.TuneAfterScatterer, OwnCloudModule.TuneCameraEvent, OwnCloudModule.TuneReaddEveryFrame, OwnCloudModule.TuneStatsS,
                OwnCloudOptions.TuneCoverageBound, OwnCloudOptions.TuneEmptyReach, OwnCloudOptions.TuneSpanSpacing, OwnCloudOptions.TuneSpanSpacingMin, OwnCloudOptions.TuneLeapScale,
                OwnCloudOptions.TuneWarpTable, OwnCloudOptions.TuneLightStride, OwnCloudOptions.TuneLightCutoff, OwnCloudOptions.TuneEmptyLeap, OwnCloudOptions.TuneCheapShare,
                OwnCloudOptions.TuneFogShare, OwnCloudOptions.TuneGridFine, OwnCloudOptions.TuneSpanTight, OwnCloudOptions.TuneDitherSequence, OwnCloudOptions.TuneDitherFix,
                OwnCloudOptions.TuneAccumulate, OwnCloudOptions.TuneReconResolution, OwnCloudOptions.TuneLayerMotion, OwnCloudOptions.TuneMotionVectors,
                OwnCloudOptions.TuneMotionAudit, EdgeSettings.TuneReconstructor, EdgeSettings.TuneEdgeAdaptive, EdgeSettings.TuneThreshold, EdgeSettings.TuneDilation,
                EdgeSettings.TunePersist,
                OwnCloudOptions.TuneCutWeight, OwnCloudOptions.TuneStepGrowth, OwnCloudOptions.TuneTapStart, OwnCloudOptions.TuneTapEnd, OwnCloudOptions.TuneTapFactor,
                OwnCloudOptions.TuneFootScale, OwnCloudOptions.TuneCapKeep);
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

        // The group of the per-layer keys by their prefix: the look factors of a layer are Shape, the rest (map and type values, deck rule, shift) Weather.
        internal static KeySection SectionOfLayerKey(string prefix)
        {
            if (prefix == CloudLookTuning.TuneSigma + "." || prefix == CloudLookTuning.TuneNoiseAmount + "." || prefix == CloudLookTuning.TuneNoiseSize + ".") return KeySection.Shape;
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
