using System;
using System.Collections.Generic;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;

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
    //   shq3       shadow.resolution 3 (the cloud shadow marched at a third of the camera's size; OPT4), shq4 at a quarter
    //   bare       layers.costDebug 7 (no light march, no shape noise, no erosion: what the march's own walk, the maps and the type tables cost)
    // The orbit_edge view is not run (its placement costs minutes and the 19-minute limit holds 7 variants x 4 views).
    //   plain2     the same as plain, run last: the drift of the start
    //   res2       recon.resolution 0.5 (the march target's side; plain is 0.33), res4 0.25, res3 0.33 and res3cat (0.33 with the Catmull history forced on) equal plain: controls
    // Each view holds PerfDwellS seconds (the GPU profiler writes a block every 10 s, so a window of 20 s holds one whole block) and takes no pictures and no noise reading.
    internal sealed partial class T09Probe
    {
        // 8 s a view: some 400 frames at 50 fps, enough for the timing rows (owner 2026-11-17: the pauses were too long; it was 20 s).
        private const double PerfDwellS = 8.0;

        // -t09PerfDwell S (the 48 fps block's profiler start): a perf view holds S seconds instead, so that whole 10 s profiler blocks lie inside it.
        private double PerfDwell
        {
            get
            {
                string s = CommandLine.After(Args(), "-t09PerfDwell", true);
                double d;
                return s != null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d > 0 ? d : PerfDwellS;
            }
        }

        // The grid points the start began with (the profile's), read before the first variant changes them.
        private double baseSamples = double.NaN, baseShadows = double.NaN, baseRays = double.NaN, baseRagged = double.NaN, baseVariation = double.NaN,
            baseStepGrowth = double.NaN, baseSkipEmpty = double.NaN, baseDetailLod = double.NaN, baseTapLod = double.NaN,baseLightCache = double.NaN, baseSceneWb = double.NaN,
            baseCloudRotation = double.NaN, baseShadowQuality = double.NaN;

        private bool PerfStart
        {
            get { return StartId != null && StartId.StartsWith("perf", StringComparison.Ordinal); }
        }

        // A perf start whose id holds "pic" also saves the full-size pictures of each view (no noise reading), so that a lever's cost and its picture come from one run; the cloud field
        // moves on between the variants (a variant's four views take about two minutes), which the pictures' labels say.
        private bool PerfPictures
        {
            get { return PerfStart && StartId.IndexOf("pic", StringComparison.Ordinal) >= 0; }
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
                case "shq3":
                case "shq4":
                case "shtap1":
                case "nomv":
                case "nowb":
                case "leap":
                case "leapshell":
                case "ceil":
                case "ceilr33":
                case "ceilvis":
                case "ceilvis8":
                case "ceilvis8s2":
                case "ceilvis8lc":
                case "ceilvis8rot":
                case "ceilvis8lcrot":
                case "ceilvis8nl":
                case "ceilvis8sd":
                case "ceilvis8c2":
                case "ceilvis8c4":
                case "ceilvis8c6":
                case "ceilvis8c7":
                case "nolayers":
                case "plain2":
                case "res66":
                case "res2":
                case "res3":
                case "res3cat":
                case "res4":
                case "cut5":
                case "cut10":
                case "cut20":
                case "cells":
                case "grow1":
                case "grow2":
                case "grow2lod":
                case "tri":
                case "r3":
                case "r3n":
                case "r4n":
                case "lvrec":
                case "lvfar8":
                case "lvfull":
                case "grow":
                case "step12":
                case "sun4":
                case "shadowlow":
                case "r3td":
                case "pre":
                case "combo":
                case "lvall":
                case "lvall2":
                case "lvallr3":
                case "samp75":
                case "samp50":
                case "r3samp75":
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
            baseStepGrowth = AtmospheresSettings.Number(AtmospheresSettings.Keys.LayerStepGrowth);
            baseSkipEmpty = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudSkipEmpty);
            baseDetailLod = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudDetailLod);
            baseTapLod = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudTapLod);
            baseLightCache = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudLightCache);
            baseSceneWb = AtmospheresSettings.Number(AtmospheresSettings.Keys.SceneWhiteBalance);
            baseCloudRotation = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudRotation);
            baseShadowQuality = AtmospheresSettings.Number(AtmospheresSettings.Keys.CloudShadowQuality);
        }

        private void PerfRestoreBase()
        {
            Game.SetLayerMask(0);
            if (double.IsNaN(baseSamples)) return;
            SetSetting(AtmospheresSettings.Keys.CloudSamples, baseSamples);
            SetSetting(AtmospheresSettings.Keys.CloudShadows, baseShadows);
            SetSetting(AtmospheresSettings.Keys.RtOwnClouds, baseRays);
            SetSetting(AtmospheresSettings.Keys.BaseRagged, baseRagged);
            SetSetting(AtmospheresSettings.Keys.CloudBaseVariation, baseVariation);
            SetSetting(AtmospheresSettings.Keys.LayerStepGrowth, baseStepGrowth);
            SetSetting(AtmospheresSettings.Keys.CloudSkipEmpty, baseSkipEmpty);
            SetSetting(AtmospheresSettings.Keys.CloudDetailLod, baseDetailLod);
            SetSetting(AtmospheresSettings.Keys.CloudTapLod, baseTapLod);
            SetSetting(AtmospheresSettings.Keys.CloudLightCache, baseLightCache);
            SetSetting(AtmospheresSettings.Keys.SceneWhiteBalance, baseSceneWb);
            SetSetting(AtmospheresSettings.Keys.CloudRotation, baseCloudRotation);
            SetSetting(AtmospheresSettings.Keys.CloudShadowQuality, baseShadowQuality);
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
                // res66 (2026-11-19, the owner's comparison of the cloud resolutions with TVC): two thirds of the render size.
                case "res66": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.66f); break;
                case "res2": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.5f); break;
                case "res3": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.33f); break;
                case "res3cat": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.33f); ForceTuning(OwnCloudOptions.TuneCatmull, 1f); break;
                case "res4": ForceTuning(OwnCloudOptions.TuneReconResolution, 0.25f); break;
                // The 48 fps plan (2026-11-19): what the clouds beyond 5, 10 and 20 km cost (layers.costCutM; cost attribution, the picture is wrong).
                case "cut5": ForceTuning(OwnCloudOptions.TuneCostCutM, 5000f); break;
                case "cut10": ForceTuning(OwnCloudOptions.TuneCostCutM, 10000f); break;
                case "cut20": ForceTuning(OwnCloudOptions.TuneCostCutM, 20000f); break;
                // The owner's question 2026-11-19 (a field of equal cotton balls from above): the same views with the single-cloud lattice (cloud.cells 1, the default 11-17 to 11-19; now a
                // shape-test tool, the weather map decides the clouds).
                case "cells": ForceTuning(CloudLookTuning.TuneCells, 1f); break;
                // The 48 fps plan, lever 1a (2026-11-19): the look marches far spans in its 6 m steps (layers.stepGrowth 0); the step grows with the span's distance
                // again (1e-4 / 2e-4 per m, 2e-4 the frame's value before the look: a 3x step at 10 km), near clouds unchanged; grow2lod adds the T16 distance LODs.
                case "grow1": ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, 1e-4f); break;
                case "grow2": ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, 2e-4f); break;
                case "grow2lod":
                    ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, 2e-4f);
                    SetSetting(AtmospheresSettings.Keys.CloudTapLod, 1.0);
                    SetSetting(AtmospheresSettings.Keys.CloudDetailLod, 1.0);
                    break;
                // HIST fps (2026-11-20, the history work's variants in the game): tri the march as one procedural triangle (today's 2 x 2 otherwise); r3 the rotation over
                // 3 x 3 blocks with today's history; r3n / r4n the rotation over 3 x 3 / 4 x 4 with the new history (the march's own distance, the sample count, the drop
                // by distance at 0.3 octaves, the clip at 2 sigma; with the distance target the march draws pass 6, so the triangle key does not act there).
                case "tri": ForceTuning(OwnCloudOptions.TuneMarchTriangle, 1f); break;
                case "r3": ForceTuning(OwnCloudOptions.TuneReconRotate, 3f); break;
                case "r3n":
                case "r4n":
                    ForceTuning(OwnCloudOptions.TuneReconRotate, name == "r3n" ? 3f : 4f);
                    ForceTuning(OwnCloudOptions.TuneMarchTriangle, 1f);
                    ForceTuning(OwnCloudOptions.TuneTrueDistance, 1f);
                    ForceTuning(OwnCloudOptions.TuneAge, 1f);
                    ForceTuning(OwnCloudOptions.TuneDistanceReject, 0.3f);
                    ForceTuning(OwnCloudOptions.TuneClipSigma, 2f);
                    break;
                // LV3 (2026-11-20, wolken-m2a-technik.md 11): the light volume's cost in the game. lvrec the recommendation (the far light from 8 km and the lateral spread from the
                // propagation fill on 156.25 m voxels, transmittance stored over 8), lvfull the same spread with the full sun march at every distance, lvfar8 the far light from
                // 8 km alone (direct fill, no spread).
                case "lvrec":
                case "lvfull":
                    ForceTuning(CloudLookTuning.TuneLightVolume, 1f);
                    ForceTuning(CloudLookTuning.TuneLvHandOver, name == "lvrec" ? 8f : -1f);
                    ForceTuning(CloudLookTuning.TuneLvSpread, 3f);
                    ForceTuning(CloudLookTuning.TuneLvFill, 1f);
                    ForceTuning(CloudLookTuning.TuneLvStoreK, 8f);
                    ForceTuning(CloudLookTuning.TuneLvVoxel, 156.25f);
                    break;
                case "lvfar8":
                    ForceTuning(CloudLookTuning.TuneLightVolume, 1f);
                    ForceTuning(CloudLookTuning.TuneLvHandOver, 8f);
                    break;
                // PERF48 (2026-11-21, the 48 fps block): one lever each against today's look (OwnCloudOptions.cs, the look constants of ca5f67da).
                //   grow       layers.stepGrowth -1 (below 0: the step quality's growth along the ray instead of the look's 0)
                //   step12     layers.minStepM 12 (the look's 6 m)
                //   sun4       layers.sunSteps 4 (the look's 8 taps towards the sun)
                //   shadowlow  the cloud shadow at its cheapest: the setting cloudShadowQuality 0 (Low: 4 steps per span, 1 disc tap) and shadow.resolution 4 (a quarter size)
                //   r3td       recon.rotate 3 with recon.trueDistance 1
                //   pre        the product before the full-size look: cloudRotation 0, recon.resolution 0.33, and the look keys handed back (layers.minStepM 0,
                //              layers.stepGrowth -1, layers.sunMarchM 0, layers.sunSteps 0: the step and light quality levels' own values)
                //   combo      grow + sun4 + r3td
                case "grow": ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, -1f); break;
                case "step12": ForceTuning(OwnCloudOptions.TuneMinStepM, 12f); break;
                case "sun4": ForceTuning(OwnCloudOptions.TuneSunSteps, 4f); break;
                case "shadowlow":
                    SetSetting(AtmospheresSettings.Keys.CloudShadowQuality, 0.0);
                    ForceTuning(OwnCloudOptions.TuneShadowResolution, 4f);
                    break;
                case "r3td":
                    ForceTuning(OwnCloudOptions.TuneReconRotate, 3f);
                    ForceTuning(OwnCloudOptions.TuneTrueDistance, 1f);
                    break;
                case "pre":
                    SetSetting(AtmospheresSettings.Keys.CloudRotation, 0.0);
                    ForceTuning(OwnCloudOptions.TuneReconResolution, 0.33f);
                    ForceTuning(OwnCloudOptions.TuneMinStepM, 0f);
                    ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, -1f);
                    ForceTuning(OwnCloudOptions.TuneSunMarchM, 0f);
                    ForceTuning(OwnCloudOptions.TuneSunSteps, 0f);
                    break;
                case "combo":
                    ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, -1f);
                    ForceTuning(OwnCloudOptions.TuneSunSteps, 4f);
                    ForceTuning(OwnCloudOptions.TuneReconRotate, 3f);
                    ForceTuning(OwnCloudOptions.TuneTrueDistance, 1f);
                    break;
                // The light-volume fps block (2026-11-21): the sun's light of every lit sample from the volume (CloudLight.cginc LightVolumePlan: with the hand-over at 0 the far
                // path's share is saturate(dist / _LvA.y) with _LvA.y = max(band x 0, 1 m) (LightVolume.cs Bind), so 1 beyond a metre and cloud.lightVolume.band has no width
                // to keep the full march in; the share is then multiplied by the volume read's weight, below 1 only outside or at the fade of a cascade's window), the far
                // path's near taps 1 (lvall) or 2 (lvall2); lvallr3 is lvall with recon.rotate 3 and recon.trueDistance 1. The volume's other keys stay at their defaults.
                // The step budget block (2026-11-21): the march's most samples per span. OwnCloudOptions.Apply(LayerFrame): f.MaxSteps = clamp(round(ScaleSamples(the step
                // quality's maxSteps, cloudSamples) x layers.gridFine), 4, 256); at Medium 48 x 256 / 48 = 256 x gridFine 1 (GridFineDefault) = 256. samp75 / samp50 set
                // layers.gridFine 0.75 / 0.5 (192 / 128 samples per span at most); r3samp75 adds recon.rotate 3 and recon.trueDistance 1 to samp75.
                case "samp75":
                case "samp50":
                case "r3samp75":
                    ForceTuning(OwnCloudOptions.TuneGridFine, name == "samp50" ? 0.5f : 0.75f);
                    if (name == "r3samp75")
                    {
                        ForceTuning(OwnCloudOptions.TuneReconRotate, 3f);
                        ForceTuning(OwnCloudOptions.TuneTrueDistance, 1f);
                    }
                    break;
                case "lvall":
                case "lvall2":
                case "lvallr3":
                    ForceTuning(CloudLookTuning.TuneLightVolume, 1f);
                    ForceTuning(CloudLookTuning.TuneLvHandOver, 0f);
                    ForceTuning(CloudLookTuning.TuneLvFarTaps, name == "lvall2" ? 2f : 1f);
                    if (name == "lvallr3")
                    {
                        ForceTuning(OwnCloudOptions.TuneReconRotate, 3f);
                        ForceTuning(OwnCloudOptions.TuneTrueDistance, 1f);
                    }
                    break;
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
                case "shq3": ForceTuning(OwnCloudOptions.TuneShadowResolution, 3f); break;
                case "shq4": ForceTuning(OwnCloudOptions.TuneShadowResolution, 4f); break;
                case "shtap1": ForceTuning(OwnCloudOptions.TuneShadowTaps, 1f); break;
                // K-T2 2026-11-18: the page fault with DLSS after a placement; the clouds' motion vectors left out of the upscaler's (the host's stand).
                case "nomv": ForceTuning(OwnCloudOptions.TuneMotionVectors, 0f); break;
                // nowb (audit A2, 2026-11-19): the scene's white balance off, the clouds' own white point as before; plain has it on (the default).
                case "nowb": SetSetting(AtmospheresSettings.Keys.SceneWhiteBalance, 0.0); break;
                // A4 2026-11-18: the shell leap alone (look-neutral in the player: cover and spectra equal) and with the fine steps in the shell, against plain for the cost.
                case "leap": ForceTuning(CloudLookTuning.TuneCellTrace, 1f); break;
                case "leapshell":
                    ForceTuning(CloudLookTuning.TuneCellTrace, 1f);
                    ForceTuning(CloudLookTuning.TuneCellShell, 1f);
                    break;
                // A4 2026-11-18: the owner chose the player's ceiling march at full size (e40puff k_ceil_rec) as the look; its cost in the game. ceil: that march at the full
                // size of the camera; ceilr33: the same march at the product's reconstruction size (the owner saw the third of the size as a visible loss).
                case "ceil":
                case "ceilr33":
                case "ceilvis":
                case "ceilvis8":
                case "ceilvis8s2":
                case "ceilvis8lc":
                case "ceilvis8rot":
                case "ceilvis8lcrot":
                case "ceilvis8nl":
                case "ceilvis8sd":
                case "ceilvis8c2":
                case "ceilvis8c4":
                case "ceilvis8c6":
                case "ceilvis8c7":
                    {
                        // ceilvis (search round 2, owner 2026-11-18): the ceiling with the product's three savings where a cloud is hidden or adds little, which the owner judged
                        // equal (e40puff k_vis_all): the early out at 1 % of the light, the step growth with the lost transmittance, the light cutoff. ceilvis8 (round 3): with 8 sun taps.
                        // ceilvis8s2 (round 4): with the product's light stride 2, the owner's accepted look; ceilvis8nl: the same without any light march (the cost debug bit 1, the
                        // picture is wrong), so the light's share of the accepted look's cost is measured.
                        bool vis = name.StartsWith("ceilvis", StringComparison.Ordinal);
                        bool eight = name.StartsWith("ceilvis8", StringComparison.Ordinal);
                        SetSetting(AtmospheresSettings.Keys.CloudSamples, 256.0);
                        if (!vis) SetSetting(AtmospheresSettings.Keys.LayerStepGrowth, 0.0);
                        ForceTuning(OwnCloudOptions.TuneMinStepM, 6f);
                        ForceTuning(OwnCloudOptions.TuneFrameStepGrowth, 0f);
                        if (!vis) ForceTuning(OwnCloudOptions.TuneEarlyOut, 0.001f);
                        ForceTuning(OwnCloudOptions.TuneEmptyLeap, 1f);
                        // The accepted look's light stride 2 for every variant from round 4 on (s2, nl, sd and the cost split c<N>).
                        if (name == "ceil" || name == "ceilr33" || name == "ceilvis" || name == "ceilvis8") ForceTuning(OwnCloudOptions.TuneLightStride, 1f);
                        // ceilvis8c<N> (2026-11-18, where the accepted look's time goes): the cost debug bits N (1 no light march, 2 no shape noise, 4 no erosion); the picture is wrong.
                        if (name.StartsWith("ceilvis8c", StringComparison.Ordinal))
                            ForceTuning(OwnCloudOptions.TuneCostDebug, float.Parse(name.Substring("ceilvis8c".Length), System.Globalization.CultureInfo.InvariantCulture));
                        // ceilvis8sd (the owner's perf program, points 2 and 6, both judged equal in the player): with the tile pass's skip of empty tiles and the detail dropped below a pixel.
                        if (name == "ceilvis8sd")
                        {
                            SetSetting(AtmospheresSettings.Keys.CloudSkipEmpty, 1.0);
                            SetSetting(AtmospheresSettings.Keys.CloudDetailLod, 1.0);
                        }
                        // ceilvis8lc (2026-11-19, the owner judged the player's lc_g128 equal): the accepted look with the light cache (3 near sun taps, 128 x 128 x 32 voxels).
                        if (name == "ceilvis8lc" || name == "ceilvis8lcrot") SetSetting(AtmospheresSettings.Keys.CloudLightCache, 1.0);
                        // ceilvis8rot (2026-11-19, the owner judged the player's rot2ac equal still, turning and flying): the accepted look as a rotation over 2 x 2 blocks
                        // (recon.rotate 2); ceilvis8lcrot the same with the light cache.
                        if (name.EndsWith("rot", StringComparison.Ordinal)) ForceTuning(OwnCloudOptions.TuneReconRotate, 2f);
                        if (!vis) ForceTuning(OwnCloudOptions.TuneLightCutoff, 0f);
                        ForceTuning(OwnCloudOptions.TuneSunMarchM, 12000f);
                        ForceTuning(OwnCloudOptions.TuneSunSteps, eight ? 8f : 16f);
                        if (name == "ceilvis8nl") ForceTuning(OwnCloudOptions.TuneCostDebug, 1f);
                        if (name != "ceilr33") ForceTuning(OwnCloudOptions.TuneReconResolution, 1f);
                        break;
                    }
                // The same fault: the pass with all its targets, maps and passes, but no layer drawn (the baseline mask), so a run tells the clouds' content from the pass's resources.
                case "nolayers": Game.SetLayerMask(NoLayersMask); break;
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
