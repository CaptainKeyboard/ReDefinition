using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Wires the settings of the own cloud system (AtmospheresSettings.Clouds.cs) to the option structs of the packages T01 to T05 and
    // names the tuning keys of the cost controls and the look constants. Each method reads through a value reader (a key to its number),
    // so that the tests and the settings window's model run it on any set of values; the overloads without a reader read the live
    // settings. The integration package (T09) calls them once per frame on the objects it keeps: the methods write every field they own,
    // so a call after a settings change needs no reset first.
    internal static class OwnCloudOptions
    {
        // The tuning keys (ATMO_TUNING { key = value }, or a matrix row atmo.<key>) of the cost controls of the march (T01c). The values
        // are the compiled defaults of LayerFrame.
        internal const string TuneCoverageBound = "layers.coverageBound", TuneLightStride = "layers.lightStride", TuneLightCutoff = "layers.lightCutoff",
            TuneEmptyLeap = "layers.emptyLeap", TuneCheapShare = "layers.cheapShare", TuneFogShare = "layers.fogShare";
        // A4 test keys: the player's ceiling march in the game (Apply).
        internal const string TuneMinStepM = "layers.minStepM", TuneEarlyOut = "layers.earlyOut", TuneSunMarchM = "layers.sunMarchM", TuneSunSteps = "layers.sunSteps";

        // layers.costDebug (LayerFrame.CostDebug, 0 to 15, default 0): cost attribution only, the picture is wrong while it is set. Bit 1 skips the light march, bit 2 reads no shape
        // noise, bit 4 no erosion, bit 8 the light march crosses the sample's own layer alone; the K-T2 perf variants nolight, noshape, noerode and lightown set it.
        internal const string TuneCostDebug = "layers.costDebug";

        // E40 C1: layers.emptyReach (LayerFrame.EmptyReach, default 1): the march leaps over the grid points that lie within the empty reach of an empty sample (EmptyReachMaps). The
        // density reads that are left are the same ones, so the picture does not change; 0 is the march as it was.
        internal const string TuneEmptyReach = "layers.emptyReach";
        internal const bool EmptyReachDefault = true;

        // E40 C2: layers.leapScale (LayerFrame.LeapScales; the longest leap over empty grid points in the layer's noise scales, 0 = off).
        internal const string TuneLeapScale = "layers.leapScale";
        // 1: the largest leap whose picture is the one without it (player series e40-c1-sheet, error against the converged picture: 1.0 the same to four digits in all eight views, 2.0 +0.003 to +0.006,
        // 4.0 +0.02 to +0.05); the pass costs 18 to 35 percent less at the clock's views (series e40-c1-cost). layers.leapScale 0 is the march as it was.
        internal const float LeapScaleDefault = 1f;

        // E40 grain: the multiplier of the most grid points of a span (LayerFrame.MaxSteps); 1: the step quality's count.
        internal const string TuneGridFine = "layers.gridFine";
        internal const float GridFineDefault = 1f;
        // E40 grain: the weight of a new frame in the history of the half-size march (ReconSettings.Accumulate); 0: no history (the pass before the package).
        // Measured at 0.2: against no history the temporal noise of a pixel falls to 0.18 (ground) and 0.27 (above) of it in the game (K-T2d start 54, rows
        // grain_tstd of g0 and gacc) and to 0.22 in the player, the error against the converged picture falls under a still camera, a turn of 30 and 180 degrees a
        // second and a fly-by of 300 and 2400 m/s (series e40-gn-temporal), and the pass costs 0.02 ms more at 1707 x 960 (series e40-gn-cost). It is the default
        // (the owner delegated the choice): level 1 keeps the reprojected, clipped history and the pass reads "reconstruct"; recon.accumulate 0 is the former plain pass.
        // The finer grid (layers.gridFine, layers.emptyLeap) stays off until its cost with all layers is measured in the game.
        internal const string TuneAccumulate = "recon.accumulate";
        // CLOUDR3 (P7): recon.catmull 1 reads the reconstruction's history with a Catmull-Rom filter instead of the bilinear one (default 1; 0: bilinear; measured +0.00 ms at 1080p).
        internal const string TuneCatmull = "recon.catmull";
        internal const bool CatmullDefault = true;
        // recon.trueDistance 1: the block reconstruction moves its history by the distance the march measured for each pixel's cloud instead of by the layers' middle spheres
        // (ReconSettings.TrueDistance; default 0: the middle spheres).
        internal const string TuneTrueDistance = "recon.trueDistance";
        internal const bool TrueDistanceDefault = false;
        // recon.age 1: the block reconstruction counts the marched samples each pixel's history holds; the n-th sample enters at max(the fixed weight, 1 / n) and a pixel
        // without one shows the current upsample (ReconSettings.Age; default 0: the fixed weights).
        internal const string TuneAge = "recon.age";
        internal const bool AgeDefault = false;
        // recon.distanceReject X (with recon.trueDistance): the history is dropped where the distance it stored is more than X octaves from the frame's (ReconSettings.DistanceReject;
        // default 0: off).
        internal const string TuneDistanceReject = "recon.distanceReject";
        internal const float DistanceRejectDefault = 0f;
        // recon.clipSigma X: the width of the history's colour clip in standard deviations of the new samples around the pixel (ReconSettings.ClipSigma; default 1, 999: none).
        internal const string TuneClipSigma = "recon.clipSigma";
        internal const float ClipSigmaDefault = 1f;   // ReconSettings.ClipSigma's own default (the T05 sweep's pick)
        // recon.sceneDepth 1: the block reconstruction takes the scene's depth into its upsample, its clip box and its history test (ReconSettings.SceneDepth). On by
        // default (owner 2026-11-21): without it the history carried cloud and sky over thin geometry in a turn, a halo round the craft and the tower (K-T2
        // perfNpic2560dlssqturnA, 2026-11-20: clean with it, 22.0 -> 22.1 fps). 0: the reconstruction without the scene's depth, as before.
        internal const string TuneSceneDepth = "recon.sceneDepth";
        internal const bool SceneDepthDefault = true;
        // recon.marchTriangle 1: the reconstructor draws the march as one procedural triangle instead of the Blit's quad (ReconSettings.ProcMarch): the same target bit for
        // bit, 5-7 % less march time in the player, 0.3-0.6 fps in the game (2026-11-20). Default 0.
        internal const string TuneMarchTriangle = "recon.marchTriangle";
        internal const bool MarchTriangleDefault = false;
        internal const float AccumulateDefault = 0.2f;

        // E40 MVCLOUD: recon.layerMotion (default 1): the cloud's place in the previous frame, for the reconstruction's history and for the motion vector the upscalers read, is taken on
        // every layer's middle sphere and blended by the layers' share of the pixel (LayerMotionModel); 0 takes the first layer's sphere for every pixel (the history as it was, and the
        // motion vector of the first layer's sphere). cloud.motionVectors (default 1): the pass registers its motion vector handler with the host (Hooks.RegisterMotionVectors), which
        // runs only while an upscaler or frame generation reads the vectors; 0 leaves the sky's rotation-only vector over the clouds, as before. cloud.mvAudit (default 0): the pass reads
        // back a few pixels of its vectors and logs their error against the model (CloudMotionAudit).
        internal const string TuneLayerMotion = "recon.layerMotion", TuneMotionVectors = "cloud.motionVectors", TuneMotionAudit = "cloud.mvAudit";
        internal const bool LayerMotionDefault = true, MotionVectorsDefault = true, MotionAuditDefault = false;

        // E40 ground cost (package L2COST): the size of the history path's march target, so that a march below half size laid up over several frames is a variant of the
        // game start and not only of the player. recon.resolution is the march target's side over the full target's (0.1 to 1; default ReconResolutionDefault);
        // it acts wherever the reconstructor runs in its mode Low: the history path of level 1 (recon.accumulate above 0) and the mode setting Low. (The key recon.jitter, the
        // 16-phase Halton offset of the march rays, is gone: it was never on and the static pictures were worse with it; ReconSettings.Jitter stays for the temporal probe.)
        internal const string TuneReconResolution = "recon.resolution";
        // The owner's look (2026-11-19): the march at the camera's full size, laid up by the rotation (cloudRotation). recon.resolution 0.33 is the third-size march with
        // its history (E40 C1, series e40-c1-sheet and e40-c1-cost), whose loss the owner saw (2026-11-18).
        internal const float ReconResolutionDefault = 1f;
        // The owner's look (2026-11-19, made the default): e40puff k4_stride's march (shortest step 6 m, no step growth, 8 taps over a 12 km march towards the sun), at
        // the full size (ReconResolutionDefault) with the rotation (cloudRotation on). Game, K-T2 perfN2560dlssqceil (A4500 2560x1440 DLSS Q): 21.4 fps at the ground.
        // Since 2026-11-23 the look is carried by the settings' levels (the owner: the quality settings act per graphics profile): the step quality's shortest step
        // (LayerSwitches.MinStepHighM 6 m from the High tier up, MinStepMediumM 12 m below it: TierRow.CloudStepQuality), the light quality's taps (High 8,
        // CloudLightSettings.SunTapsHigh) and cloudShortSunMarch (off: LayerFrame.SunMarchLongM 12 km). The keys layers.minStepM, layers.sunMarchM and layers.sunSteps
        // replace them only when set above 0 (a probe's or the viewer's hand).

        // The defaults in force of the keys the fast cloud path changes (CloudFastPath): the readers below and the viewer's registry take the same property, so the viewer shows
        // what runs on the tier in force.
        internal static float SpanGrowthDefaultPerM { get { return CloudFastPath.Pick(CloudFastPath.SpanGrowthPerM, 0f); } }
        internal static float ShadowResolutionDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.ShadowResolution, ShadowResolutionDefault); } }
        internal static float ReconRotateDefault(float full) { return CloudFastPath.Pick(CloudFastPath.ReconRotate, full); }
        internal static float TrueDistanceDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.TrueDistance, TrueDistanceDefault) ? 1f : 0f; } }
        internal static float AgeDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.Age, AgeDefault) ? 1f : 0f; } }
        internal static float MarchTriangleDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.MarchTriangle, MarchTriangleDefault) ? 1f : 0f; } }
        internal static float DistanceRejectDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.DistanceReject, DistanceRejectDefault); } }
        internal static float ClipSigmaDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.ClipSigma, ClipSigmaDefault); } }
        // recon.rotate N (2 to 4; default 0, off): with the march at full size (recon.resolution 1) and a history (recon.accumulate above 0), each frame marches one pixel of every N x N
        // block, at full size, and blends it into that pixel's history with the accumulation weight; the other pixels keep their reprojected history (Catmull-Rom) alone. The owner judged
        // N = 2 equal to the full march in the weather player still, turning and flying (2026-11-19, e40puff rot2ac against k4_stride); the march costs a third (33 against 97 ms).
        internal const string TuneReconRotate = "recon.rotate";
        // recon.hostJitter (ReconSettings.HostJitter): the march's rays carry the upscaler's jitter, so that each ray is the one the scene's depth texel of its pixel was
        // rendered for (the depth test at the terrain's silhouette); the history's reprojection and the motion vectors keep the unjittered camera. Without an upscaler's jitter
        // the pass is the same either way.
        internal const string TuneHostJitter = "recon.hostJitter";
        internal const bool HostJitterDefault = false;
        // layers.costCutM (LayerFrame.CostCutM, default 0, off): cost attribution only, the march ends at this distance in m; the plain run's time minus the cut run's is what
        // the clouds beyond the cut cost (the 48 fps plan 2026-11-19: whether impostors for far clouds can pay). The K-T2 perf variants cut5, cut10, cut20 set it.
        internal const string TuneCostCutM = "layers.costCutM";

        // layers.spanGrowthPerM (LayerFrame.SpanGrowthPerM, default 0, off): a span's points by distance, the growth of the step per m of distance from the camera. With
        // layers.maxSteps it sets how many points a long span takes and where they lie.
        internal const string TuneSpanGrowthPerM = "layers.spanGrowthPerM";

        // layers.spanGrowthFromM (LayerFrame.SpanGrowthFromM, default 0): the distance from the camera beyond which layers.spanGrowthPerM grows the step.
        internal const string TuneSpanGrowthFromM = "layers.spanGrowthFromM";

        // layers.ditherSequence (LayerFrame.DitherSequence): 1 a per-pixel white hash offset, 2 the interleaved gradient noise, 3 (the default, E40 F2) the 16 x 16 blue-noise tile
        // (DitherMatrix), all advanced by the golden ratio per frame. 2 has the lower temporal noise than 1 (rim 0.0023 against 0.0032 at the soft 0.5, player series e40-gs3) but
        // a regular screen-fixed pattern (a few percent of the residual's power in single spatial lines); 3 has the temporal noise of 2 and a spectrum with no line
        // (the strongest line 0.4 / 0.9 percent of the power against 4.2 / 3.5, series e40-f2-fix) and no power below 0.15 cycles per pixel in its tile.
        internal const string TuneDitherSequence = "layers.ditherSequence";
        // M2c (the low decks, 2026-11-23): cloud.undulatus draws the sheet types' undulations (variety undulatus, CloudTypeDef.UndulatusAmount) with this share of their depth
        // (0, the default: off, the deck of before, until the owner says yes on the pictures); cloud.undulatusWavelengthShare is the wavelength as a share of the layer's depth.
        internal const string TuneUndulatus = "cloud.undulatus", TuneUndulatusWavelengthShare = "cloud.undulatusWavelengthShare";
        internal const float UndulatusDefault = 0f, UndulatusWavelengthShareDefault = 1f;
        internal const int DitherSequenceDefault = 3;
        // The per-pixel pattern of the step offset is read on the march target's own pixel grid (E40 F2).

        // E40 C2: light.moonMinShare: an extra light (moon, planet shine) whose luminance is below this share of the sun's is not marched to and adds nothing (0: every light above zero is
        // marched, the pass as it was). Every cloud sample marched to each lit moon afresh (the moon's taps through every layer, no reuse), and the player's cost series measured that at +21 to +28 percent
        // of the pass for a moon at 3 percent of the sun, which the game has in any daytime sky with a moon (RtReflectors.CloudLight gives light for any body in the sky). A moon of a few
        // hundredths of the sun changes a cloud's brightness by less than that share; at dusk the sun's luminance falls, the share rises, and the moon comes in.
        internal const string TuneMoonMinShare = "light.moonMinShare";
        // 0.03: a moon at 3 percent of the sun changes the error against the picture without it by 0.0001 to 0.0005 (luma, series e40-c1-sheet case mn300); the full Mun is 2.7 percent at best.
        internal const float MoonMinShareDefault = 0.03f;

        // Whether a light of that luminance is too faint beside a sun of that luminance to be marched to (share: light.moonMinShare).
        internal static bool MoonTooFaint(float moonLuma, float sunLuma, float share)
        {
            return share > 0f && moonLuma < share * Mathf.Max(sunLuma, 1e-4f);
        }

        // The tuning keys of the light's look constants (T03); the compiled defaults are the fields of CloudLightSettings.
        internal const string TuneOctaveA = "light.octaveA", TuneOctaveB = "light.octaveB", TuneOctaveC = "light.octaveC",
            TuneMsBrightness = "light.msBrightness", TuneDiffusion = "light.diffusion", TuneDiffusionTau = "light.diffusionTau",
            TuneMsIsotropy = "light.msIsotropy", TuneAmbientOcclusion = "light.ambientOcclusion", TuneAmbientGain = "light.ambientGain",
            TuneSkyTint = "light.skyTint", TuneAmbientFloor = "light.ambientFloor", TuneAmbientColumnTop = "light.ambientColumnTop";
        // E40 LM2: the cell diffusion's numbers (CloudLightSettings.Cell*); the per-layer cell width is the cfg key lightCellWidth.
        internal const string TuneCellGain = "light.cellGain", TuneCellBeta = "light.cellBeta", TuneCellExtrapolation = "light.cellExtrapolation", TuneCellSide = "light.cellSide",
            TuneCellOctaves = "light.cellOctaves", TuneCellIncidencePower = "light.cellIncidencePower", TuneCellIncidenceRef = "light.cellIncidenceRef",
            TuneCellIncidenceEps = "light.cellIncidenceEps";

        private static readonly CloudLightSettings Compiled = new CloudLightSettings();
        private static readonly LayerFrame CompiledFrame = new LayerFrame();

        // One delegate for the process: a method group made at each call is a new object each time (about eight Apply calls a frame).
        private static readonly Func<string, double> LiveNumber = AtmospheresSettings.Number;

        private static Func<string, double> Live
        {
            get { return LiveNumber; }
        }

        // The same delegate for callers outside this class that pass the live numbers on every frame.
        internal static Func<string, double> LiveSettings
        {
            get { return LiveNumber; }
        }

        private static bool On(Func<string, double> v, string key)
        {
            return v(key) != 0.0;
        }

        private static CloudDetailLevel Level(Func<string, double> v, string key)
        {
            return (CloudDetailLevel)Math.Max(0, Math.Min(2, (int)v(key)));
        }

        // Whether the own cloud system draws for a body: the switch is on and the source in force is Ours (LayerSwitches.Active).
        internal static bool Active(Func<string, double> v, CloudSourceKind source)
        {
            return LayerSwitches.Active(On(v, AtmospheresSettings.Keys.OwnClouds), source);
        }

        internal static bool Active(CloudSourceKind source)
        {
            return Active(Live, source);
        }

        // The layers a pass draws for a stack of that many layers.
        internal static int LayersToDraw(Func<string, double> v, int stackCount)
        {
            return LayerSwitches.LayersToDraw(stackCount, (int)v(AtmospheresSettings.Keys.LayerCap));
        }

        // T02: the shape model's switches. SphereScale and Seed are per body and stay as the caller set them.
        internal static void Apply(NoiseSwitches s, Func<string, double> v)
        {
            s.Enabled = On(v, AtmospheresSettings.Keys.CloudNoise);
            s.Untile = On(v, AtmospheresSettings.Keys.CloudNoiseUntile);
            s.Curl = On(v, AtmospheresSettings.Keys.CloudNoiseCurl);
            s.DistanceLod = On(v, AtmospheresSettings.Keys.CloudNoiseDistanceLod);
            s.FineDetail = On(v, AtmospheresSettings.Keys.CloudFineDetail);
            s.SizeLevel = (int)Level(v, AtmospheresSettings.Keys.CloudNoiseTier);
        }

        internal static void Apply(NoiseSwitches s)
        {
            Apply(s, Live);
        }

        // T13: the four switches of the height variation; the numbers are the body's (CloudHeightRules, HeightTuning.TakeNumbers) and stay as the caller set them.
        internal static void Apply(HeightTuning t, Func<string, double> v)
        {
            t.BaseVariation = On(v, AtmospheresSettings.Keys.CloudBaseVariation);
            t.BasePhysics = On(v, AtmospheresSettings.Keys.CloudBasePhysics);
            t.TopFromUpdraft = On(v, AtmospheresSettings.Keys.CloudTopFromUpdraft);
            t.BaseRagged = On(v, AtmospheresSettings.Keys.BaseRagged);
        }

        internal static void Apply(HeightTuning t)
        {
            Apply(t, Live);
        }

        // The tuning keys of the culling (T16); the compiled values are the fields of CullSettings.
        internal const string TuneFootScale = "cull.footScale", TuneCapKeep = "cull.capKeep";

        private static readonly CullSettings CompiledCull = new CullSettings();

        // T16: the switches of the visibility culling; the numbers are tuning keys with the compiled values of the struct.
        internal static void Apply(CullSettings c, Func<string, double> v)
        {
            c.SkipEmpty = On(v, AtmospheresSettings.Keys.CloudSkipEmpty);
            c.CapDistanceM = Math.Max(0, Math.Min(400, (int)v(AtmospheresSettings.Keys.CloudLayerCapDistance))) * 1000f;
            c.FootScale = Mathf.Clamp(Tuning.Get(TuneFootScale, CompiledCull.FootScale), 0f, 8f);
            c.CapKeepSpans = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneCapKeep, CompiledCull.CapKeepSpans)), 1, 8);
        }

        // T01, T01c, T05: the march of a frame.
        internal static void Apply(LayerFrame f, Func<string, double> v)
        {
            int maxSteps, sunSteps;
            float minStep;
            LayerSwitches.StepQuality((int)Level(v, AtmospheresSettings.Keys.LayerStepQuality), out maxSteps, out sunSteps, out minStep);
            // E40 grain: layers.gridFine N (1 to 8; default 1) multiplies the most grid points of a span (up to the shader's 256); with layers.emptyLeap N the leap over
            // empty samples is as long as the product's grid step at most, so the search is N times finer where the clouds are at about the density reads of before.
            // E40 grain against TVC: the multiplier may be below 1 (down to 0.25).
            // E40 soft density: cloudSamples (24 to 256 grid points, relative to the Medium step quality's 48) scales the count of every step quality by the same share; the
            // owner's softness is read into CloudSoftDensity once a frame (the layers' uploaded numbers follow it).
            maxSteps = CloudSoftDensity.ScaleSamples(maxSteps, (int)v(AtmospheresSettings.Keys.CloudSamples));
            CloudSoftDensity.Softness = v(AtmospheresSettings.Keys.CloudSoftness);
            f.MaxSteps = Math.Max(4, Math.Min(256, Mathf.RoundToInt(maxSteps * Mathf.Clamp(Tuning.Get(TuneGridFine, GridFineDefault), 0.25f, 8f))));
            f.SunSteps = sunSteps;
            f.MinStepM = minStep;
            f.TransmittanceGrowth = (float)v(AtmospheresSettings.Keys.LayerStepGrowth);
            f.SunMarchM = On(v, AtmospheresSettings.Keys.CloudShortSunMarch) ? LayerFrame.SunMarchShortM : LayerFrame.SunMarchLongM;
            // The shortest step and the march towards the sun are the settings' (the step quality's level, cloudShortSunMarch); layers.minStepM and layers.sunMarchM above 0 replace
            // them (default 0: the settings'); layers.earlyOut above 0 replaces the frame's early out.
            float minStepKey = Tuning.Get(TuneMinStepM, 0f);
            if (minStepKey > 0f) f.MinStepM = minStepKey;
            float earlyKey = Tuning.Get(TuneEarlyOut, 0f);
            if (earlyKey > 0f) f.EarlyOut = earlyKey;
            float sunMarchKey = Tuning.Get(TuneSunMarchM, 0f);
            if (sunMarchKey > 0f) f.SunMarchM = sunMarchKey;
            // The cost controls: tuning keys with the compiled values of the frame.
            f.CoverageBound = Tuning.Flag(TuneCoverageBound, CompiledFrame.CoverageBound);
            f.EmptyReach = Tuning.Flag(TuneEmptyReach, EmptyReachDefault);
            f.LeapScales = Mathf.Max(0f, Tuning.Get(TuneLeapScale, LeapScaleDefault));
            f.LightStride = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneLightStride, CompiledFrame.LightStride)), 1, 8);
            f.CostCutM = Mathf.Max(0f, Tuning.Get(TuneCostCutM, 0f));
            f.SpanGrowthPerM = Mathf.Max(0f, Tuning.Get(TuneSpanGrowthPerM, SpanGrowthDefaultPerM));
            f.SpanGrowthFromM = Mathf.Max(0f, Tuning.Get(TuneSpanGrowthFromM, 0f));
            f.LightCutoff = Mathf.Max(0f, Tuning.Get(TuneLightCutoff, CompiledFrame.LightCutoff));
            f.EmptyLeap = Mathf.Max(1f, Tuning.Get(TuneEmptyLeap, CompiledFrame.EmptyLeap));
            f.CheapShare = Mathf.Clamp01(Tuning.Get(TuneCheapShare, CompiledFrame.CheapShare));
            f.FogShare = Mathf.Clamp01(Tuning.Get(TuneFogShare, CompiledFrame.FogShare));
            f.CostDebug = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneCostDebug, 0f)), 0, 15);
            f.DitherSequence = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneDitherSequence, DitherSequenceDefault)), 1, 3);
            f.Undulatus = Mathf.Clamp01(Tuning.Get(TuneUndulatus, UndulatusDefault));
            f.UndulatusWavelengthShare = Mathf.Clamp(Tuning.Get(TuneUndulatusWavelengthShare, UndulatusWavelengthShareDefault), 0.05f, 20f);
            Apply(f.Cull, v);
        }

        internal static void Apply(LayerFrame f)
        {
            Apply(f, Live);
        }

        // T03: the light. The quality level sets the taps and the octaves, cloudLightOctaves caps the octaves, the look constants are tuning keys.
        // The extra lights are limited by LimitMoonLights after the caller has filled them.
        internal static void Apply(CloudLightSettings s, Func<string, double> v)
        {
            CloudLightSettings q = CloudLightSettings.ForQuality((int)Level(v, AtmospheresSettings.Keys.CloudLightQuality));
            s.SunStepsOverride = q.SunStepsOverride;
            // The taps towards the sun are the light quality level's; layers.sunSteps above 0 replaces them (default 0).
            int sunSteps = Mathf.RoundToInt(Tuning.Get(TuneSunSteps, 0f));
            if (sunSteps > 0) s.SunStepsOverride = Math.Min(sunSteps, 64);
            s.MoonSteps = q.MoonSteps;
            s.UpSteps = q.UpSteps;
            s.Octaves = Math.Max(1, Math.Min(q.Octaves, (int)v(AtmospheresSettings.Keys.CloudLightOctaves)));
            s.PowderStrength = (float)v(AtmospheresSettings.Keys.CloudPowder);
            s.SunAirHeightM = On(v, AtmospheresSettings.Keys.CloudSunTint) ? Compiled.SunAirHeightM : 0f;
            s.AmbientOcclusionTaps = On(v, AtmospheresSettings.Keys.CloudAmbientTaps);
            s.SunAirWhiteBalance = On(v, AtmospheresSettings.Keys.CloudSunWhiteBalance);
            s.DiffusionDecay = Math.Max(0f, (float)v(AtmospheresSettings.Keys.CloudDiffusionDecay));
            s.UpReuse = (int)v(AtmospheresSettings.Keys.CloudAmbientReuse);
            if (s.Cache != null) s.Cache.Enabled = On(v, AtmospheresSettings.Keys.CloudLightCache);
            s.OctaveA = Tuning.Get(TuneOctaveA, Compiled.OctaveA);
            s.OctaveB = Tuning.Get(TuneOctaveB, Compiled.OctaveB);
            s.OctaveC = Tuning.Get(TuneOctaveC, Compiled.OctaveC);
            s.MsBrightness = Tuning.Get(TuneMsBrightness, Compiled.MsBrightness);
            s.Diffusion = Tuning.Get(TuneDiffusion, Compiled.Diffusion);
            s.DiffusionTau = Tuning.Get(TuneDiffusionTau, Compiled.DiffusionTau);
            s.MsIsotropy = Tuning.Get(TuneMsIsotropy, Compiled.MsIsotropy);
            s.AmbientOcclusion = Tuning.Get(TuneAmbientOcclusion, Compiled.AmbientOcclusion);
            s.AmbientGain = Tuning.Get(TuneAmbientGain, Compiled.AmbientGain);
            s.SkyTint = Tuning.Get(TuneSkyTint, Compiled.SkyTint);
            // The floor is read through EffectiveFloor, which never lets it fall below the parity gate's 3 percent.
            s.AmbientFloor = Tuning.Get(TuneAmbientFloor, Compiled.AmbientFloor);
            // light.ambientColumnTop (default 1): the analytic occlusion's column ends at the cloud type's top; 0: at the layer's outer altitude (the earlier behaviour).
            s.AmbientColumnToTypeTop = Tuning.Flag(TuneAmbientColumnTop, Compiled.AmbientColumnToTypeTop);
            s.CellGain = Tuning.Get(TuneCellGain, Compiled.CellGain);
            s.CellBeta = Tuning.Get(TuneCellBeta, Compiled.CellBeta);
            s.CellExtrapolation = Tuning.Get(TuneCellExtrapolation, Compiled.CellExtrapolation);
            s.CellSide = Tuning.Get(TuneCellSide, Compiled.CellSide);
            s.CellOctaves = Tuning.Get(TuneCellOctaves, Compiled.CellOctaves);
            s.CellIncidencePower = Tuning.Get(TuneCellIncidencePower, Compiled.CellIncidencePower);
            s.CellIncidenceRef = Tuning.Get(TuneCellIncidenceRef, Compiled.CellIncidenceRef);
            s.CellIncidenceEps = Tuning.Get(TuneCellIncidenceEps, Compiled.CellIncidenceEps);
            LimitMoonLights(s, v);
        }

        internal static void Apply(CloudLightSettings s)
        {
            Apply(s, Live);
        }

        // The moons and planets that light the clouds: the lights above the setting's count are switched off (a zero colour is skipped by the shader).
        // Apply(CloudLightSettings) calls it for the lights as they stand; a caller that fills s.Lights afterwards calls it again after the fill.
        internal static void LimitMoonLights(CloudLightSettings s, Func<string, double> v)
        {
            int count = Math.Max(0, Math.Min(CloudLightSettings.MaxLights, (int)v(AtmospheresSettings.Keys.CloudMoonLights)));
            for (int i = count; i < CloudLightSettings.MaxLights; i++) s.Lights[i].Colour = new Color(0f, 0f, 0f, 0f);
        }

        // T04: how the shadow's steps scale with a layer's interval (CloudShadowStepPolicy): the tuning key shadow.stepPolicy, else the compiled default.
        internal static CloudShadowSteps ShadowStepPolicy
        {
            get { return CloudShadowStepPolicy.FromNumber(Tuning.Get(CloudShadowStepPolicy.TuneKey, (float)CloudShadowStepPolicy.Default)); }
        }

        // E40 FL-FIX: whether the cloud shadow (shader and CPU twin) reads the layer pass's shape noise: the tuning key shadow.shapeNoise, else the compiled default.
        internal static bool ShadowShapeNoise
        {
            get { return Tuning.Get(CloudShadowShapeNoise.TuneKey, CloudShadowShapeNoise.Default ? 1f : 0f) >= 0.5f; }
        }

        // OPT4: shadow.resolution, the divisor of the size the cloud shadow is marched at (CloudShadowFrame.Divisor; 2 half size, the default, up to 4).
        internal const string TuneShadowResolution = "shadow.resolution";
        internal const float ShadowResolutionDefault = 2f;

        // OPT5: shadow.taps, the directions across the sun's disc of the cloud shadow (0: the quality level's own number, CloudShadowSwitches.Quality; 1 to 8 replace it).
        internal const string TuneShadowTaps = "shadow.taps";
        internal const float ShadowTapsDefault = 0f;

        // T04: the cloud shadow's numbers of a frame.
        internal static void Apply(CloudShadowFrame f, Func<string, double> v)
        {
            f.Divisor = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneShadowResolution, ShadowResolutionDefaultNow)), 2, 4);
            int steps, taps;
            CloudShadowSwitches.Quality((int)Level(v, AtmospheresSettings.Keys.CloudShadowQuality), out steps, out taps);
            int tapsKey = Mathf.Clamp(Mathf.RoundToInt(Tuning.Get(TuneShadowTaps, ShadowTapsDefault)), 0, 8);
            if (tapsKey > 0) taps = tapsKey;
            f.StepsPerSpan = steps;
            f.StepPolicy = ShadowStepPolicy;
            f.ShapeNoise = ShadowShapeNoise;
            f.DiscTaps = taps;
            f.Floor = Mathf.Clamp01((float)v(AtmospheresSettings.Keys.CloudShadowFloor));
        }

        // Whether the cloud shadow is drawn for a body with that many own layers (CloudShadowSwitches.Active).
        internal static bool ShadowActive(Func<string, double> v, CloudSourceKind source, int layerCount)
        {
            return CloudShadowSwitches.Active(On(v, AtmospheresSettings.Keys.CloudShadows), Active(v, source), layerCount);
        }

        // T06: the far view's switches. The air's coefficients, the colour of the in-scattered light and the lod range are not settings: the integration
        // (T09) supplies them from the body and the sky.
        internal static void Apply(FarViewSettings s, Func<string, double> v)
        {
            // The look knobs (CloudLookTuning): cloud.farHaze forces the aerial perspective off / on / automatic (default -1: the setting decides), cloud.hazeStrength scales it (default 1).
            bool haze, auto;
            CloudHazePlan.Resolve(CloudLookTuning.FarHazeForce, On(v, AtmospheresSettings.Keys.CloudFarHaze), out haze, out auto);
            s.Haze = haze;
            s.HazeAuto = auto;
            s.HazeStrength = CloudLookTuning.HazeStrengthScale;
            s.ShapeMean = On(v, AtmospheresSettings.Keys.CloudFarShapeMean);
            s.MinSamples = Math.Max(0, Math.Min(16, (int)v(AtmospheresSettings.Keys.CloudFarMinSamples)));
            s.Policy = On(v, AtmospheresSettings.Keys.CloudFarPolicy);
        }

        internal static void Apply(FarViewSettings s)
        {
            Apply(s, Live);
        }

        // Whether the map view fades the cloud shell with the camera's distance (OwnShell.MapFadeEnabled).
        internal static bool MapFade(Func<string, double> v)
        {
            return On(v, AtmospheresSettings.Keys.CloudFarMapFade);
        }

        // T05, T14: the reconstruction. The slider cloudResolution is the level: 1 marches the half-size target of the plain pass (the default, ReconMode.Full; LayerFrame.Resolution), 2 to 4 march one
        // pixel of every 2 x 2, 3 x 3 or 4 x 4 block (ReconMode.Block). The mode setting's Low (prototype B, a smaller march target) overrides the slider.
        internal static void Apply(ReconSettings s, Func<string, double> v)
        {
            int level = ReconPattern.ClampLevel((int)v(AtmospheresSettings.Keys.CloudResolution));
            bool low = (ReconMode)Math.Max(0, Math.Min(2, (int)v(AtmospheresSettings.Keys.CloudReconMode))) == ReconMode.Low;
            s.Mode = low ? ReconMode.Low : (level > 1 ? ReconMode.Block : ReconMode.Full);
            s.Block = Math.Max(2, level);
            s.LayerMotion = Tuning.Flag(TuneLayerMotion, LayerMotionDefault);
            s.HostJitter = Tuning.Flag(TuneHostJitter, HostJitterDefault);
            s.CatmullHistory = Tuning.Flag(TuneCatmull, CatmullDefault);
            s.TrueDistance = Tuning.Flag(TuneTrueDistance, TrueDistanceDefaultNow > 0.5f);
            s.Age = Tuning.Flag(TuneAge, AgeDefaultNow > 0.5f);
            s.ProcMarch = Tuning.Flag(TuneMarchTriangle, MarchTriangleDefaultNow > 0.5f);
            s.SceneDepth = Tuning.Flag(TuneSceneDepth, SceneDepthDefault);
            s.DistanceReject = Mathf.Max(0f, Tuning.Get(TuneDistanceReject, DistanceRejectDefaultNow));
            s.ClipSigma = Mathf.Max(0f, Tuning.Get(TuneClipSigma, ClipSigmaDefaultNow));
            // E40 grain: recon.accumulate W (0 to 1; default 0.2, 0 = off) gives the half-size march of level 1 a history of its own size: each frame enters it with the
            // weight W. It acts at level 1 only (the block levels have their own history) and runs the pass through the reconstructor's mode Low.
            float accumulate = Mathf.Clamp01(Tuning.Get(TuneAccumulate, AccumulateDefault));
            s.Accumulate = 0f;
            if (accumulate > 0f && s.Mode == ReconMode.Full)
            {
                s.Mode = ReconMode.Low;
                s.Resolution = Mathf.Clamp(Tuning.Get(TuneReconResolution, ReconResolutionDefault), 0.1f, 1f);
                s.Accumulate = accumulate;
            }
            else if (low)
            {
                s.Resolution = Mathf.Clamp(Tuning.Get(TuneReconResolution, ReconResolutionDefault), 0.1f, 1f);
                s.Accumulate = accumulate;
            }
            // recon.rotate: the full-size march with a history as a rotation over N x N blocks (the block mode with the history's own weights).
            var defaults = new ReconSettings();
            s.FreshWeight = defaults.FreshWeight;
            s.StaleWeight = defaults.StaleWeight;
            s.Rotation = false;
            // The switch cloudRotation is 2 x 2; the tuning key recon.rotate, where set, names the block side.
            // The fast cloud path (CloudFastPath, the tier's) takes 4 x 4 when the key is not set.
            int rotate = (int)Tuning.Get(TuneReconRotate, ReconRotateDefault(v(AtmospheresSettings.Keys.CloudRotation) > 0.5 ? 2f : 0f));
            // Only the history path of level 1: the mode setting Low (prototype B) stays as the player chose it.
            if (rotate >= 2 && !low && s.Mode == ReconMode.Low && s.Accumulate > 0f && s.Resolution > 0.99f)
            {
                s.Rotation = true;
                s.Mode = ReconMode.Block;
                s.Block = Math.Min(rotate, 4);
                s.FreshWeight = s.Accumulate;
                s.StaleWeight = 0f;
                s.CatmullHistory = true;
            }
        }

        internal static void Apply(ReconSettings s)
        {
            Apply(s, Live);
        }
    }
}
