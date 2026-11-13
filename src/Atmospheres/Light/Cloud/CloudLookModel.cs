using System;
using System.Collections.Generic;
using March = ReDefinition.Atmospheres.CloudCoverOracle.March;

namespace ReDefinition.Atmospheres
{
    // What the cloud-look series of the game (K-T2d starts 51 and 52, T09ProbeLook) need besides the pass: the sets of look knobs the sweeps compare, the expectation of the
    // closed share the CPU oracle gives for a frame (written to the log before the frame is read), and the great-circle walk that reads the layer maps along the view.
    // The sets are the six candidates of the E40 plan (tools/weather_metrics/e40o.py has the same numbers for the player's sweeps).
    internal sealed class LookSet
    {
        internal readonly string Tag, Note;
        internal readonly KeyValuePair<string, float>[] Keys;

        internal LookSet(string tag, string note, params KeyValuePair<string, float>[] keys)
        {
            Tag = tag;
            Note = note;
            Keys = keys;
        }

        // Whether the set changes the type values (they reach the pass through the maps, which slew toward the new bytes, so the frame waits for them).
        internal bool ChangesTypes
        {
            get
            {
                foreach (KeyValuePair<string, float> k in Keys)
                    if (k.Key.StartsWith(CloudLookTuning.TuneTypeFair, StringComparison.Ordinal) || k.Key.StartsWith(CloudLookTuning.TuneTypeOverride, StringComparison.Ordinal)) return true;
                return false;
            }
        }
    }

    // The CPU's view of the stack at the vessel: the layers, their tables, the noise parameters the pass has (offset, seed), and the maps. T09 reads it from the module;
    // a test makes it from fixtures.
    internal sealed class T09LookInput
    {
        internal CloudLayerDef[] Layers;
        internal LayerTypeTable[] Tables;
        internal NoiseLayerParams[] Noise;
        // The layer maps at the vessel (the CPU values) and a function that reads layer l's map and type at a place (degrees); null: the maps at the vessel everywhere.
        internal double[] Cover, Type;
        internal Func<int, double, double, double> CoverAt;
        // The height maps' offsets at the vessel per layer (m): the root offset, the top term and the variation as the stack holds them; null where the stack could not be asked.
        internal float[] RootM, TopM, VariationM;
        internal double BodyRadiusM = 600000.0;
        internal uint Seed = 1;
        internal int SizeLevel = 1;
    }

    // The expectation for one frame: the mean and the spread over the sites of the share of the sky above the horizon whose stack optical depth is at least CloudCoverOracle's CLOSED tau.
    internal struct LookExpectation
    {
        internal double Mean, P10, P90;
        internal int Sites;
        // The optical depth straight up from the camera over the same sites, all layers added (K-T2d start 51 asked for a tau beside the colour-change measure): the mean and the
        // spread over the sites, and the share of the sites whose zenith is closed (tau at least ClosedTau).
        internal double ZenithTauMean, ZenithTauP10, ZenithTauP90, ZenithClosed;
    }

    internal static class CloudLookModel
    {
        // The optical depth of the picture proxy's opacity 0.6 (T09Readout.ClosedChange): the share of the sky at or above it is the "closed share" the rows report.
        internal const double ClosedTau = 0.92;

        // The noise of the sets, relative to the cumulus layer's cfg (Clouds-Kerbin.cfg: amount 1, scale 500 m): the amount 0.7 (the reader's range is 0 to 1; the factor of the former
        // base, 0.55 / 0.35, would give 1.57) and the cells 1.4 times larger, 700 m. CloudLookFixtureDriftTests holds the fixtures to the cfg's numbers; the factors are the sets'.
        internal const float NoiseAmountFactor = 0.7f, NoiseSizeFactor = 1.4f;

        private static readonly KeyValuePair<string, float>[] NoiseKeys =
        {
            new KeyValuePair<string, float>(CloudLookTuning.TuneNoiseAmount + ".Kerbin-cumulus", NoiseAmountFactor),
            new KeyValuePair<string, float>(CloudLookTuning.TuneNoiseSize + ".Kerbin-cumulus", NoiseSizeFactor)
        };

        private static KeyValuePair<string, float> K(string key, float value)
        {
            return new KeyValuePair<string, float>(key, value);
        }

        private static KeyValuePair<string, float>[] Cat(KeyValuePair<string, float>[] a, params KeyValuePair<string, float>[] b)
        {
            var all = new List<KeyValuePair<string, float>>(a);
            all.AddRange(b);
            return all.ToArray();
        }

        // The six candidates. The layer names are Kerbin's (Clouds-Kerbin.cfg); another body's file would give its own sets.
        internal static readonly LookSet[] Sets =
        {
            new LookSet("C0", "current numbers"),
            new LookSet("C1", "fair weather shows Cumulus humilis", K(CloudLookTuning.TuneTypeFair + "Kerbin-cumulus", 0.25f)),
            new LookSet("C2", "noise amount 1 to 0.7, scale 500 to 700 m", NoiseKeys),
            new LookSet("C3", "the erosion also thins the interior (0.6)", K(CloudLookTuning.TuneDensityRemap, 0.6f)),
            new LookSet("C4", "L1 coverage 0.25, softness 0.35, sigma 0.035, noise 0.7 / 700 m", Cat(NoiseKeys, K(CloudLookTuning.TuneLayerShift + "Kerbin-cumulus", -0.10f),
                K(CloudLookTuning.TuneSoftness + ".Kerbin-cumulus", 0.35f / 0.15f), K(CloudLookTuning.TuneSigma + ".Kerbin-cumulus", 0.035f / 0.05f))),
            new LookSet("C5", "C1 + C2 + C3", Cat(NoiseKeys, K(CloudLookTuning.TuneTypeFair + "Kerbin-cumulus", 0.25f), K(CloudLookTuning.TuneDensityRemap, 0.6f)))
        };

        internal static LookSet SetByTag(string tag)
        {
            foreach (LookSet s in Sets)
                if (string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // The sets named in a comma-separated list ("C0,C4,C5"); null or empty: all of them. An unknown tag is skipped.
        internal static List<LookSet> Selected(string list)
        {
            var found = new List<LookSet>();
            if (string.IsNullOrEmpty(list))
            {
                found.AddRange(Sets);
                return found;
            }
            foreach (string part in list.Split(','))
            {
                LookSet s = SetByTag(part.Trim());
                if (s != null && !found.Contains(s)) found.Add(s);
            }
            return found;
        }

        // The point at a great-circle distance and bearing (degrees from north towards east) from a place; degrees.
        internal static void Walk(double latDeg, double lonDeg, double bearingDeg, double distanceM, double radiusM, out double lat2Deg, out double lon2Deg)
        {
            double lat = latDeg * Math.PI / 180.0, lon = lonDeg * Math.PI / 180.0, th = bearingDeg * Math.PI / 180.0, d = distanceM / radiusM;
            double s = Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(th);
            double lat2 = Math.Asin(Math.Max(-1.0, Math.Min(1.0, s)));
            double lon2 = lon + Math.Atan2(Math.Sin(th) * Math.Sin(d) * Math.Cos(lat), Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));
            lat2Deg = lat2 * 180.0 / Math.PI;
            lon2Deg = ((lon2 * 180.0 / Math.PI + 540.0) % 360.0) - 180.0;
        }

        // The expected closed share of a frame at the camera's height, pitch and field of view, for the stack's CPU maps at the vessel (constant over the view) with the knobs as
        // they are NOW (the Effective* getters): the ensemble over sites of the noise, 250 km apart, each a frame of w x h rays. This is what a constant map gives; a frame that
        // differs from it points at the maps along the view or at the pass.
        // onlyLayer: the index of the one layer the frame draws (the layer-alone frames of the look starts); -1: all layers.
        internal static LookExpectation Expect(T09LookInput input, double heightM, double pitchDeg, double fovDeg, int sites = 8, int w = 32, int h = 18, March march = default(March), int onlyLayer = -1)
        {
            if (march.MaxSteps == 0) march = March.Medium;
            NoiseVolumeSet set = Volumes(input.Seed, input.SizeLevel);
            var shares = new List<double>();
            var zenith = new List<double>();
            for (int s = 0; s < sites; s++)
            {
                double[] tau = null;
                double zenithTau = 0.0;
                CloudCoverOracle.Pixels first = null;
                for (int l = 0; l < input.Layers.Length; l++)
                {
                    if (onlyLayer >= 0 && l != onlyLayer) continue;
                    CloudLayerDef layer = input.Layers[l];
                    NoiseLayerParams prm = input.Noise[l];
                    prm.OffsetX += s * 250000.0;
                    prm.OffsetY += s * 130000.0;
                    prm.DensityRemap = CloudLookTuning.DensityRemap;
                    prm.ScaleM = layer.EffectiveNoiseScaleM;
                    // input.Type is what the stack's CPU map holds, i.e. already the layer's own type value (the module's fetch applies the type knobs before the mirror is filled).
                    double typeValue = input.Type[l];
                    CloudCoverOracle.Frame frame = new CloudCoverOracle.Frame
                    {
                        HeightM = heightM, PitchDeg = pitchDeg, FovVDeg = fovDeg, YawDeg = 0.0, BodyRadiusM = input.BodyRadiusM, Width = w, Height = h
                    };
                    CloudCoverOracle.Pixels px = CloudCoverOracle.Render(layer, input.Tables[l], set, prm, frame, CloudCoverOracle.Constant(input.Cover[l], typeValue), layer.EffectiveCoverage, march);
                    zenithTau += CloudCoverOracle.ZenithTau(layer, input.Tables[l], set, prm, heightM, input.BodyRadiusM, CloudCoverOracle.Constant(input.Cover[l], typeValue), layer.EffectiveCoverage, march);
                    if (first == null)
                    {
                        first = px;
                        tau = (double[])px.Tau.Clone();
                    }
                    else
                    {
                        for (int i = 0; i < tau.Length; i++) tau[i] += px.Tau[i];
                    }
                }
                if (first == null) continue;
                int n = 0, hit = 0;
                for (int i = 0; i < tau.Length; i++)
                {
                    if (!first.AboveHorizon[i]) continue;
                    n++;
                    if (tau[i] >= ClosedTau) hit++;
                }
                shares.Add(n == 0 ? 0.0 : hit / (double)n);
                zenith.Add(zenithTau);
            }
            if (shares.Count == 0) return default(LookExpectation);
            shares.Sort();
            zenith.Sort();
            double sum = 0.0, zsum = 0.0;
            int zclosed = 0;
            foreach (double v in shares) sum += v;
            foreach (double v in zenith)
            {
                zsum += v;
                if (v >= ClosedTau) zclosed++;
            }
            return new LookExpectation
            {
                Mean = sum / shares.Count, P10 = shares[(int)Math.Round(0.1 * (shares.Count - 1))], P90 = shares[(int)Math.Round(0.9 * (shares.Count - 1))], Sites = shares.Count,
                ZenithTauMean = zsum / zenith.Count, ZenithTauP10 = zenith[(int)Math.Round(0.1 * (zenith.Count - 1))], ZenithTauP90 = zenith[(int)Math.Round(0.9 * (zenith.Count - 1))],
                ZenithClosed = zclosed / (double)zenith.Count
            };
        }

        private static NoiseVolumeSet cached;
        private static uint cachedSeed;
        private static int cachedLevel = -1;
        private static readonly object CacheGate = new object();

        internal static NoiseVolumeSet Volumes(uint seed, int level)
        {
            lock (CacheGate)
            {
                if (cached == null || cachedSeed != seed || cachedLevel != level)
                {
                    cached = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(level), seed);
                    cachedSeed = seed;
                    cachedLevel = level;
                }
                return cached;
            }
        }
    }
}
