using System;

namespace ReDefinition.Atmospheres
{
    // The numbers of the edge-adaptive resolution of the cloud pass (package T12, the plan's T05b), without any engine type so that the
    // tests can run them. The coarse march keeps one ray per 4 x 4 block; blocks that hold a cloud contour (a gradient of the alpha between
    // neighbouring samples, or in the last picture) march a 2 x 2 pattern of rays with the entry refinement on, the rest of the picture does not.
    internal sealed class EdgeSettings
    {
        // Tuning keys (ATMO_TUNING { key = value }, or a matrix row atmo.<key>); all default off or at the sweep's pick.
        internal const string TuneEdgeAdaptive = "layers.edgeAdaptive", TuneDilation = "layers.edgeMaskDilation", TuneStepBand = "layers.edgeStepBand",
            TuneReconstructor = "layers.reconstructorEnabled", TuneThreshold = "layers.edgeThreshold", TunePersist = "layers.edgePersist";

        // The compiled defaults. The reconstruction and the edge mode are off until the owner agrees (they reduce quality against the full pass).
        // The step band is off too: with it the coarse draw has no entry refinement and thick or grazing interiors (orbit edge, interior error
        // 0.35, seam 0.16) get a visible dark patch with a hard border; it stays a switch for the views where it passes (review T12/T13, B2).
        internal const bool AdaptiveDefault = false, ReconstructorDefault = false, StepBandDefault = false;
        // The mask's numbers as the sweep of the verification picked them (see tools/weather_metrics/edge.py, the three sweeps).
        internal const float ThresholdDefault = 0.06f;
        internal const int DilationDefault = 1, PersistDefault = 4;
        // The block of the coarse pattern, the block of the edge pattern, in pixels.
        internal const int CoarseBlock = 4, EdgeBlock = 2;
        // The most blocks the mask is widened by.
        internal const int MaxDilation = 8;

        public bool Enabled;
        // The alpha range between a block's sample and its four neighbours (or over the block in the last picture) above which the block is an edge.
        public float Threshold = ThresholdDefault;
        // The blocks the edge mask is widened by on every side, besides the widening of the picture's motion.
        public int Dilation = DilationDefault;
        // The entry refinement runs in the edge blocks only (true), or in every block (false; only meaningful with the refinement on).
        public bool StepBand = StepBandDefault;
        // Frames a block stays an edge after its last detection (the mask's hysteresis: a block that flips between the two patterns shimmers).
        public int Persist = PersistDefault;

        // The values of the tuning keys. get returns the key's number or the default it is given.
        internal static EdgeSettings From(Func<string, float, float> get)
        {
            return new EdgeSettings
            {
                Enabled = get(TuneEdgeAdaptive, AdaptiveDefault ? 1f : 0f) > 0.5f,
                Threshold = Math.Max(0.001f, get(TuneThreshold, ThresholdDefault)),
                Dilation = Math.Max(0, Math.Min(MaxDilation, (int)get(TuneDilation, DilationDefault))),
                StepBand = get(TuneStepBand, StepBandDefault ? 1f : 0f) > 0.5f,
                Persist = Math.Max(1, Math.Min(32, (int)get(TunePersist, PersistDefault)))
            };
        }

        internal static bool ReconstructorOn(Func<string, float, float> get)
        {
            return get(TuneReconstructor, ReconstructorDefault ? 1f : 0f) > 0.5f;
        }
    }

    // The CPU model of the mask: what the shader ReDefinitionAtmospheresEdge.shader does per block, as plain arrays.
    internal static class EdgeMath
    {
        // The blocks of an image of that size (the coarse pattern's grid).
        internal static int Blocks(int pixels)
        {
            return (pixels + EdgeSettings.CoarseBlock - 1) / EdgeSettings.CoarseBlock;
        }

        // The alpha range of the block's own sample and its four neighbours (the plus shape; samples beyond the grid repeat the edge's).
        internal static float PlusRange(float[,] alpha, int x, int y)
        {
            int w = alpha.GetLength(0), h = alpha.GetLength(1);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int k = 0; k < 5; k++)
            {
                int dx = k == 1 ? -1 : (k == 2 ? 1 : 0), dy = k == 3 ? -1 : (k == 4 ? 1 : 0);
                float a = alpha[Math.Max(0, Math.Min(w - 1, x + dx)), Math.Max(0, Math.Min(h - 1, y + dy))];
                lo = Math.Min(lo, a);
                hi = Math.Max(hi, a);
            }
            return hi - lo;
        }

        // The block classification from the coarse samples' alpha: true where the plus-shaped range exceeds the threshold.
        internal static bool[,] Classify(float[,] coarseAlpha, float threshold)
        {
            int w = coarseAlpha.GetLength(0), h = coarseAlpha.GetLength(1);
            var edge = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    edge[x, y] = PlusRange(coarseAlpha, x, y) > threshold;
            return edge;
        }

        // The mask widened by r blocks on every side (a square structuring element, separable like the shader's two passes).
        internal static bool[,] Dilate(bool[,] mask, int r)
        {
            int w = mask.GetLength(0), h = mask.GetLength(1);
            if (r <= 0) return (bool[,])mask.Clone();
            var tmp = new bool[w, h];
            var outMask = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int d = -r; d <= r && !tmp[x, y]; d++)
                        if (x + d >= 0 && x + d < w && mask[x + d, y]) tmp[x, y] = true;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int d = -r; d <= r && !outMask[x, y]; d++)
                        if (y + d >= 0 && y + d < h && tmp[x, y + d]) outMask[x, y] = true;
            return outMask;
        }

        // The blocks the mask is widened by in a frame: the setting's, and one more for every four pixels the picture moved since the last frame
        // (a camera turn or the cloud's wind), so that an edge that moves is never at a block that is still coarse.
        internal static int DilationFor(int baseDilation, float motionPixels)
        {
            int extra = (int)Math.Ceiling(Math.Max(0f, motionPixels) / EdgeSettings.CoarseBlock);
            return Math.Max(0, Math.Min(EdgeSettings.MaxDilation, baseDilation + extra));
        }

        // The state of a block's hysteresis after a frame: 1 where the block was detected, otherwise the previous level less one frame's share.
        internal static float Persist(float previous, bool detected, int frames)
        {
            return detected ? 1f : Math.Max(0f, previous - 1f / Math.Max(1, frames));
        }

        // The narrowest band around a contour that the mask holds from the coarse samples of one frame, in pixels, in the worst frame: the contour
        // lies between two samples, the plus shape flags both their blocks, and the sample can stand in the block's corner next to the contour, so
        // that only a pixel of the band is certain; every dilation block adds 4 pixels to each side. The history and the persistence add to it.
        internal static int GuaranteedBandPixels(int dilation)
        {
            return 1 + EdgeSettings.CoarseBlock * Math.Max(0, dilation);
        }

        // The smallest dilation whose guaranteed band holds the width.
        internal static int DilationForBand(int bandPixels)
        {
            return Math.Max(0, (bandPixels - 1 + EdgeSettings.CoarseBlock - 1) / EdgeSettings.CoarseBlock);
        }

        // The rays one block of the pattern marches in a frame: one at the coarse pattern, (4 / 2)^2 at the edge pattern.
        internal static int RaysPerBlock(bool edge)
        {
            int n = EdgeSettings.CoarseBlock / (edge ? EdgeSettings.EdgeBlock : EdgeSettings.CoarseBlock);
            return n * n;
        }

        // The share of the picture's rays the pattern marches at an edge share: 1 / 16 coarse, 1 / 4 in the edge blocks.
        internal static double RayShare(double edgeShare)
        {
            double coarse = 1.0 / (EdgeSettings.CoarseBlock * EdgeSettings.CoarseBlock), fine = 1.0 / (EdgeSettings.EdgeBlock * EdgeSettings.EdgeBlock);
            return (1.0 - edgeShare) * coarse + edgeShare * (coarse + fine);
        }

        // The step band: the refinement budget of a march in a block. Only an edge block refines when the band is on; without the band every block does.
        internal static float RefineBudget(float budget, bool edgeBlock, bool stepBand)
        {
            return !stepBand || edgeBlock ? budget : 0f;
        }
    }

    // Detects the cuts after which the reconstruction has no history: a teleport (the camera moved further than a frame of flight can),
    // a scene or camera change, a request through ReDefinition.Api (Frame.RequestHistoryReset), a weather cut (WeatherCutDetector) and a
    // change of the layer stack. Plain numbers, so that the tests run it without the game.
    internal sealed class ReconCutDetector
    {
        // The distance in m that one frame may move the camera without being a cut, at the speed it had; the floor covers a standing start.
        internal const float JumpFloorM = 2000f, JumpPerSpeed = 4f;

        private bool have;
        private Vector3d previous;
        private float lastStep;
        private int sceneId = int.MinValue, apiResets, weatherCuts, stackVersion = -1;

        internal struct Vector3d
        {
            public double X, Y, Z;
        }

        // Observes a frame; returns the reason of the cut, or null where the history stays. position is planet-relative, the counters rise
        // by one with every request or cut seen by the caller (the API's, the weather cut detector's), stack is the layer stack's version.
        internal string Observe(double px, double py, double pz, int scene, int apiResetCount, int weatherCutCount, int stack)
        {
            string reason = null;
            if (have)
            {
                double dx = px - previous.X, dy = py - previous.Y, dz = pz - previous.Z;
                float step = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (step > Math.Max(JumpFloorM, JumpPerSpeed * lastStep)) reason = "teleport";
                lastStep = reason == null ? step : 0f;
            }
            if (have && scene != sceneId) reason = "scene change";
            if (have && apiResetCount != apiResets) reason = "history reset request";
            if (have && weatherCutCount != weatherCuts) reason = "weather cut";
            if (have && stack != stackVersion) reason = "layer stack change";
            have = true;
            previous = new Vector3d { X = px, Y = py, Z = pz };
            sceneId = scene;
            apiResets = apiResetCount;
            weatherCuts = weatherCutCount;
            stackVersion = stack;
            return reason;
        }

        internal void Forget()
        {
            have = false;
            lastStep = 0f;
        }
    }
}
