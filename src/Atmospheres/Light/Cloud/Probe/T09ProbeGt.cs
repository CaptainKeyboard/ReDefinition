using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Series K-T2, the starts gt1707 and gttvc1707 (E40 grain against TVC, .local/plans/atmospheres/E40-grain-tvc-result.md): the cost of the own pass in the K-T2 views against
    // TVC with the K-T2 method (timing windows, the GPU profiler's busy time against the off window), and a grain reading that is the same for the own pass and for TVC.
    //
    // The variants beside off, plain (the product's defaults) and tvc are named sets of tuning keys (GtVariants): "before" is the pass before this package.
    // The grain reading (after the pictures of a view, outside the timing window): three raw frames of the held pose read one after the other, written at full size
    // (<variant>a / b / c), and measured over a fixed box of the picture that shows the clouds of the view (GtBox: the sky of the ground views, the deck of the view from above,
    // the whole height inside), on both sides of the vessel:
    //   T09.grain.<view>.tstd   temporal noise: the standard deviation of a pixel's luma over the three frames, mean over the box
    //   T09.grain.<view>.blob   spatial speckle: rms of (luma - its 5 x 5 mean) of frame a over the box
    //   T09.grain.<view>.lap    mean |3 x 3 Laplacian| of the luma of frame a over the box
    //   T09.grain.<view>.luma   the mean luma of the box, .frames_apart the frames between the first and the last read
    // The off variant gives the floor of every box (the scene without clouds); TVC has no baseline of its own (EVE's layers cannot be taken out of a running frame),
    // so the two systems are compared by the box values and the floor of the own start.
    internal sealed partial class T09Probe
    {
        internal struct GtVariant
        {
            internal string Name;
            internal KeyValuePair<string, float>[] Keys;
        }

        internal static readonly GtVariant[] GtVariants =
        {
            // The pass without the span cut: the type table's whole interval, 48 grid points, the history of the half-size march. (The "before" of the package also had a
            // new step offset per pixel and frame, which went with layers.ditherSequence.)
            new GtVariant { Name = "before", Keys = new[] { Key(OwnCloudOptions.TuneSpanTight, 0f), Key(OwnCloudOptions.TuneGridFine, 1f) } },
            // The cut span and the sequence with the full count of grid points (what the count alone costs).
            new GtVariant { Name = "full", Keys = new[] { Key(OwnCloudOptions.TuneGridFine, 1f) } },
            // The cut span and the sequence with a third of the grid points.
            new GtVariant { Name = "low", Keys = new[] { Key(OwnCloudOptions.TuneGridFine, 0.33f) } },
        };

        // Series K-T2d, the start gt57 (-atmoLook H): the same package in the fair-weather scene of the owner's complaint (the low cover of the look starts, where the
        // cumulus layer draws separate cloud bodies). Ground view and view from above: three frames each of the grain variants below (T09ProbeGrain.DiagLookG: rows
        // grain_tstd / grain_hfx over the pixels the clouds changed, spans), then 35 s cost windows of off / before / plain between "T09 view begin / end" lines.
        internal const double Look57MaxSeconds = 16 * 60;

        // The variants gnewramp (layers.entryRamp 2) of start 57 and the whole start gt58 (the surface march, layers.surfaceMarch) went with their keys (E40 step (c));
        // the start gt58 is out of K-T2d. The "before" of gt57 had also a new step offset per pixel and frame (layers.ditherSequence 0), which is gone.
        private static readonly KeyValuePair<string, float>[] Gt57Before =
        {
            Key(OwnCloudOptions.TuneAccumulate, 0.2f), Key(OwnCloudOptions.TuneSpanTight, 0f), Key(OwnCloudOptions.TuneGridFine, 1f)
        };
        private static readonly KeyValuePair<string, float>[] Gt57New = { Key(OwnCloudOptions.TuneSpanTight, 1f) };

        internal static readonly GrainVariant[] Gt57Variants =
        {
            new GrainVariant { Tag = "gbefore", Level = 1, HistoryFrames = 60, Keys = Gt57Before },
            new GrainVariant { Tag = "gnew", Level = 1, HistoryFrames = 60, Keys = Gt57New },
            new GrainVariant { Tag = "gnew10", Level = 1, HistoryFrames = 90, Keys = new[] { Key(OwnCloudOptions.TuneSpanTight, 1f), Key(OwnCloudOptions.TuneAccumulate, 0.1f) } },
        };

        private IEnumerable<ProbeWait> Look57Cover(string cover, double lat, double lon)
        {
            if (cover != "low") yield break;
            foreach (View view in new[] { Views[0], Views[2] })
            {
                foreach (ProbeWait w in PlaceChecked(lat, lon, view)) yield return w;
                string head = "T09d." + view.Name + "." + cover + ".";
                Emit(head + "placed_ok", placeOk);
                foreach (ProbeWait w in DiagLookG(view, cover, null, "goff", Gt57Variants)) yield return w;
                ClearForced();
                Game.SetLayerMask(0);
                foreach (ProbeWait w in Look55CostWindow(view, head, "off", NoLayersMask, null)) yield return w;
                foreach (ProbeWait w in Look55CostWindow(view, head, "before", 0, Gt57Before)) yield return w;
                foreach (ProbeWait w in Look55CostWindow(view, head, "plain", 0, Gt57New)) yield return w;
                ClearForced();
                Game.SetLayerMask(0);
                yield return ProbeWait.Frames(DiagSettleFrames);
            }
        }

        private static bool GtFind(string name, out GtVariant variant)
        {
            foreach (GtVariant v in GtVariants)
                if (v.Name == name) { variant = v; return true; }
            variant = default(GtVariant);
            return false;
        }

        // The starts of this package: no orbit view (its placement costs a minute per variant and holds no cloud detail), a grain reading in every other view.
        private bool GtStart
        {
            get { return StartId != null && StartId.StartsWith("gt", StringComparison.Ordinal); }
        }

        private bool GtApply(string name)
        {
            GtVariant v;
            if (!GtFind(name, out v)) return false;
            UseOwn();
            foreach (KeyValuePair<string, float> k in v.Keys) ForceTuning(k.Key, k.Value);
            return true;
        }

        // The box of a view as shares of the picture (x from the left, y from the bottom; the raw read's row 0 is the bottom row): two boxes, left and right of the vessel.
        internal static void GtBox(string view, out float y0, out float y1)
        {
            switch (view)
            {
                case "above": y0 = 0.05f; y1 = 0.60f; break;
                case "inside": y0 = 0.05f; y1 = 0.95f; break;
                default: y0 = 0.35f; y1 = 0.95f; break;
            }
        }

        internal struct GtGrain
        {
            internal int Pixels;
            internal double Tstd, Blob, Lap, Luma;
        }

        // The measures of the header over the two side boxes (x 0.04 to 0.42 and 0.58 to 0.96 of the width). frames: two or more raw pictures of one size.
        internal static bool MeasureGtGrain(IList<Color[]> frames, int width, int height, float y0, float y1, out GtGrain g)
        {
            g = new GtGrain();
            if (frames == null || frames.Count < 2 || width < 16 || height < 16) return false;
            int count = width * height;
            var lumas = new List<float[]>();
            foreach (Color[] f in frames)
            {
                if (f == null || f.Length != count) return false;
                lumas.Add(GrainLuma(f));
            }
            float[] first = lumas[0];
            int ya = Mathf.Clamp(Mathf.RoundToInt(y0 * height), 3, height - 4), yb = Mathf.Clamp(Mathf.RoundToInt(y1 * height), 3, height - 4);
            double tstd = 0.0, blob = 0.0, lap = 0.0, luma = 0.0;
            int n = 0;
            foreach (float[] xs in new[] { new[] { 0.04f, 0.42f }, new[] { 0.58f, 0.96f } })
            {
                int xa = Mathf.Clamp(Mathf.RoundToInt(xs[0] * width), 3, width - 4), xb = Mathf.Clamp(Mathf.RoundToInt(xs[1] * width), 3, width - 4);
                for (int y = ya; y < yb; y++)
                    for (int x = xa; x < xb; x++)
                    {
                        int i = y * width + x;
                        double mean = 0.0, sq = 0.0;
                        foreach (float[] l in lumas) { mean += l[i]; sq += (double)l[i] * l[i]; }
                        mean /= lumas.Count;
                        tstd += Math.Sqrt(Math.Max(sq / lumas.Count - mean * mean, 0.0));
                        double box = 0.0;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++) box += first[i + dy * width + dx];
                        double d = first[i] - box / 25.0;
                        blob += d * d;
                        lap += Math.Abs(4.0 * first[i] - first[i - 1] - first[i + 1] - first[i - width] - first[i + width]);
                        luma += first[i];
                        n++;
                    }
            }
            if (n == 0) return false;
            g.Pixels = n;
            g.Tstd = tstd / n;
            g.Blob = Math.Sqrt(blob / n);
            g.Lap = lap / n;
            g.Luma = luma / n;
            return true;
        }

        private IEnumerable<ProbeWait> GtGrainRead(View view, string variant)
        {
            if (picturesStopped || !viewPoseOk) yield break;
            if (Elapsed > MaxSeconds - PictureReserveS)
            {
                Warn("grain_time", view.Name + " " + variant + ": the scenario is at " + Elapsed.ToString("0") + " s of " + MaxSeconds.ToString("0") + " s; no grain read");
                yield break;
            }
            string what = view.Name + " " + variant + " grain";
            for (int i = 0; i < 12 && Game.HoldReached && !Game.HoldStable; i++) yield return ProbeWait.Frames(5);
            T09Pose before = Game.ReadPose();
            var raws = new List<Color[]>();
            var images = new List<T09Image>();
            int firstFrame = 0, lastFrame = 0;
            foreach (string letter in new[] { "a", "b", "c" })
            {
                foreach (ProbeWait w in ReadRawFrame(what + " " + letter)) yield return w;
                if (noiseImage == null) yield break;
                if (raws.Count == 0) firstFrame = noiseBeginFrame;
                lastFrame = noiseBeginFrame;
                raws.Add(noiseImage.Raw);
                images.Add(noiseImage);
            }
            T09Pose after = Game.ReadPose();
            string poseProblem = PoseProblem(view, before) ?? PoseProblem(view, after);
            if (poseProblem != null)
            {
                Warn("grain_pose", what + ": " + poseProblem + "; no grain rows");
                yield break;
            }
            float y0, y1;
            GtBox(view.Name, out y0, out y1);
            GtGrain g;
            string prefix = "T09.grain." + view.Name + ".";
            if (!MeasureGtGrain(raws, images[0].Width, images[0].Height, y0, y1, out g))
            {
                Warn("grain_measure", what + ": the three frames could not be measured");
                yield break;
            }
            Emit(prefix + "tstd", g.Tstd);
            Emit(prefix + "blob", g.Blob);
            Emit(prefix + "lap", g.Lap);
            Emit(prefix + "luma", g.Luma);
            Emit(prefix + "frames_apart", lastFrame - firstFrame);
            // The altitude intervals the pass searched in its last frame (LayerRenderer.SpanStats: "none", "table a-b" or "a-b" per layer).
            OwnCloudModule own = OwnCloudModule.Current;
            if (own != null && own.Renderer != null && IsOwn(variant))
            {
                string spans = own.Renderer.SpanStats;
                EmitText(prefix + "spans", spans);
                AtmospheresLog.Say("T09 grain spans: " + view.Name + " " + variant + ": " + spans + ", grid points " + own.Frame.MaxSteps);
            }
            AtmospheresLog.Say("T09 grain: " + view.Name + " " + variant + ": tstd " + g.Tstd.ToString("0.00000", CultureInfo.InvariantCulture) + ", blob "
                               + g.Blob.ToString("0.00000", CultureInfo.InvariantCulture) + ", lap " + g.Lap.ToString("0.00000", CultureInfo.InvariantCulture) + ", luma "
                               + g.Luma.ToString("0.000", CultureInfo.InvariantCulture) + " over " + g.Pixels + " px, " + (lastFrame - firstFrame) + " frame(s) from first to last");
            string[] letters = { "a", "b", "c" };
            for (int k = 0; k < images.Count; k++)
            {
                string name = T09Pictures.Name(StartId, view.Name, variant + letters[k], Game.ScreenWidth, Game.ScreenHeight, T09Pictures.Natural, false);
                try
                {
                    picturesBytes += Game.SavePicture(name, images[k], 1);
                    picturesSaved++;
                }
                catch (Exception e)
                {
                    Warn("picture_not_saved", name + ".png: " + e.GetType().Name + " " + e.Message);
                }
            }
        }
    }
}
