using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2d, the grain start (-atmoLook G; E40 grain, .local/plans/atmospheres/E40-grain-result.md): the per-pixel speckle of the own clouds in the game, per march and
    // reconstruction variant. At the low cover, in the ground view and the view from above: the baseline frame (no layer drawn), then for every variant of GrainVariants three
    // frames read one after the other (a, b, c), written at FULL size (the half-size pictures of the other starts average the grain away).
    // Rows T09d.<view>.low.<variant>.grain_*, measured on the raw target values (display units) over the pixels the clouds changed against the baseline by GrainCloudChange in a
    // channel in any of the three frames:
    //   grain_n         the pixels of that mask
    //   grain_tstd      temporal noise: the standard deviation of a pixel's luma over the three frames, mean over the mask
    //   grain_hf        spatial grain: mean |3 x 3 Laplacian| of the luma of frame a over the mask; grain_hf_off the same of the baseline frame over the same mask
    //                   (what the scene behind holds), grain_hfx their difference
    //   grain_frames    the frames the pass drew between the first and the last read (how far apart the three reads were)
    // plus the rows of every frame as in the other starts (<variant>a / b / c .*).
    internal sealed partial class T09Probe
    {
        internal struct GrainVariant
        {
            internal string Tag;
            // Tuning keys forced for the variant, the cloud resolution level (1: the half-size march, 2: the 2 x 2 block mode), and the frames the history is given before the reads.
            internal KeyValuePair<string, float>[] Keys;
            internal int Level, HistoryFrames;
        }

        private static KeyValuePair<string, float> Key(string key, float value)
        {
            return new KeyValuePair<string, float>(key, value);
        }

        // Every variant names the history weight (recon.accumulate), so that the table does not move with the default. g0 is the pass without anything of this package
        // (the product before it). The others add, one at a time and together: the history of the half-size march, the finer grid with the leap (layers.gridFine,
        // layers.emptyLeap), the entry ramp (layers.entryRamp) and level 2 of the resolution slider.
        private static KeyValuePair<string, float>[] GrainKeys(float accumulate, bool leap, bool ramp)
        {
            var keys = new List<KeyValuePair<string, float>> { Key(OwnCloudOptions.TuneAccumulate, accumulate) };
            if (leap)
            {
                keys.Add(Key(OwnCloudOptions.TuneGridFine, 4f));
                keys.Add(Key(OwnCloudOptions.TuneEmptyLeap, 4f));
            }
            if (ramp) keys.Add(Key(OwnCloudOptions.TuneEntryRamp, 4f));
            return keys.ToArray();
        }

        internal static readonly GrainVariant[] GrainVariants =
        {
            new GrainVariant { Tag = "g0", Level = 1, Keys = GrainKeys(0f, false, false) },
            new GrainVariant { Tag = "gacc", Level = 1, HistoryFrames = 60, Keys = GrainKeys(0.2f, false, false) },
            new GrainVariant { Tag = "gleap", Level = 1, Keys = GrainKeys(0f, true, false) },
            new GrainVariant { Tag = "gleapacc", Level = 1, HistoryFrames = 60, Keys = GrainKeys(0.2f, true, false) },
            new GrainVariant { Tag = "gramp", Level = 1, Keys = GrainKeys(0f, true, true) },
            new GrainVariant { Tag = "grampacc", Level = 1, HistoryFrames = 60, Keys = GrainKeys(0.2f, true, true) },
            new GrainVariant { Tag = "gb2", Level = 2, HistoryFrames = 60, Keys = GrainKeys(0f, false, false) },
        };

        // The probe's limit in the grain start: 44 full-size reads, and the variants with the finer grid draw slower.
        internal const double GrainMaxSeconds = 15 * 60;

        // The change of a channel against the baseline above which a pixel belongs to the clouds (display units).
        internal const float GrainCloudChange = 0.05f;

        private static float[] GrainLuma(Color[] raw)
        {
            var l = new float[raw.Length];
            for (int i = 0; i < raw.Length; i++) l[i] = 0.2126f * raw[i].r + 0.7152f * raw[i].g + 0.0722f * raw[i].b;
            return l;
        }

        // The grain rows of three frames against the baseline (see the header). Returns false where the mask is empty.
        internal static bool MeasureGrain(Color[] off, IList<Color[]> frames, int width, int height, out int n, out double tstd, out double hf, out double hfOff)
        {
            n = 0;
            tstd = hf = hfOff = 0.0;
            if (off == null || frames == null || frames.Count < 2) return false;
            int count = width * height;
            var mask = new bool[count];
            foreach (Color[] f in frames)
            {
                if (f == null || f.Length != count || off.Length != count) return false;
                for (int i = 0; i < count; i++)
                {
                    float d = Mathf.Max(Mathf.Abs(f[i].r - off[i].r), Mathf.Max(Mathf.Abs(f[i].g - off[i].g), Mathf.Abs(f[i].b - off[i].b)));
                    if (d >= GrainCloudChange) mask[i] = true;
                }
            }
            var lumas = new List<float[]>();
            foreach (Color[] f in frames) lumas.Add(GrainLuma(f));
            float[] first = lumas[0], baseline = GrainLuma(off);
            for (int y = 1; y < height - 1; y++)
                for (int x = 1; x < width - 1; x++)
                {
                    int i = y * width + x;
                    if (!mask[i]) continue;
                    double mean = 0.0, sq = 0.0;
                    foreach (float[] l in lumas) { mean += l[i]; sq += (double)l[i] * l[i]; }
                    mean /= lumas.Count;
                    tstd += Math.Sqrt(Math.Max(sq / lumas.Count - mean * mean, 0.0));
                    hf += Math.Abs(4.0 * first[i] - first[i - 1] - first[i + 1] - first[i - width] - first[i + width]);
                    hfOff += Math.Abs(4.0 * baseline[i] - baseline[i - 1] - baseline[i + 1] - baseline[i - width] - baseline[i + width]);
                    n++;
                }
            if (n == 0) return false;
            tstd /= n;
            hf /= n;
            hfOff /= n;
            return true;
        }

        private IEnumerable<ProbeWait> DiagLookG(View view, string cover)
        {
            string head = "T09d." + view.Name + "." + cover + ".";
            diagPictureFactor = 1;
            ClearForced();
            SetSetting(AtmospheresSettings.Keys.CloudResolution, 1.0);
            Game.SetLayerMask(NoLayersMask);
            yield return ProbeWait.Frames(DiagSettleFrames);
            foreach (ProbeWait w in DiagTake(view, cover, "off", false, null)) yield return w;
            T09Readout.Reading off = diagReading;
            Color[] offRaw = diagImage != null ? diagImage.Raw : null;
            Game.SetLayerMask(0);
            foreach (GrainVariant v in GrainVariants)
            {
                ClearForced();
                if (v.Keys != null)
                    foreach (KeyValuePair<string, float> k in v.Keys) ForceTuning(k.Key, k.Value);
                SetSetting(AtmospheresSettings.Keys.CloudResolution, (double)v.Level);
                yield return ProbeWait.Frames(DiagSettleFrames + v.HistoryFrames);
                OwnCloudModule own = OwnCloudModule.Current;
                if (own != null) EmitText(head + v.Tag + ".mode", own.Stats.Mode ?? "");
                int drawnFrom = own != null ? own.Stats.FramesDrawn : 0;
                // The cuts of the history during the reads: a history that is dropped every frame removes nothing, whatever the mode row says.
                int cutsFrom = own != null ? own.Stats.Cuts : 0;
                var raws = new List<Color[]>();
                foreach (string letter in new[] { "a", "b", "c" })
                {
                    foreach (ProbeWait w in DiagTake(view, cover, v.Tag + letter, false, off)) yield return w;
                    if (diagReading != null && diagImage != null && diagImage.Raw != null) raws.Add(diagImage.Raw);
                }
                int n = 0;
                double tstd = 0.0, hf = 0.0, hfOff = 0.0;
                bool measured = raws.Count == 3 && MeasureGrain(offRaw, raws, diagImage.Width, diagImage.Height, out n, out tstd, out hf, out hfOff);
                Check("T09d.grain_" + view.Name + "_" + cover + "_" + v.Tag, measured, raws.Count + " frames read; the mask of the clouds against the baseline " + (measured ? "holds pixels" : "is empty or a frame is missing"));
                if (!measured) continue;
                Emit(head + v.Tag + ".grain_n", n);
                Emit(head + v.Tag + ".grain_tstd", tstd);
                Emit(head + v.Tag + ".grain_hf", hf);
                Emit(head + v.Tag + ".grain_hf_off", hfOff);
                Emit(head + v.Tag + ".grain_hfx", hf - hfOff);
                if (own != null) Emit(head + v.Tag + ".grain_frames", own.Stats.FramesDrawn - drawnFrom);
                if (own != null) Emit(head + v.Tag + ".grain_cuts", own.Stats.Cuts - cutsFrom);
                AtmospheresLog.Say("T09d grain " + view.Name + " " + v.Tag + ": tstd " + tstd.ToString("0.0000", CultureInfo.InvariantCulture) + ", hfx "
                                   + (hf - hfOff).ToString("0.0000", CultureInfo.InvariantCulture) + " over " + n + " pixels");
            }
            ClearForced();
            SetSetting(AtmospheresSettings.Keys.CloudResolution, 1.0);
            diagPictureFactor = 2;
            yield return ProbeWait.Frames(DiagSettleFrames);
        }
    }
}
