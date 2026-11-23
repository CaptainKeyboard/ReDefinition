using System;

namespace ReDefinition.Atmospheres
{
    // The frame's share of the step offset of the layer march (the shader's _DitherP.x). Every pixel keeps one spatial offset and every frame adds the same phase to it, so
    // that the history of the pass sees each pixel's offsets spread over the grid step: the golden-ratio sequence (Roberts 2018, the R1 sequence). E40 L4 clean: the other
    // sequences (van der Corput, shuffled strata, white, golden per block) and the gradient noise's per-frame shift (layers.ditherPhase, layers.ditherShift) are gone; the
    // weave measurement gave no case that separated from the default (E40-weave-march-result.md).
    internal static class StepPhase
    {
        private const double GoldenRatio = 0.6180339887498949;

        // The phase of a frame, in [0, 1).
        internal static double Of(int frame)
        {
            if (frame < 0) frame = 0;
            double x = frame * GoldenRatio;
            return x - Math.Floor(x);
        }

        // The phase of a march under the history rotation (recon.rotate, an N x N block: cycle = N * N): a pixel is marched once per cycle, so the sequence counts the
        // pixel's own samples (index / cycle) and each pixel's successive offsets step by the golden ratio, as every pixel's do without the rotation. The frame's own phase
        // (Of(index)) stepped a pixel's samples by frac(16 x 0.618) = 0.888 at 4 x 4: its last few samples covered a third of the step, and each cell of the block kept a
        // bias of its own, the fine square grid of the clouds seen from far (PRUEFPROTOKOLL 137). All pixels of one cycle share the phase; their own blue-noise offsets differ.
        internal static double OfSample(int index, int cycle)
        {
            if (index < 0) index = 0;
            return Of(index / Math.Max(cycle, 1));
        }
    }
}
