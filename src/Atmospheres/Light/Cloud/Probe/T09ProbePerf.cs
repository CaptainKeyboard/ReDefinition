using System;
using System.Collections.Generic;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the starts perfA* and perfB* (package PERF, the cost of each part of the default cloud look): the own pass at its defaults ("plain": it reconstructs, the module's
    // mode reads "reconstruct" because recon.accumulate is above 0, see ExpectedMode) and the same pass with ONE thing switched off or changed, each variant on the five views:
    //   noero      cloud.light.erosion 0 (the sun's march reads no eroded density)
    //   noblend    cloud.noise.blend 0 (no four-tap blended noise read, the untiling displacement again)
    //   noheap     the heap volume off on the cumulus layer only (edgePower stays; the cavity goes with the heap, it acts only on a type that reads one)
    //   nocatmull  recon.catmull 0 (bilinear history)
    //   g48        48 grid points (cloudSamples 48)
    //   prev       the whole previous look (the viewer's preset "Previous Look": blend 0, erosion 0, Catmull 0, heap 0 / edgePower 2 / cavity 0 on cumulus, 48 points)
    //   noshadow   cloudShadows 0
    //   norays     rtOwnClouds 0 (the ray-side pack; in the K-T2 starts raytracing is Off, so nothing asks for the pack (RtOwnClouds.Tick releases it) and this equals plain: a drift control)
    //   nolight    layers.costDebug 1 (no light march; cost attribution, the picture is wrong)
    //   noshape    layers.costDebug 2 (no shape noise read at all)
    //   noerode    layers.costDebug 4 (no erosion)
    //   shnoise0   shadow.shapeNoise 0 (the cloud shadow reads no shape noise)
    //   lightown   layers.costDebug 8 (the light march crosses the sample's own layer alone; cost attribution)
    //   noragged   baseRagged 0 (the ragged cloud base: no hashed noise per sample; the height maps stay)
    //   noheight   cloudBaseVariation 0 (the whole height variation off: no height map read, no ragged base)
    //   ostride2   light.otherStride 2 (the sun march crosses the other layers at every second march of a span; OPT3), ostride4 at every fourth
    //   noragtaps  light.raggedTaps 0 (the light path reads the smooth base; OPT3)
    //   olite      light.otherStride 2 and light.raggedTaps 0 together
    //   bare       layers.costDebug 7 (no light march, no shape noise, no erosion: what the march's own walk, the maps and the type tables cost)
    // The orbit_edge view is not run (its placement costs minutes and the 19-minute limit holds 7 variants x 4 views).
    //   plain2     the same as plain, run last: the drift of the start
    //   res2       recon.resolution 0.5 (the march target's side; plain is 0.33), res4 0.25, res3 0.33 and res3cat (0.33 with the Catmull history forced on) equal plain: controls
    // Each view holds PerfDwellS seconds (the GPU profiler writes a block every 10 s, so a window of 20 s holds one whole block) and takes no pictures and no noise reading.
    internal sealed partial class T09Probe
    {
        private const double PerfDwellS = 20.0;

        // The grid points the start began with (the profile's), read before the first variant changes them.
        private double baseSamples = double.NaN, baseShadows = double.NaN, baseRays = double.NaN, baseRagged = double.NaN, baseVariation = double.NaN;

        private bool PerfStart
        {
            get { return StartId != null && StartId.StartsWith("perf", StringComparison.Ordinal); }
        }

        private static bool IsPerfVariant(string name)
        {
            switch (name)
            {
                case "noero":
                case "noblend":
                case "noheap":
                case "nocatmull":
                case "g48":
                case "prev":
                case "noshadow":
                case "norays":
                case "nolight":
                case "noshape":
                case "noerode":
                case "shnoise0":
                case "lightown":
                case "noragged":
                case "noheight":
                case "bare":
                case "ostride2":
                case "ostride4":
                case "noragtaps":
                case "olite":
                case "plain2":
                case "res2":
                case "res3":
                case "res3cat":
                case "res4":
                    return true;
                default:
                    return false;
            }
        }

        // The settings a perf variant changes go back to the start's values at every variant (the runner restores them only after the start).
        private void PerfRememberBase()
        {
            if (!double.IsNaN(baseSamples)) return;
            baseSamples = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudSamples);
            baseShadows = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudShadows);
            baseRays = AtmospheresSettings.Number(AtmospheresSettings.Keys.RtOwnClouds);
            baseRagged = AtmospheresSettings.Number(AtmospheresSettings.Keys.BaseRagged);
            baseVariation = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudBaseVariation);
        }

        private void PerfRestoreBase()
        {
            if (double.IsNaN(baseSamples)) return;
            SetSetting(AtmospheresSettings.Keys.CloudSamples, baseSamples);
            SetSetting(AtmospheresSettings.Keys.CloudShadows, baseShadows);
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, baseRays);
            SetSetting(AtmospheresSettings.Keys.BaseRagged, baseRagged);
            SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, baseVariation);
        }

        // The cumulus layer's name in the stack that stands ("Kerbin-cumulus" where no stack is drawn yet).
        private static string CumulusLayer()
        {
            foreach (ViewerLayer l in CloudViewer.Layers())
                if (l.Name.EndsWith("cumulus", StringComparison.OrdinalIgnoreCase)) return l.Name;
            return "Kerbin-cumulus";
        }

        // True where name is a perf variant (the own pass at its defaults with the one change applied).
        private bool PerfApply(string name)
        {
            if (!IsPerfVariant(name)) return false;
            PerfRememberBase();
            UseOwn();
            PerfRestoreBase();
            switch (name)
            {
                case "noero": ForceTuning(CloudLookTuning.TuneLightErosion, 0f); break;
                case "noblend": ForceTuning(CloudLookTuning.TuneNoiseBlend, 0f); break;
                case "noheap": ForceTuning(CloudLookTuning.TuneHeapLayer + CumulusLayer(), 0f); break;
                case "nocatmull": ForceTuning(OwnCloudOptions.TuneCatmull, 0f); break;
                case "g48": SetSetting(AtmospheresSettings.Keys.CloudSamples, 48.0); break;
                case "prev":
                    {
                        string cumulus = CumulusLayer();
                        ForceTuning(CloudLookTuning.TuneNoiseBlend, 0f);
                        ForceTuning(CloudLookTuning.TuneLightErosion, 0f);
                        ForceTuning(OwnCloudOptions.TuneCatmull, 0f);
                        ForceTuning(CloudLookTuning.TuneHeapLayer + cumulus, 0f);
                        ForceTuning(CloudLookTuning.TuneEdgePowerLayer + cumulus, 2f);
                        ForceTuning(CloudLookTuning.TuneCavityLayer + cumulus, 0f);
                        SetSetting(AtmospheresSettings.Keys.CloudSamples, 48.0);
                        break;
                    }
                // The march target's side over the full target's in the history path of level 1 (recon.resolution; the default is 0.33, so plain already marches a third).
                case "res2": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.5f); break;
                case "res3": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.33f); break;
                case "res3cat": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.33f); ForceTuning(OwnCloudOptions.TuneCatmull, 1f); break;
                case "res4": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.25f); break;
                case "noshadow": SetSetting(AtmospheresSettings.Keys.CloudShadows, 0.0); break;
                case "norays": SetSetting(AtmospheresSettings.Keys.RtOwnClouds, 0.0); break;
                case "nolight": ForceTuning(OwnCloudOptions.TuneCostDebug, 1f); break;
                case "noshape": ForceTuning(OwnCloudOptions.TuneCostDebug, 2f); break;
                case "noerode": ForceTuning(OwnCloudOptions.TuneCostDebug, 4f); break;
                case "shnoise0": ForceTuning(CloudShadowShapeNoise.TuneKey, 0f); break;
                case "lightown": ForceTuning(OwnCloudOptions.TuneCostDebug, 8f); break;
                case "noragged": SetSetting(AtmospheresSettings.Keys.BaseRagged, 0.0); break;
                case "noheight": SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, 0.0); break;
                case "bare": ForceTuning(OwnCloudOptions.TuneCostDebug, 7f); break;
                case "ostride2": ForceTuning(OwnCloudOptions.TuneOtherStride, 2f); break;
                case "ostride4": ForceTuning(OwnCloudOptions.TuneOtherStride, 4f); break;
                case "noragtaps": ForceTuning(OwnCloudOptions.TuneRaggedTaps, 0f); break;
                case "olite": ForceTuning(OwnCloudOptions.TuneOtherStride, 2f); ForceTuning(OwnCloudOptions.TuneRaggedTaps, 0f); break;
            }
            return true;
        }

        // A variant that is not perf (off, plain, ...) in a perf start must not inherit a setting an earlier perf variant changed.
        private void PerfRevertForOthers()
        {
            if (PerfStart) PerfRestoreBase();
        }
    }
}
